namespace KumiIncrementalbackUp.Models
{
    public class FileUploadResult
    {
        public bool IsSuccess { get; set; }
        public string RemoteFilePath { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;

        public static FileUploadResult Success(string remoteFilePath)
        {
            return new FileUploadResult
            {
                IsSuccess = true,
                RemoteFilePath = remoteFilePath
            };
        }

        public static FileUploadResult Failed(string errorMessage)
        {
            return new FileUploadResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage
            };
        }
    }
}
