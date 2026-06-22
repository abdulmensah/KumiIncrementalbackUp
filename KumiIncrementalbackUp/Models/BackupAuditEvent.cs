using Newtonsoft.Json;

namespace KumiIncrementalbackUp.Models
{
    public class BackupAuditEvent
    {
        [JsonProperty(PropertyName = "id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty(PropertyName = "jobId")]
        public string JobId { get; set; } = string.Empty;

        public string DocumentType { get; set; } = "AuditEvent";
        public string EventType { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string? RemoteFileSharePath { get; set; }
        public string SourceUsbDevice { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? PreviousJobId { get; set; }
        public long FileSizeInBytes { get; set; }
        public long BytesTransferred { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
