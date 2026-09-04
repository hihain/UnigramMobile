using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;

namespace Unigram.Logs
{
    public static class PushDiagnostics
    {
        private const long MaximumFileSize = 1024 * 1024;
        private static readonly object FileSyncRoot = new object();

        public const string DirectoryName = "Diagnostics";
        public const string FileName = "push-diagnostics.txt";

        public static void Write(string eventName, string details = null)
        {
            try
            {
                lock (FileSyncRoot)
                {
                    var filePath = GetFilePath();
                    if (string.IsNullOrEmpty(filePath))
                    {
                        return;
                    }

                    var directory = Path.GetDirectoryName(filePath);
                    Directory.CreateDirectory(directory);

                    if (File.Exists(filePath) && new FileInfo(filePath).Length >= MaximumFileSize)
                    {
                        File.Delete(filePath);
                    }

                    var line = $"{DateTimeOffset.UtcNow:O}|{eventName}";
                    if (!string.IsNullOrEmpty(details))
                    {
                        line += $"|{details}";
                    }

                    line += Environment.NewLine;
                    var bytes = Encoding.UTF8.GetBytes(line);
                    using (var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to write push diagnostics: 0x{ex.HResult:X8}");
            }
        }

        public static void WriteException(string eventName, Exception exception)
        {
            Write(eventName, $"result=error;hresult=0x{exception.HResult:X8};type={exception.GetType().Name}");
        }

        public static string HashIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "none";
            }

            try
            {
                var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
                var input = CryptographicBuffer.ConvertStringToBinary(value, BinaryStringEncoding.Utf8);
                var hash = CryptographicBuffer.EncodeToHexString(provider.HashData(input));
                return hash.Substring(0, 12);
            }
            catch
            {
                return "hash_error";
            }
        }

        public static string SanitizeErrorMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "none";
            }

            try
            {
                var sanitized = message.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
                sanitized = Regex.Replace(sanitized, @"https?://\S+", "[redacted_uri]", RegexOptions.IgnoreCase);
                sanitized = Regex.Replace(sanitized, @"\b\d{6,}\b", "[redacted_number]");
                sanitized = Regex.Replace(sanitized, @"\b[A-Za-z0-9_-]{24,}\b", "[redacted_token]");
                return sanitized.Length <= 256 ? sanitized : sanitized.Substring(0, 256);
            }
            catch
            {
                return "sanitize_error";
            }
        }

        private static string GetFilePath()
        {
            try
            {
                var localFolder = ApplicationData.Current?.LocalFolder;
                if (localFolder == null || string.IsNullOrEmpty(localFolder.Path))
                {
                    return null;
                }

                return Path.Combine(localFolder.Path, DirectoryName, FileName);
            }
            catch
            {
                return null;
            }
        }
    }
}
