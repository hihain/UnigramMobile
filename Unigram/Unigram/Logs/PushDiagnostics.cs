using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
        public const string FaultFileName = "voip-fault.txt";

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

        public static void WriteException(string eventName, Exception exception, string context = null)
        {
            WriteException(eventName, exception, context, null);
        }

        /// <summary>
        /// <paramref name="description"/> carries the platform-supplied text of an unhandled
        /// failure. On .NET Native the managed exception reaching Application.UnhandledException
        /// has already lost its stack, while the event argument's own message still describes the
        /// throw site, so it is the only remaining way to attribute the fault.
        /// </summary>
        public static void WriteException(string eventName, Exception exception, string context, string description)
        {
            var prefix = string.IsNullOrEmpty(context) ? string.Empty : context + ";";

            if (exception == null)
            {
                Write(eventName, prefix + "result=error;type=none");
                return;
            }

            string details;

            try
            {
#if MODERN_TDLIB
                // EventAggregator dispatches updates through MethodInfo.Invoke, so the
                // failure that matters is wrapped in a TargetInvocationException. Report the
                // innermost cause and keep the wrapper chain, which is type names only.
                var root = exception;
                var chain = root.GetType().Name;
                while (root is TargetInvocationException && root.InnerException != null)
                {
                    root = root.InnerException;
                    chain += ">" + root.GetType().Name;
                }

                details = $"result=error;hresult=0x{root.HResult:X8};type={root.GetType().Name};chain={chain}";

                // The parameter name carried by an argument exception identifies which value an
                // interop call rejected, which is what makes a marshalling failure actionable.
                // It is a compile-time identifier from the API surface, never user data.
                if (root is ArgumentException argument && !string.IsNullOrEmpty(argument.ParamName))
                {
                    details += $";param={argument.ParamName}";
                }

                // A missing native module surfaces as FileNotFoundException/DllNotFoundException
                // with HRESULT 0x8007007E, and the module name is the only thing that identifies
                // which payload failed to load. The HRESULT test keeps ordinary file I/O failures
                // out of this field, since those carry a caller-supplied name that may describe
                // user content; a module name is a build artifact.
                if (root.HResult == unchecked((int)0x8007007E))
                {
                    if (root is FileNotFoundException notFound && !string.IsNullOrEmpty(notFound.FileName))
                    {
                        details += $";missing_module={SanitizeErrorMessage(notFound.FileName, 120)}";
                    }
                    else
                    {
                        details += ";missing_module=unnamed";
                    }
                }

                details += $";message={SanitizeErrorMessage(root.Message)}";

                // .NET Native reduces a NullReferenceException message to the resource key
                // "Arg_NullReferenceException", so the frame list is the only thing that
                // identifies where the fault happened. Frames are method names from this
                // app's own surface, and the shared sanitizer still strips any path.
                var stack = root.StackTrace;
                if (!string.IsNullOrEmpty(stack))
                {
                    var frames = stack.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " << ");
                    details += $";stack={SanitizeErrorMessage(frames, 900)}";
                }
                else
                {
                    // An exception surfaced through Application.UnhandledException has already
                    // had its stack discarded. The platform description is populated before that
                    // happens, so prefer it and fall back to ToString when none was supplied.
                    var text = string.IsNullOrEmpty(description) ? root.ToString() : description;
                    if (!string.IsNullOrEmpty(text))
                    {
                        var frames = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " << ");
                        details += $";detail={SanitizeErrorMessage(frames, 900)}";
                    }
                }
#else
                details = $"result=error;hresult=0x{exception.HResult:X8};type={exception.GetType().Name}";
#endif
            }
            catch
            {
                // This runs inside a catch handler on TDLib's receive thread. Letting a
                // logging failure escape would reach the native caller and fail the process
                // fast, replacing the original fault with a less useful one.
                details = "result=error;type=unavailable";
            }

            Write(eventName, prefix + details);
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

        /// <summary>
        /// Returns the file that native fault reporting appends to. It is deliberately not
        /// the main diagnostics log: a fault handler can only append with raw file APIs,
        /// while the managed writer tracks its own end-of-file offset and would write back
        /// over anything appended behind its back.
        /// </summary>
        public static string GetFaultFilePath()
        {
            try
            {
                var path = GetFilePath();
                return string.IsNullOrEmpty(path)
                    ? null
                    : Path.Combine(Path.GetDirectoryName(path), FaultFileName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Moves anything the native fault handler recorded into the main log, so a single
        /// file still tells the whole story, then clears it.
        /// </summary>
        public static void DrainFaultFile()
        {
            try
            {
                var path = GetFaultFilePath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return;
                }

                foreach (var line in File.ReadAllLines(path))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Write("voip.fault", SanitizeErrorMessage(line.Trim()) + ";deferred=1");
                    }
                }

                File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to drain fault diagnostics: 0x{ex.HResult:X8}");
            }
        }

        public static string SanitizeErrorMessage(string message)        {
            return SanitizeErrorMessage(message, 256);
        }

        public static string SanitizeErrorMessage(string message, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "none";
            }

            try
            {
                var sanitized = message.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
                sanitized = Regex.Replace(sanitized, @"https?://\S+", "[redacted_uri]", RegexOptions.IgnoreCase);
                // Must run after the URI rule, otherwise it would consume "s://host" first.
                // Segments deliberately allow spaces so that a profile name such as
                // "C:\Users\John Smith\..." cannot survive redaction; over-redacting the
                // tail of a sentence is an acceptable trade for never emitting a path.
                sanitized = Regex.Replace(sanitized, @"[A-Za-z]:(?:[\\/][^\\/""\r\n]*)+", "[redacted_path]");
                sanitized = Regex.Replace(sanitized, @"\b\d{6,}\b", "[redacted_number]");
                // Framework resource keys and type names are the only locator left once
                // .NET Native strips an exception's stack, and they are long enough to trip
                // the opaque-token rule. Preserve that one identifier shape -- letters and
                // underscores only, and only when it names an exception -- together with the
                // two native status keys that reach this length, so that anything else
                // bearing digits, hyphens or arbitrary words (tokens, GUIDs, base64, URIs,
                // usernames, host labels, file names) still redacts.
                sanitized = Regex.Replace(sanitized, @"\b[A-Za-z0-9_-]{24,}\b", match =>
                    Regex.IsMatch(match.Value, @"^(?:Arg_[A-Za-z]+|[A-Za-z]*Exception[A-Za-z]*|stereo_playout_available|stereo_recording_available)$")
                        ? match.Value
                        : "[redacted_token]");
                return sanitized.Length <= maxLength ? sanitized : sanitized.Substring(0, maxLength);
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
