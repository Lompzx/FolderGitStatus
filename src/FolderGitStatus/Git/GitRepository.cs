using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace FolderGitStatus
{
    /// <summary>
    /// Keeps a repository's status up to date: watches the working tree and the index
    /// and reruns "git status" (debounced) when something changes.
    /// </summary>
    internal sealed class GitRepository : IDisposable
    {
        private const int DebounceMilliseconds = 700;

        // Folders that change all the time and are usually in .gitignore; watching them
        // would trigger "git status" continuously during builds and while VS is open.
        private static readonly string[] IgnoredSegments = { @"\.git\", @"\.vs\", @"\bin\", @"\obj\", @"\node_modules\" };

        private readonly Func<string> _gitExecutable;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _debounce;
        private readonly object _gate = new object();
        private volatile StatusSnapshot _snapshot = StatusSnapshot.Empty;
        private bool _running;
        private bool _pending;
        private bool _disposed;

        public GitRepository(string root, Func<string> gitExecutable)
        {
            Root = root;
            _gitExecutable = gitExecutable;
            _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);

            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;
            _watcher.Error += (s, e) => ScheduleRefresh(DebounceMilliseconds);
        }

        public string Root { get; }

        public StatusSnapshot Snapshot => _snapshot;

        public event EventHandler Changed;

        public void Start()
        {
            _watcher.EnableRaisingEvents = true;
            ScheduleRefresh(0);
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (IsRelevant(e.FullPath) || (e is RenamedEventArgs renamed && IsRelevant(renamed.OldFullPath)))
            {
                ScheduleRefresh(DebounceMilliseconds);
            }
        }

        private bool IsRelevant(string fullPath)
        {
            if (fullPath == null || fullPath.Length <= Root.Length)
            {
                return false;
            }

            var relative = fullPath.Substring(Root.Length);

            // stage/unstage, commit, checkout and merge change the index or HEAD
            if (relative.Equals(@"\.git\index", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals(@"\.git\HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            relative += "\\";
            foreach (var segment in IgnoredSegments)
            {
                if (relative.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }

            return true;
        }

        private void ScheduleRefresh(int delay)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _debounce.Change(delay, Timeout.Infinite);
                }
            }
        }

        private void Refresh()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_running)
                {
                    _pending = true;
                    return;
                }

                _running = true;
            }

            try
            {
                var output = RunGitStatus();
                if (output != null)
                {
                    var snapshot = StatusSnapshot.Parse(Root, output);
                    if (!snapshot.HasSameContent(_snapshot))
                    {
                        _snapshot = snapshot;
                        Log.Debug($"Status updated: {Root}");
                        Changed?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Failed to get the status of '{Root}': {ex.Message}");
            }
            finally
            {
                bool again;
                lock (_gate)
                {
                    _running = false;
                    again = _pending;
                    _pending = false;
                }

                if (again)
                {
                    ScheduleRefresh(DebounceMilliseconds);
                }
            }
        }

        private string RunGitStatus()
        {
            // --no-optional-locks keeps "git status" itself from rewriting the index
            // (which would trigger the watcher and cause a refresh loop).
            var startInfo = new ProcessStartInfo(_gitExecutable(), "--no-optional-locks status --porcelain=v1 -z --untracked-files=all")
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                var error = new StringBuilder();
                process.ErrorDataReceived += (s, e) => error.AppendLine(e.Data);
                process.Start();
                process.BeginErrorReadLine();

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Log.Write($"git status failed in '{Root}': {error.ToString().Trim()}");
                    return null;
                }

                return output;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }

            _watcher.Dispose();
            _debounce.Dispose();
        }
    }
}
