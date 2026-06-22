using Microsoft.Extensions.Configuration;

namespace KumiIncrementalbackUp.Configuration
{
    public sealed class BackupAppSettings
    {
        public BackupSettings Backup { get; set; } = new();
        public CosmosDbSettings CosmosDb { get; set; } = new();
        public AzureFileShareSettings AzureFileShare { get; set; } = new();
        public ScheduleSettings Schedule { get; set; } = new();
        public AuditTelemetrySettings AuditTelemetry { get; set; } = new();

        public static BackupAppSettings Load()
        {
            string environmentName = GetEnvironmentName();

            IConfiguration configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();

            var settings = configuration.Get<BackupAppSettings>() ?? new BackupAppSettings();
            settings.Validate();
            return settings;
        }

        private void Validate()
        {
            Require(Backup.SourceUsbDrive, "Backup:SourceUsbDrive");
            Require(CosmosDb.EndpointUri, "CosmosDb:EndpointUri");
            Require(CosmosDb.PrimaryKey, "CosmosDb:PrimaryKey");
            Require(CosmosDb.DatabaseId, "CosmosDb:DatabaseId");
            Require(CosmosDb.ContainerId, "CosmosDb:ContainerId");
            Require(AzureFileShare.ConnectionString, "AzureFileShare:ConnectionString");
            Require(AzureFileShare.ShareName, "AzureFileShare:ShareName");

            if (Schedule.IntervalMinutes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Schedule.IntervalMinutes), "Schedule:IntervalMinutes must be greater than zero.");
            }
        }

        private static void Require(string value, string settingName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Missing required app setting '{settingName}'.");
            }
        }

        private static string GetEnvironmentName()
        {
            string? environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

            if (!string.IsNullOrWhiteSpace(environmentName))
            {
                return environmentName;
            }

#if DEBUG
            return "Development";
#else
            return "Production";
#endif
        }
    }

    public sealed class BackupSettings
    {
        public string SourceUsbDrive { get; set; } = string.Empty;
    }

    public sealed class CosmosDbSettings
    {
        public string EndpointUri { get; set; } = string.Empty;
        public string PrimaryKey { get; set; } = string.Empty;
        public string DatabaseId { get; set; } = string.Empty;
        public string ContainerId { get; set; } = string.Empty;
    }

    public sealed class AzureFileShareSettings
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string ShareName { get; set; } = string.Empty;
        public string DirectoryName { get; set; } = string.Empty;
    }

    public sealed class ScheduleSettings
    {
        public bool Enabled { get; set; }
        public double IntervalMinutes { get; set; } = 60;
    }

    public sealed class AuditTelemetrySettings
    {
        public string ApplicationInsightsConnectionString { get; set; } = string.Empty;
        public string LogFilePath { get; set; } = "logs/backup-audit-.log";
        public string MinimumLevel { get; set; } = "Information";
    }
}
