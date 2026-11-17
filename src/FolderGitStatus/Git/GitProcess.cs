using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

// Runs on background threads (timer callbacks and Task.Run), never on the UI thread.
#pragma warning disable VSTHRD002

namespace FolderGitStatus
{
    /// <summary>
    /// Runs git with a time limit: a git that hangs (a lock held by another process, a network
    /// file system, an antivirus scan) is killed instead of blocking the repository forever.
    /// </summary>
    internal static class GitProcess
    {
        /// <summary>
        /// Raised once per session when the git executable cannot be started (not installed,
        /// not on the PATH or a wrong path in the options).
        /// </summary>
        public static event EventHandler<string> Unavailable;

        /// <summary>
        /// Raised when git starts again after <see cref="Unavailable"/> (e.g. the path was fixed).
        /// </summary>
        public static event EventHandler Available;

        private static volatile bool _reportedUnavailable;

        internal sealed class Result
        {
            public bool Success { get; set; }

            public byte[] Output { get; set; } = Array.Empty<byte>();

            public string Error { get; set; } = string.Empty;

            public bool TimedOut { get; set; }

            public string Text => Encoding.UTF8.GetString(Output);
        }

        public static Result Run(string gitExecutable, string workingDirectory, string arguments, TimeSpan timeout)
        {
            var startInfo = new ProcessStartInfo(string.IsNullOrWhiteSpace(gitExecutable) ? "git" : gitExecutable.Trim().Trim('"'), arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // Never wait for credentials or an editor.
            startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";

            using (var process = new Process { StartInfo = startInfo })
            {
                try
                {
                    process.Start();
                }
                catch (Win32Exception ex)
                {
                    ReportUnavailable($"Could not run \"{startInfo.FileName}\": {ex.Message}");
                    return new Result { Error = ex.Message };
                }

                if (_reportedUnavailable)
                {
                    _reportedUnavailable = false;
                    Available?.Invoke(null, EventArgs.Empty);
                }

                var output = new MemoryStream();
                var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
                var error = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
                    {
                    }

                    return new Result { TimedOut = true, Error = $"git {arguments} did not finish in {timeout.TotalSeconds:0}s and was stopped" };
                }

                // The streams finish shortly after the process exits.
                Task.WaitAll(new Task[] { copy, error }, TimeSpan.FromSeconds(5));

                return new Result
                {
                    Success = process.ExitCode == 0,
                    Output = output.ToArray(),
                    Error = error.Status == TaskStatus.RanToCompletion ? error.Result.Trim() : string.Empty,
                };
            }
        }

        private static void ReportUnavailable(string message)
        {
            if (_reportedUnavailable)
            {
                return;
            }

            _reportedUnavailable = true;
            Log.Write(message);
            Unavailable?.Invoke(null, message);
        }
    }
}
