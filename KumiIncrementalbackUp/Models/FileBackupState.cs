using Newtonsoft.Json;

namespace KumiIncrementalbackUp.Models
{
    public class FileBackupState
    {
        [JsonProperty(PropertyName = "id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty(PropertyName = "jobId")]
        public string JobId { get; set; } = string.Empty;

        public string DocumentType { get; set; } = "FileState";
        public string FileName { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string CurrentState { get; set; } = string.Empty;
        public string BackupAction { get; set; } = string.Empty;
        public long FileSizeInBytes { get; set; }
        public long BytesTransferred { get; set; }
        public DateTime LastModifiedUtc { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public string? FailureReason { get; set; }
        public string? RemoteFileSharePath { get; set; }
        public string SourceUsbDevice { get; set; } = string.Empty;
        public string? PreviousJobId { get; set; }
        public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    }
}
