using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using KumiIncrementalbackUp.Models;

namespace KumiIncrementalbackUp.Services
{
    public class AzureFileShareBackupService
    {
        private const int DefaultBufferSize = 4 * 1024 * 1024;

        private readonly ShareClient _shareClient;
        private readonly string _baseDirectoryName;

        public AzureFileShareBackupService(string connectionString, string shareName, string baseDirectoryName)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("An Azure Storage connection string is required.", nameof(connectionString));
            }

            if (string.IsNullOrWhiteSpace(shareName))
            {
                throw new ArgumentException("An Azure File Share name is required.", nameof(shareName));
            }

            _baseDirectoryName = string.IsNullOrWhiteSpace(baseDirectoryName)
                ? string.Empty
                : NormalizeSharePath(baseDirectoryName);

            _shareClient = new ShareClient(connectionString, shareName);
        }

        public async Task<FileUploadResult> UploadFileAsync(
            string localFilePath,
            Func<long, long, Task>? progressCallback = null)
        {
            return await UploadFileAsync(localFilePath, Path.GetFileName(localFilePath), progressCallback);
        }

        public async Task<FileUploadResult> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            Func<long, long, Task>? progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(localFilePath))
            {
                return FileUploadResult.Failed("A local file path is required.");
            }

            if (!File.Exists(localFilePath))
            {
                return FileUploadResult.Failed($"File '{localFilePath}' was not found.");
            }

            try
            {
                var fileInfo = new FileInfo(localFilePath);
                string normalizedRelativePath = NormalizeSharePath(
                    string.IsNullOrWhiteSpace(relativeRemotePath) ? fileInfo.Name : relativeRemotePath);

                if (ContainsParentTraversal(normalizedRelativePath))
                {
                    return FileUploadResult.Failed($"Remote path '{relativeRemotePath}' contains invalid parent directory traversal.");
                }

                string remoteDirectoryPath = CombineSharePath(_baseDirectoryName, Path.GetDirectoryName(normalizedRelativePath));
                ShareDirectoryClient targetDirectory = await GetOrCreateDirectoryAsync(remoteDirectoryPath);
                string remoteFileName = Path.GetFileName(normalizedRelativePath);
                ShareFileClient fileClient = targetDirectory.GetFileClient(remoteFileName);

                await fileClient.CreateAsync(fileInfo.Length);

                long uploadedBytes = 0;
                byte[] buffer = new byte[DefaultBufferSize];

                await using FileStream sourceStream = new FileStream(
                    localFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    DefaultBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                int bytesRead;
                while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    using var chunkStream = new MemoryStream(buffer, 0, bytesRead, writable: false);
                    var range = new HttpRange(uploadedBytes, bytesRead);
                    await fileClient.UploadRangeAsync(range, chunkStream);

                    uploadedBytes += bytesRead;
                    if (progressCallback is not null)
                    {
                        await progressCallback(uploadedBytes, fileInfo.Length);
                    }
                }

                string remotePath = CombineSharePath(_baseDirectoryName, normalizedRelativePath);

                return FileUploadResult.Success(remotePath);
            }
            catch (RequestFailedException ex)
            {
                return FileUploadResult.Failed($"Azure File Share upload failed: {ex.Message}");
            }
            catch (IOException ex)
            {
                return FileUploadResult.Failed($"Local file read failed: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return FileUploadResult.Failed($"Local file access denied: {ex.Message}");
            }
        }

        private async Task<ShareDirectoryClient> GetOrCreateDirectoryAsync(string directoryPath)
        {
            await _shareClient.CreateIfNotExistsAsync();

            ShareDirectoryClient currentDirectory = _shareClient.GetRootDirectoryClient();
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return currentDirectory;
            }

            foreach (string segment in directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                currentDirectory = currentDirectory.GetSubdirectoryClient(segment);
                await currentDirectory.CreateIfNotExistsAsync();
            }

            return currentDirectory;
        }

        private static string NormalizeSharePath(string path)
        {
            return path
                .Replace('\\', '/')
                .Trim('/');
        }

        private static string CombineSharePath(params string?[] pathParts)
        {
            return string.Join(
                '/',
                pathParts
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => NormalizeSharePath(part!))
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private static bool ContainsParentTraversal(string path)
        {
            return path
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment == "..");
        }
    }
}
