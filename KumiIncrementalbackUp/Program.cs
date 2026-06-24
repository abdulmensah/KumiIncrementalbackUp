using System;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Azure.Cosmos;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.ApplicationInsights.TelemetryConverters;
using KumiIncrementalbackUp.Configuration;
using KumiIncrementalbackUp.Models;
using KumiIncrementalbackUp.Services;

namespace KumiIncrementalbackUp
{

    // 2. Main Application Flow
    class Program
    {
        static async Task Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .CreateLogger();

            try
            {
                BackupAppSettings settings = BackupAppSettings.Load();
                Log.Logger = CreateLogger(settings);

                BackupScheduleOptions scheduleOptions = BackupScheduleOptions.From(args, settings.Schedule);
                using var shutdown = new CancellationTokenSource();

                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    shutdown.Cancel();
                    Log.Warning("Shutdown requested. Waiting for the current backup step to stop.");
                };

                try
                {
                    if (scheduleOptions.IsEnabled)
                    {
                        await RunScheduledBackupsAsync(settings, scheduleOptions, shutdown.Token);
                    }
                    else
                    {
                        await RunBackupJobAsync(settings, shutdown.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.Information("Backup scheduler stopped.");
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Backup application terminated unexpectedly.");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static async Task RunScheduledBackupsAsync(BackupAppSettings settings, BackupScheduleOptions scheduleOptions, CancellationToken cancellationToken)
        {
            Log.Information("Backup scheduler enabled. Interval: {Interval}.", scheduleOptions.Interval);
            Log.Information("Press Ctrl+C to stop the scheduler.");

            while (!cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset startedAt = DateTimeOffset.Now;
                Log.Information("Scheduled backup started at {StartedAt}.", startedAt);

                await RunBackupJobAsync(settings, cancellationToken);

                DateTimeOffset nextRun = DateTimeOffset.Now.Add(scheduleOptions.Interval);
                Log.Information("Next backup scheduled for {NextRun}.", nextRun);

                using var timer = new PeriodicTimer(scheduleOptions.Interval);
                await timer.WaitForNextTickAsync(cancellationToken);
            }
        }

        private static async Task RunBackupJobAsync(BackupAppSettings settings, CancellationToken cancellationToken)
        {
            DateTimeOffset jobStartedAt = DateTimeOffset.UtcNow;
            Log.Information("Azure File Share backup with USB source started.");

            try
            {
                // Step 0: Detect and list USB devices
                string sourceUsbDrive = settings.Backup.SourceUsbDrive;
                Log.Information("Backup job initializing for source device {SourceUsbDevice}.", sourceUsbDrive);

                // Initialize Cosmos Client
                using CosmosClient cosmosClient = new CosmosClient(settings.CosmosDb.EndpointUri, settings.CosmosDb.PrimaryKey, new CosmosClientOptions
                {
                    SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase }
                });

                // Initialize Azure File Share Service
                var fileShareService = new AzureFileShareBackupService(
                    settings.AzureFileShare.ConnectionString,
                    settings.AzureFileShare.ShareName,
                    settings.AzureFileShare.DirectoryName);

                // Ensure DB and Container exist
                Database database = await cosmosClient.CreateDatabaseIfNotExistsAsync(settings.CosmosDb.DatabaseId);
                Container container = await database.CreateContainerIfNotExistsAsync(settings.CosmosDb.ContainerId, "/jobId");

                // Create unique Job ID
                string currentJobId = $"job-{Guid.NewGuid().ToString().Substring(0, 8)}";
                Log.Information("Backup job {JobId} started for source device {SourceUsbDevice}.", currentJobId, sourceUsbDrive);

                // Step 1: Discover files from USB device
                Log.Information("Discovering files from source device for backup job {JobId}.", currentJobId);
                var fileDiscoveryService = new FileDiscoveryService(sourceUsbDrive);
                var filesToBackup = await fileDiscoveryService.DiscoverFilesAsync();
                cancellationToken.ThrowIfCancellationRequested();

                if (filesToBackup.Count == 0)
                {
                    Log.Warning("Backup job {JobId} found no files matching the backup criteria.", currentJobId);
                    return;
                }

                Log.Information("Backup job {JobId} discovered {FileCount} files.", currentJobId, filesToBackup.Count);

                // Calculate total size
                long totalSizeToBackup = filesToBackup.Sum(f => f.FileSizeInBytes);
                Log.Information("Backup job {JobId} discovered {TotalSizeBytes} bytes ({TotalSize}) to evaluate.", currentJobId, totalSizeToBackup, FormatFileSize(totalSizeToBackup));

                Log.Information("Evaluating files for incremental backup for job {JobId}.", currentJobId);
                var filesSelectedForBackup = new List<DiscoveredFile>();
                int skippedCount = 0;

                foreach (var fileInfo in filesToBackup)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    LogAuditEvent(currentJobId, sourceUsbDrive, fileInfo, "Discovered", "File discovered during source scan.");
                    FileBackupState? previousBackup = await GetLatestCompletedBackupAsync(container, sourceUsbDrive, fileInfo.RelativePath);

                    bool isUnchanged = previousBackup is not null
                        && previousBackup.FileSizeInBytes == fileInfo.FileSizeInBytes
                        && previousBackup.LastModifiedUtc == fileInfo.LastModifiedUtc
                        && string.Equals(previousBackup.ContentHash, fileInfo.ContentHash, StringComparison.OrdinalIgnoreCase);

                    var initialState = new FileBackupState
                    {
                        Id = CreateDocumentId("state", $"{currentJobId}|{fileInfo.RelativePath}"),
                        JobId = currentJobId,
                        FileName = fileInfo.FileName,
                        LocalPath = fileInfo.FilePath,
                        RelativePath = fileInfo.RelativePath,
                        CurrentState = isUnchanged ? "Skipped" : "Pending",
                        BackupAction = isUnchanged ? "SkippedUnchanged" : previousBackup is null ? "New" : "Modified",
                        FileSizeInBytes = fileInfo.FileSizeInBytes,
                        BytesTransferred = isUnchanged ? fileInfo.FileSizeInBytes : 0,
                        LastModifiedUtc = fileInfo.LastModifiedUtc,
                        ContentHash = fileInfo.ContentHash,
                        RemoteFileSharePath = previousBackup?.RemoteFileSharePath,
                        SourceUsbDevice = sourceUsbDrive,
                        PreviousJobId = previousBackup?.JobId
                    };

                    await container.UpsertItemAsync(initialState, new PartitionKey(currentJobId));

                    if (isUnchanged)
                    {
                        skippedCount++;
                        LogAuditEvent(currentJobId, sourceUsbDrive, fileInfo, "Skipped", "File is unchanged from the latest completed backup.", previousBackup);
                    }
                    else
                    {
                        filesSelectedForBackup.Add(fileInfo);
                        string action = previousBackup is null ? "new file" : "modified file";
                        LogAuditEvent(currentJobId, sourceUsbDrive, fileInfo, "Pending", $"Queued as {action}.", previousBackup);
                    }
                }

                // Step 3: Process files and upload to Azure File Share
                Log.Information("Uploading {FileCount} selected files to Azure File Share for job {JobId}.", filesSelectedForBackup.Count, currentJobId);
                int successCount = 0;
                int failureCount = 0;
                long totalBackedUp = 0;

                foreach (var fileInfo in filesSelectedForBackup)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string fileId = CreateDocumentId("state", $"{currentJobId}|{fileInfo.RelativePath}");

                    Log.Information("Processing file {RelativePath} for backup job {JobId}.", fileInfo.RelativePath, currentJobId);

                    try
                    {
                        // Update state to Processing
                        List<PatchOperation> processingPatches = new List<PatchOperation>
                        {
                            PatchOperation.Replace("/currentState", "Processing"),
                            PatchOperation.Replace("/lastUpdated", DateTimeOffset.UtcNow)
                        };
                        await container.PatchItemAsync<FileBackupState>(fileId, new PartitionKey(currentJobId), processingPatches);
                        LogAuditEvent(currentJobId, sourceUsbDrive, fileInfo, "Processing", "Upload started.");

                        // Upload to Azure File Share
                        var loggedProgressMilestones = new HashSet<int>();
                        var uploadResult = await fileShareService.UploadFileAsync(fileInfo.FilePath, fileInfo.RelativePath, async (uploaded, total) =>
                        {
                            int percentComplete = total == 0 ? 100 : (int)((uploaded * 100) / total);

                            // Update progress in Cosmos DB (less frequent to avoid throttling)
                            if (percentComplete % 10 == 0)
                            {
                                List<PatchOperation> progressPatches = new List<PatchOperation>
                                {
                                    PatchOperation.Replace("/bytesTransferred", uploaded),
                                    PatchOperation.Replace("/lastUpdated", DateTimeOffset.UtcNow)
                                };
                                await container.PatchItemAsync<FileBackupState>(fileId, new PartitionKey(currentJobId), progressPatches);
                            }

                            int progressMilestone = Math.Min(100, (percentComplete / 10) * 10);
                            if (progressMilestone > 0 && loggedProgressMilestones.Add(progressMilestone))
                            {
                                Log.Information(
                                    "Upload progress for file {RelativePath} in job {JobId}: {PercentComplete}% ({UploadedBytes}/{TotalBytes} bytes).",
                                    fileInfo.RelativePath,
                                    currentJobId,
                                    progressMilestone,
                                    uploaded,
                                    total);
                            }
                        });

                        if (uploadResult.IsSuccess)
                        {
                            // Update to Completed
                            List<PatchOperation> completedPatches = new List<PatchOperation>
                            {
                                PatchOperation.Replace("/bytesTransferred", fileInfo.FileSizeInBytes),
                                PatchOperation.Replace("/currentState", "Completed"),
                                PatchOperation.Replace("/remoteFileSharePath", uploadResult.RemoteFilePath),
                                PatchOperation.Replace("/lastUpdated", DateTimeOffset.UtcNow)
                            };
                            await container.PatchItemAsync<FileBackupState>(fileId, new PartitionKey(currentJobId), completedPatches);
                            LogAuditEvent(currentJobId, sourceUsbDrive, fileInfo, "Completed", "Upload completed successfully.", remoteFileSharePath: uploadResult.RemoteFilePath, bytesTransferred: fileInfo.FileSizeInBytes);
                            successCount++;
                            totalBackedUp += fileInfo.FileSizeInBytes;
                        }
                        else
                        {
                            throw new Exception(uploadResult.ErrorMessage);
                        }
                    }
                    catch (Exception ex)
                    {
                        List<PatchOperation> failedPatches = new List<PatchOperation>
                        {
                            PatchOperation.Replace("/currentState", "Failed"),
                            PatchOperation.Replace("/failureReason", ex.Message),
                            PatchOperation.Replace("/lastUpdated", DateTimeOffset.UtcNow)
                        };
                        await container.PatchItemAsync<FileBackupState>(fileId, new PartitionKey(currentJobId), failedPatches);
                        LogAuditEvent(currentJobId, sourceUsbDrive, fileInfo, "Failed", ex.Message);
                        failureCount++;
                    }
                }

                // Step 5: Generate final report
                Log.Information(
                    "Backup job {JobId} completed. SourceUsbDevice={SourceUsbDevice}, TotalFiles={TotalFiles}, SkippedUnchanged={SkippedUnchanged}, UploadedOrChanged={UploadedOrChanged}, Successful={Successful}, Failed={Failed}, TotalBackedUpBytes={TotalBackedUpBytes}, TotalBackedUp={TotalBackedUp}, UploadSuccessRate={UploadSuccessRate}, DurationMs={DurationMs}.",
                    currentJobId,
                    sourceUsbDrive,
                    filesToBackup.Count,
                    skippedCount,
                    filesSelectedForBackup.Count,
                    successCount,
                    failureCount,
                    totalBackedUp,
                    FormatFileSize(totalBackedUp),
                    filesSelectedForBackup.Count == 0 ? 100 : successCount * 100.0 / filesSelectedForBackup.Count,
                    (DateTimeOffset.UtcNow - jobStartedAt).TotalMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Backup job failed after {DurationMs} ms.", (DateTimeOffset.UtcNow - jobStartedAt).TotalMilliseconds);
            }
        }

        /// <summary>
        /// Formats file size in human-readable format
        /// </summary>
        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }

        private static async Task<FileBackupState?> GetLatestCompletedBackupAsync(Container container, string sourceUsbDevice, string relativePath)
        {
            var query = new QueryDefinition(
                "SELECT TOP 1 * FROM c WHERE c.documentType = 'FileState' AND c.sourceUsbDevice = @sourceUsbDevice AND c.relativePath = @relativePath AND c.currentState = 'Completed' ORDER BY c.lastUpdated DESC")
                .WithParameter("@sourceUsbDevice", sourceUsbDevice)
                .WithParameter("@relativePath", relativePath);

            using FeedIterator<FileBackupState> iterator = container.GetItemQueryIterator<FileBackupState>(
                query,
                requestOptions: new QueryRequestOptions
                {
                    MaxItemCount = 1
                });

            while (iterator.HasMoreResults)
            {
                FeedResponse<FileBackupState> response = await iterator.ReadNextAsync();
                FileBackupState? latestBackup = response.FirstOrDefault();
                if (latestBackup is not null)
                {
                    return latestBackup;
                }
            }

            return null;
        }

        private static void LogAuditEvent(
            string jobId,
            string sourceUsbDevice,
            DiscoveredFile fileInfo,
            string eventType,
            string message,
            FileBackupState? previousBackup = null,
            string? remoteFileSharePath = null,
            long bytesTransferred = 0)
        {
            var auditEvent = new BackupAuditEvent
            {
                Id = CreateDocumentId("audit", $"{jobId}|{fileInfo.RelativePath}|{eventType}|{Guid.NewGuid()}"),
                JobId = jobId,
                EventType = eventType,
                FileName = fileInfo.FileName,
                LocalPath = fileInfo.FilePath,
                RelativePath = fileInfo.RelativePath,
                RemoteFileSharePath = remoteFileSharePath ?? previousBackup?.RemoteFileSharePath,
                SourceUsbDevice = sourceUsbDevice,
                Message = message,
                PreviousJobId = previousBackup?.JobId,
                FileSizeInBytes = fileInfo.FileSizeInBytes,
                BytesTransferred = bytesTransferred
            };

            Log.ForContext("AuditEventId", auditEvent.Id)
                .ForContext("AuditEventType", auditEvent.EventType)
                .ForContext("JobId", auditEvent.JobId)
                .ForContext("FileName", auditEvent.FileName)
                .ForContext("LocalPath", auditEvent.LocalPath)
                .ForContext("RelativePath", auditEvent.RelativePath)
                .ForContext("RemoteFileSharePath", auditEvent.RemoteFileSharePath)
                .ForContext("SourceUsbDevice", auditEvent.SourceUsbDevice)
                .ForContext("PreviousJobId", auditEvent.PreviousJobId)
                .ForContext("FileSizeInBytes", auditEvent.FileSizeInBytes)
                .ForContext("BytesTransferred", auditEvent.BytesTransferred)
                .ForContext("DocumentType", auditEvent.DocumentType)
                .Write(GetAuditLogLevel(eventType), "Backup audit event {AuditEventType}: {AuditMessage}", auditEvent.EventType, auditEvent.Message);
        }

        private static string CreateDocumentId(string prefix, string value)
        {
            byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return $"{prefix}-{Convert.ToHexString(hashBytes).ToLowerInvariant()}";
        }

        private static Serilog.Core.Logger CreateLogger(BackupAppSettings settings)
        {
            LogEventLevel minimumLevel = ParseMinimumLevel(settings.AuditTelemetry.MinimumLevel);
            string logFilePath = string.IsNullOrWhiteSpace(settings.AuditTelemetry.LogFilePath)
                ? "logs/backup-audit-.log"
                : settings.AuditTelemetry.LogFilePath;

            var loggerConfiguration = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .Enrich.WithProperty("Application", "KumiIncrementalbackUp")
                .Enrich.WithProperty("AuditSource", "UsbAzureFileShareBackup")
                .WriteTo.Console()
                .WriteTo.File(
                    new CompactJsonFormatter(),
                    logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 31,
                    shared: true);

            if (!string.IsNullOrWhiteSpace(settings.AuditTelemetry.ApplicationInsightsConnectionString))
            {
                loggerConfiguration.WriteTo.ApplicationInsights(
                    settings.AuditTelemetry.ApplicationInsightsConnectionString,
                    TelemetryConverter.Traces);
            }

            return loggerConfiguration.CreateLogger();
        }

        private static LogEventLevel GetAuditLogLevel(string eventType)
        {
            return eventType switch
            {
                "Failed" => LogEventLevel.Error,
                "Skipped" => LogEventLevel.Warning,
                _ => LogEventLevel.Information
            };
        }

        private static LogEventLevel ParseMinimumLevel(string minimumLevel)
        {
            return Enum.TryParse(minimumLevel, ignoreCase: true, out LogEventLevel parsedLevel)
                ? parsedLevel
                : LogEventLevel.Information;
        }
    }
}
