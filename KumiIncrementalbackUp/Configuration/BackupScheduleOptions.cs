namespace KumiIncrementalbackUp.Configuration
{
    public sealed class BackupScheduleOptions
    {
        public bool IsEnabled { get; private set; }
        public TimeSpan Interval { get; private set; } = TimeSpan.FromHours(1);

        public static BackupScheduleOptions From(string[] args, ScheduleSettings scheduleSettings)
        {
            var options = new BackupScheduleOptions
            {
                IsEnabled = scheduleSettings.Enabled,
                Interval = TimeSpan.FromMinutes(scheduleSettings.IntervalMinutes)
            };

            string? environmentInterval = Environment.GetEnvironmentVariable("BACKUP_INTERVAL_MINUTES");
            if (double.TryParse(environmentInterval, out double intervalFromEnvironment))
            {
                options.Interval = TimeSpan.FromMinutes(intervalFromEnvironment);
            }

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];

                if (string.Equals(argument, "--schedule", StringComparison.OrdinalIgnoreCase))
                {
                    options.IsEnabled = true;
                    continue;
                }

                if (string.Equals(argument, "--once", StringComparison.OrdinalIgnoreCase))
                {
                    options.IsEnabled = false;
                    continue;
                }

                if (string.Equals(argument, "--interval-minutes", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (double.TryParse(args[++i], out double intervalFromArgs))
                    {
                        options.Interval = TimeSpan.FromMinutes(intervalFromArgs);
                    }
                }
            }

            if (options.Interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(Interval), "Backup interval must be greater than zero.");
            }

            return options;
        }
    }
}
