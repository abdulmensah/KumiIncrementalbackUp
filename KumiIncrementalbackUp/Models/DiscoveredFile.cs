namespace KumiIncrementalbackUp.Models
{
    public class DiscoveredFile
    {
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long FileSizeInBytes { get; set; }
        public DateTime LastModifiedUtc { get; set; }
        public string ContentHash { get; set; } = string.Empty;
    }
}
