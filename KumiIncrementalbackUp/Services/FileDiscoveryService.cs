using KumiIncrementalbackUp.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Serilog;

namespace KumiIncrementalbackUp.Services
{
    public class FileDiscoveryService
    {
        private readonly string _sourcePath;
        private readonly HashSet<string> _excludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "$RECYCLE.BIN",
            "trashbox",
            "System Volume Information"
        };

        public FileDiscoveryService(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("A source path is required.", nameof(sourcePath));
            }

            _sourcePath = sourcePath;
        }

        public Task<List<DiscoveredFile>> DiscoverFilesAsync()
        {
            return Task.Run(() =>
            {
                if (!Directory.Exists(_sourcePath))
                {
                    throw new DirectoryNotFoundException($"Source path '{_sourcePath}' was not found.");
                }

                var files = new List<DiscoveredFile>();
                foreach (string filePath in EnumerateFilesSafely(_sourcePath))
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        var lastAccess = new DateTime();
                        try
                        {
                            //Not important if this succeeds
                            lastAccess = fileInfo.LastWriteTimeUtc;
                        }
                        catch
                        {
                            Log.Warning("Could not read last write time for file {FilePath}.", filePath);
                        }

                        if (!fileInfo.Exists || IsHiddenOrSystem(fileInfo.Attributes))
                        {
                            continue;
                        }

                        files.Add( new DiscoveredFile
                        {
                            FileName = fileInfo.Name,
                            FilePath = fileInfo.FullName,
                            RelativePath = NormalizeRelativePath(Path.GetRelativePath(_sourcePath, fileInfo.FullName)),
                            FileSizeInBytes = fileInfo.Length,
                            ContentHash = ComputeSha256(fileInfo.FullName),
                            LastModifiedUtc = lastAccess
                        });

                    }
                    catch (IOException ex)
                    {
                        Log.Warning(ex, "Skipping file {FilePath} because it could not be read.", filePath);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Log.Warning(ex, "Skipping file {FilePath} because access was denied.", filePath);
                    }
                }

                return files
                    .OrderBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
        }

        private IEnumerable<string> EnumerateFilesSafely(string directoryPath)
        {
            IEnumerable<string> files = Enumerable.Empty<string>();
            IEnumerable<string> directories = Enumerable.Empty<string>();

            try
            {
                files = Directory.EnumerateFiles(directoryPath);
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "Could not read files in directory {DirectoryPath}.", directoryPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning(ex, "Could not read files in directory {DirectoryPath} because access was denied.", directoryPath);
            }

            foreach (string file in files)
            {
                yield return file;
            }

            try
            {
                directories = Directory.EnumerateDirectories(directoryPath)
                    .Where(directory => !_excludedDirectoryNames.Contains(Path.GetFileName(directory)));
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "Could not read subdirectories in directory {DirectoryPath}.", directoryPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning(ex, "Could not read subdirectories in directory {DirectoryPath} because access was denied.", directoryPath);
            }

            foreach (string directory in directories)
            {
                DirectoryInfo directoryInfo;
                try
                {
                    directoryInfo = new DirectoryInfo(directory);
                    if (IsHiddenOrSystem(directoryInfo.Attributes))
                    {
                        continue;
                    }
                }
                catch (IOException ex)
                {
                    Log.Warning(ex, "Skipping directory {DirectoryPath} because it could not be read.", directory);
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Warning(ex, "Skipping directory {DirectoryPath} because access was denied.", directory);
                    continue;
                }

                foreach (string file in EnumerateFilesSafely(directory))
                {
                    yield return file;
                }
            }
        }

        private static bool IsHiddenOrSystem(FileAttributes attributes)
        {
            return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
        }

        private static string NormalizeRelativePath(string path)
        {
            return path
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Trim('/');
        }

        private static string ComputeSha256(string filePath)
        {
            using FileStream stream = File.OpenRead(filePath);
            byte[] hashBytes = SHA256.HashData(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
    }
}
