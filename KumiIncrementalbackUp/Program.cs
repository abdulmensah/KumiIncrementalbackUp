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
                    Console.WriteLine("\nShutdown requested. Waiting for the current backup step to stop...");
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
                    Console.WriteLine("Backup scheduler stopped.");
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Backup application terminated unexpectedly.");
                Console.WriteLine($"\nFatal Error: {ex.Message}");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static async Task RunScheduledBackupsAsync(BackupAppSettings settings, BackupScheduleOptions scheduleOptions, CancellationToken cancellationToken)
        {
            Log.Information("Backup scheduler enabled. Interval: {Interval}.", scheduleOptions.Interval);
            Console.WriteLine($"Backup scheduler enabled. Interval: {scheduleOptions.Interval}.");
            Console.WriteLine("Press Ctrl+C to stop the scheduler.\n");

            while (!cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset startedAt = DateTimeOffset.Now;
                Log.Information("Scheduled backup started at {StartedAt}.", startedAt);
                Console.WriteLine($"Scheduled backup started at {startedAt:yyyy-MM-dd HH:mm:ss zzz}.");

                await RunBackupJobAsync(settings, cancellationToken);

                DateTimeOffset nextRun = DateTimeOffset.Now.Add(scheduleOptions.Interval);
                Log.Information("Next backup scheduled for {NextRun}.", nextRun);
                Console.WriteLine($"Next backup scheduled for {nextRun:yyyy-MM-dd HH:mm:ss zzz}.\n");

                using var timer = new PeriodicTimer(scheduleOptions.Interval);
                await timer.WaitForNextTickAsync(cancellationToken);
            }
        }

        private static async Task RunBackupJobAsync(BackupAppSettings settings, CancellationToken cancellationToken)
        {
            DateTimeOffset jobStartedAt = DateTimeOffset.UtcNow;
            Console.WriteLine("=== Azure File Share Backup with USB 3 Source ===\n");

            try
            {
                // Step 0: Detect and list USB devices
                Console.WriteLine("Step 0: Detecting USB devices...\n");
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
                Console.WriteLine($"Backup Job ID: {currentJobId}\n");

                // Step 1: Discover files from USB device
                Console.WriteLine("Step 1: Discovering files from USB device...\n");
                var fileDiscoveryService = new FileDiscoveryService(sourceUsbDrive);
                var filesToBackup = await fileDiscoveryService.DiscoverFilesAsync();
                cancellationToken.ThrowIfCancellationRequested();

                if (filesToBackup.Count == 0)
                {
                    Log.Warning("Backup job {JobId} found no files matching the backup criteria.", currentJobId);
                    Console.WriteLine("✗ No files found matching the backup criteria.\n");
                    return;
                }

                Log.Information("Backup job {JobId} discovered {FileCount} files.", currentJobId, filesToBackup.Count);
                Console.WriteLine($"\n✓ Discovered {filesToBackup.Count} files to backup\n");

                // Calculate total size
                long totalSizeToBackup = filesToBackup.Sum(f => f.FileSizeInBytes);
                Log.Information("Backup job {JobId} discovered {TotalSizeBytes} bytes to evaluate.", currentJobId, totalSizeToBackup);
                Console.WriteLine($"Total data size: {FormatFileSize(totalSizeToBackup)}\n");

                Console.WriteLine("Step 2: Evaluating files for incremental backup...\n");
                var filesSelectedForBackup = new List<DiscoveredFile>();
                int skippedCount = 0;

                foreach (var fileInfo in filesToBackup)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await WriteAuditEventAsync(container, currentJobId, sourceUsbDrive, fileInfo, "Discovered", "File discovered during source scan.");
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
                        await WriteAuditEventAsync(container, currentJobId, sourceUsbDrive, fileInfo, "Skipped", "File is unchanged from the latest completed backup.", previousBackup);
                        Console.WriteLine($" → {fileInfo.RelativePath} ({FormatFileSize(fileInfo.FileSizeInBytes)}) [Skipped unchanged]");
                    }
                    else
                    {
                        filesSelectedForBackup.Add(fileInfo);
                        string action = previousBackup is null ? "new file" : "modified file";
                        await WriteAuditEventAsync(container, currentJobId, sourceUsbDrive, fileInfo, "Pending", $"Queued as {action}.", previousBackup);
                        Console.WriteLine($" → {fileInfo.RelativePath} ({FormatFileSize(fileInfo.FileSizeInBytes)}) [Pending: {action}]");
                    }
                }

                // Step 3: Process files and upload to Azure File Share
                Console.WriteLine("\nStep 3: Uploading files to Azure File Share...\n");
                int successCount = 0;
                int failureCount = 0;
                long totalBackedUp = 0;

                foreach (var fileInfo in filesSelectedForBackup)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string fileId = CreateDocumentId("state", $"{currentJobId}|{fileInfo.RelativePath}");

                    Console.WriteLine($"\nProcessing: {fileInfo.RelativePath}");

                    try
                    {
                        // Update state to Processing
                        List<PatchOperation> processingPatches = new List<PatchOperation>
                        {
                            PatchOperation.Replace("/currentState", "Processing"),
                            PatchOperation.Replace("/lastUpdated", DateTimeOffset.UtcNow)
                        };
                        await container.PatchItemAsync<FileBackupState>(fileId, new PartitionKey(currentJobId), processingPatches);
                        await WriteAuditEventAsync(container, currentJobId, sourceUsbDrive, fileInfo, "Processing", "Upload started.");

                        // Upload to Azure File Share
                        var uploadResult = await fileShareService.UploadFileAsync(fileInfo.FilePath, fileInfo.RelativePath, async (uploaded, total) =>
                        {
                            int percentComplete = total == 0 ? 100 : (int)((uploaded * 100) / total);
                            Console.Write($"\r → Progress: {uploaded}/{total} bytes ({percentComplete}%)");

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
                            await WriteAuditEventAsync(container, currentJobId, sourceUsbDrive, fileInfo, "Completed", "Upload completed successfully.", remoteFileSharePath: uploadResult.RemoteFilePath, bytesTransferred: fileInfo.FileSizeInBytes);
                            Console.WriteLine($"\n ✓ Successfully uploaded to {uploadResult.RemoteFilePath}");
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
                        await WriteAuditEventAsync(container, currentJobId, sourceUsbDrive, fileInfo, "Failed", ex.Message);
                        Console.WriteLine($"\n ✗ Failed: {ex.Message}");
                        failureCount++;
                    }
                }

                // Step 5: Generate final report
                Console.WriteLine("\n\n═══════════════════════════════════════════════════════");
                Console.WriteLine("BACKUP JOB SUMMARY");
                Console.WriteLine("═══════════════════════════════════════════════════════");
                Console.WriteLine($"Job ID: {currentJobId}");
                Console.WriteLine($"Source USB Drive: {sourceUsbDrive}");
                Console.WriteLine($"Total Files: {filesToBackup.Count}");
                Console.WriteLine($"Skipped Unchanged: {skippedCount}");
                Console.WriteLine($"Uploaded/Changed: {filesSelectedForBackup.Count}");
                Console.WriteLine($"Successful: {successCount}");
                Console.WriteLine($"Failed: {failureCount}");
                Console.WriteLine($"Total Data Backed Up: {FormatFileSize(totalBackedUp)}");
                Console.WriteLine($"Upload Success Rate: {(filesSelectedForBackup.Count == 0 ? 100 : successCount * 100.0 / filesSelectedForBackup.Count):F1}%");
                Console.WriteLine("═══════════════════════════════════════════════════════\n");
                Log.Information(
                    "Backup job {JobId} completed. TotalFiles={TotalFiles}, SkippedUnchanged={SkippedUnchanged}, UploadedOrChanged={UploadedOrChanged}, Successful={Successful}, Failed={Failed}, TotalBackedUpBytes={TotalBackedUpBytes}, DurationMs={DurationMs}.",
                    currentJobId,
                    filesToBackup.Count,
                    skippedCount,
                    filesSelectedForBackup.Count,
                    successCount,
                    failureCount,
                    totalBackedUp,
                    (DateTimeOffset.UtcNow - jobStartedAt).TotalMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Backup job failed after {DurationMs} ms.", (DateTimeOffset.UtcNow - jobStartedAt).TotalMilliseconds);
                Console.WriteLine($"\n✗ Fatal Error: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
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
                "SELECT TOP 1 * FROM c WHERE (NOT IS_DEFINED(c.documentType) OR c.documentType != 'AuditEvent') AND c.sourceUsbDevice = @sourceUsbDevice AND c.relativePath = @relativePath AND c.currentState = 'Completed' ORDER BY c.lastUpdated DESC")
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

        private static async Task WriteAuditEventAsync(
            Container container,
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

            await container.CreateItemAsync(auditEvent, new PartitionKey(jobId));
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
