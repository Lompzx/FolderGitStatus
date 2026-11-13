using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace FolderGitStatus
{
    /// <summary>
    /// Keeps a repository's status up to date: watches the working tree and the index
    /// and reruns "git status" (debounced) when something changes. With "Show branch changes"
    /// on, it also lists the files changed by the commits of the current branch.
    /// </summary>
    internal sealed class GitRepository : IDisposable
    {
        private const int DebounceMilliseconds = 700;
        private const string StatusArguments = "--no-optional-locks status --porcelain=v1 -z --untracked-files=all";

        // Folders that change all the time and are usually in .gitignore; watching them
        // would trigger "git status" continuously during builds and while VS is open.
        private static readonly string[] IgnoredSegments = { @"\.git\", @"\.vs\", @"\bin\", @"\obj\", @"\node_modules\" };

        // Tried in this order when no base branch is configured and origin/HEAD is not set.
        private static readonly string[] BaseBranchCandidates = { "origin/main", "origin/master", "main", "master" };

        private readonly OptionsPage _options;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _debounce;
        private readonly object _gate = new object();
        private volatile StatusSnapshot _snapshot = StatusSnapshot.Empty;
        private volatile StatusSnapshot _branchSnapshot = StatusSnapshot.Empty;
        private volatile bool _baseResolved;
        private string _baseRef;
        private string _loggedBase;
        private string _lastBranchError;
        private bool _running;
        private bool _pending;
        private bool _disposed;

        public GitRepository(string root, OptionsPage options)
        {
            Root = root;
            _options = options;
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

        /// <summary>
        /// Uncommitted changes (working tree and index).
        /// </summary>
        public StatusSnapshot Snapshot => _snapshot;

        /// <summary>
        /// Changes committed on the current branch since it left the base branch
        /// (empty with "Show branch changes" off).
        /// </summary>
        public StatusSnapshot BranchSnapshot => _branchSnapshot;

        public event EventHandler Changed;

        public void Start()
        {
            _watcher.EnableRaisingEvents = true;
            ScheduleRefresh(0);
        }

        /// <summary>
        /// Looks up the base branch again and recomputes everything (after the options change).
        /// </summary>
        public void Invalidate()
        {
            _baseResolved = false;
            ScheduleRefresh(0);
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            var oldPath = (e as RenamedEventArgs)?.OldFullPath;

            // Commits, checkouts and fetches move the refs; the base branch may also appear
            // (e.g. after the first fetch), so it is looked up again.
            if (_options.ShowBranchChanges && (IsRefChange(e.FullPath) || IsRefChange(oldPath)))
            {
                _baseResolved = false;
                ScheduleRefresh(DebounceMilliseconds);
            }
            else if (IsRelevant(e.FullPath) || IsRelevant(oldPath))
            {
                ScheduleRefresh(DebounceMilliseconds);
            }
        }

        private string GetRelativePath(string fullPath) =>
            fullPath == null || fullPath.Length <= Root.Length ? null : fullPath.Substring(Root.Length);

        private bool IsRefChange(string fullPath)
        {
            var relative = GetRelativePath(fullPath);
            return relative != null && (
                relative.StartsWith(@"\.git\refs\", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(@"\.git\packed-refs", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals(@"\.git\HEAD", StringComparison.OrdinalIgnoreCase));
        }

        private bool IsRelevant(string fullPath)
        {
            var relative = GetRelativePath(fullPath);
            if (relative == null)
            {
                return false;
            }

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
                var changed = false;

                // --no-optional-locks keeps "git status" itself from rewriting the index
                // (which would trigger the watcher and cause a refresh loop).
                if (TryRunGit(StatusArguments, out var output, out var error))
                {
                    var snapshot = StatusSnapshot.Parse(Root, output);
                    if (!snapshot.HasSameContent(_snapshot))
                    {
                        _snapshot = snapshot;
                        changed = true;
                    }
                }
                else
                {
                    Log.Write($"git status failed in '{Root}': {error}");
                }

                var branchSnapshot = GetBranchSnapshot();
                if (branchSnapshot != null && !branchSnapshot.HasSameContent(_branchSnapshot))
                {
                    _branchSnapshot = branchSnapshot;
                    changed = true;
                }

                if (changed)
                {
                    Log.Debug($"Status updated: {Root}");
                    Changed?.Invoke(this, EventArgs.Empty);
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

        /// <summary>
        /// Files changed between the point where the branch left the base branch and HEAD
        /// ("git diff base...HEAD"). Returns null to keep the previous result when git fails.
        /// </summary>
        private StatusSnapshot GetBranchSnapshot()
        {
            if (!_options.ShowBranchChanges)
            {
                _baseResolved = false;
                _loggedBase = null;
                return StatusSnapshot.Empty;
            }

            if (!_baseResolved)
            {
                _baseResolved = true;
                _baseRef = ResolveBaseRef();
                var description = _baseRef ?? "(none)";
                if (description != _loggedBase)
                {
                    _loggedBase = description;
                    Log.Write(_baseRef == null
                        ? $"Branch changes in '{Root}': no base branch found; set one under Tools > Options > Folder Git Status"
                        : $"Branch changes in '{Root}': comparing with {_baseRef}");
                }
            }

            if (_baseRef == null)
            {
                return StatusSnapshot.Empty;
            }

            if (!TryRunGit($"--no-optional-locks diff --name-status -z --no-ext-diff {_baseRef}...HEAD --", out var output, out var error))
            {
                if (error != _lastBranchError)
                {
                    _lastBranchError = error;
                    Log.Write($"Failed to compare '{Root}' with {_baseRef}: {error}");
                }

                return null;
            }

            _lastBranchError = null;
            return StatusSnapshot.ParseNameStatus(Root, output);
        }

        /// <summary>
        /// The configured base branch if it exists in this repository (the option applies to every
        /// repository, which may use different names); otherwise the remote's default branch, or
        /// the first of <see cref="BaseBranchCandidates"/> that exists.
        /// </summary>
        private string ResolveBaseRef()
        {
            var configured = _options.BaseBranch?.Trim();
            if (!string.IsNullOrEmpty(configured))
            {
                if (IsSafeRefName(configured) && RefExists(configured))
                {
                    return configured;
                }

                Log.Write($"Base branch \"{configured}\" not found in '{Root}'; detecting it automatically");
            }

            if (TryRunGit("symbolic-ref --quiet --short refs/remotes/origin/HEAD", out var originHead, out _))
            {
                originHead = originHead.Trim();
                if (IsSafeRefName(originHead) && RefExists(originHead))
                {
                    return originHead;
                }
            }

            foreach (var candidate in BaseBranchCandidates)
            {
                if (RefExists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private bool RefExists(string name) => TryRunGit($"rev-parse --verify --quiet {name}^{{commit}}", out _, out _);

        /// <summary>
        /// The name goes into a command line, so only characters valid in Git ref names are accepted.
        /// </summary>
        private static bool IsSafeRefName(string name)
        {
            if (name.Length == 0 || name[0] == '-' || name.Contains(".."))
            {
                return false;
            }

            foreach (var c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '/' || c == '-' || c == '_' || c == '.'))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryRunGit(string arguments, out string output, out string error)
        {
            var startInfo = new ProcessStartInfo(_options.GitExecutable, arguments)
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
                var errorText = new StringBuilder();
                process.ErrorDataReceived += (s, e) => errorText.AppendLine(e.Data);
                process.Start();
                process.BeginErrorReadLine();

                output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                error = errorText.ToString().Trim();
                return process.ExitCode == 0;
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
