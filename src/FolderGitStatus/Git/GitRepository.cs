using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FolderGitStatus
{
    /// <summary>
    /// Keeps a repository's status up to date: watches the working tree and the Git directory
    /// and reruns "git status" (debounced) when something changes. When branch changes are needed
    /// (the option or the Solution Explorer filter), it also lists the files changed by the
    /// commits of the current branch.
    /// </summary>
    internal sealed class GitRepository : IDisposable
    {
        private const int DebounceMilliseconds = 700;
        private const string StatusArguments = "--no-optional-locks status --porcelain=v1 -z --untracked-files=all";

        /// <summary>
        /// Git config key with the base branch of one repository ("git config foldergitstatus.baseBranch develop").
        /// </summary>
        public const string BaseBranchConfigKey = "foldergitstatus.baseBranch";

        // Tried in this order when no base branch is configured and origin/HEAD is not set.
        private static readonly string[] BaseBranchCandidates = { "origin/main", "origin/master", "main", "master" };

        private readonly OptionsPage _options;
        private readonly Func<bool> _branchChangesNeeded;
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly Timer _debounce;
        private readonly object _gate = new object();
        private volatile StatusSnapshot _snapshot = StatusSnapshot.Empty;
        private volatile StatusSnapshot _branchSnapshot = StatusSnapshot.Empty;
        private volatile bool _baseResolved;
        private volatile string _baseRef;
        private volatile string[] _ignoredSegments;
        private volatile string _gitDir;
        private volatile string _commonDir;
        private bool _gitDirsResolved;
        private string _loggedBase;
        private string _lastBranchError;
        private bool _running;
        private bool _pending;
        private bool _disposed;

        public GitRepository(string root, OptionsPage options, Func<bool> branchChangesNeeded)
        {
            Root = root;
            _options = options;
            _branchChangesNeeded = branchChangesNeeded;
            _gitDir = _commonDir = Path.Combine(root, ".git");
            _ignoredSegments = GetIgnoredSegments();
            _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
            _watchers.Add(CreateWatcher(root));
        }

        public string Root { get; }

        /// <summary>
        /// Uncommitted changes (working tree and index).
        /// </summary>
        public StatusSnapshot Snapshot => _snapshot;

        /// <summary>
        /// Changes committed on the current branch since it left the base branch
        /// (empty when branch changes are not needed).
        /// </summary>
        public StatusSnapshot BranchSnapshot => _branchSnapshot;

        /// <summary>
        /// Base branch the branch changes are compared with (null if not resolved or not found).
        /// </summary>
        public string BaseRef => _baseResolved ? _baseRef : null;

        public event EventHandler Changed;

        private TimeSpan GitTimeout => TimeSpan.FromSeconds(Math.Max(5, _options.GitTimeoutSeconds));

        public void Start()
        {
            _watchers[0].EnableRaisingEvents = true;
            ScheduleRefresh(0);
        }

        /// <summary>
        /// Looks up the base branch again and recomputes everything (after the options change).
        /// </summary>
        public void Invalidate()
        {
            _ignoredSegments = GetIgnoredSegments();
            _baseResolved = false;
            ScheduleRefresh(0);
        }

        private FileSystemWatcher CreateWatcher(string path)
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            watcher.Error += (s, e) => ScheduleRefresh(DebounceMilliseconds);
            return watcher;
        }

        private string[] GetIgnoredSegments()
        {
            var segments = GitOutput.ParseIgnoredFolders(_options.IgnoredFolders);
            return segments.Length == 0 && _options.IgnoredFolders == null
                ? GitOutput.ParseIgnoredFolders(GitOutput.DefaultIgnoredFolders)
                : segments;
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            var oldPath = (e as RenamedEventArgs)?.OldFullPath;
            var kind = Classify(e.FullPath);
            if (oldPath != null)
            {
                var oldKind = Classify(oldPath);
                if (oldKind > kind)
                {
                    kind = oldKind;
                }
            }

            switch (kind)
            {
                case EventKind.RefOrConfig:
                    // Commits, checkouts and fetches move the refs; the base branch may also appear
                    // (e.g. after the first fetch) or be set in the git config, so it is looked up again.
                    _baseResolved = false;
                    ScheduleRefresh(DebounceMilliseconds);
                    break;
                case EventKind.Status:
                    ScheduleRefresh(DebounceMilliseconds);
                    break;
            }
        }

        private enum EventKind
        {
            None,
            Status,
            RefOrConfig,
        }

        /// <summary>
        /// The Git directory is usually "&lt;root&gt;\.git", but in worktrees and submodules ".git" is a
        /// file that points elsewhere: the index and HEAD are in the Git directory, and the refs and
        /// the config in the common directory (the main repository's ".git").
        /// </summary>
        private EventKind Classify(string fullPath)
        {
            if (fullPath == null)
            {
                return EventKind.None;
            }

            var gitDir = _gitDir;
            var commonDir = _commonDir;

            var inGitDir = GetRelative(fullPath, gitDir);
            if (inGitDir != null)
            {
                // stage/unstage, commit, checkout and merge change the index or HEAD
                if (inGitDir.Equals(@"\HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    return EventKind.RefOrConfig;
                }

                if (inGitDir.Equals(@"\index", StringComparison.OrdinalIgnoreCase))
                {
                    return EventKind.Status;
                }
            }

            var inCommonDir = GetRelative(fullPath, commonDir);
            if (inCommonDir != null)
            {
                return inCommonDir.StartsWith(@"\refs\", StringComparison.OrdinalIgnoreCase) ||
                       inCommonDir.Equals(@"\packed-refs", StringComparison.OrdinalIgnoreCase) ||
                       inCommonDir.Equals(@"\config", StringComparison.OrdinalIgnoreCase)
                    ? EventKind.RefOrConfig
                    : EventKind.None;
            }

            if (inGitDir != null)
            {
                return EventKind.None;
            }

            var relative = GetRelative(fullPath, Root);
            if (relative == null)
            {
                return EventKind.None;
            }

            relative += "\\";
            if (relative.StartsWith(@"\.git\", StringComparison.OrdinalIgnoreCase))
            {
                return EventKind.None;
            }

            foreach (var segment in _ignoredSegments)
            {
                if (relative.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return EventKind.None;
                }
            }

            return EventKind.Status;
        }

        /// <summary>
        /// "\sub\file" for a path under <paramref name="directory"/>, otherwise null.
        /// </summary>
        private static string GetRelative(string fullPath, string directory)
        {
            if (directory == null || fullPath.Length <= directory.Length ||
                fullPath[directory.Length] != '\\' ||
                !fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fullPath.Substring(directory.Length);
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
                if (!_gitDirsResolved)
                {
                    _gitDirsResolved = true;
                    ResolveGitDirectories();
                }

                var changed = false;

                // --no-optional-locks keeps "git status" itself from rewriting the index
                // (which would trigger the watcher and cause a refresh loop).
                var arguments = _options.DimIgnoredItems ? StatusArguments + " --ignored=matching" : StatusArguments;
                var status = RunGit(arguments);
                if (status.Success)
                {
                    var snapshot = StatusSnapshot.Parse(Root, status.Text);
                    if (!snapshot.HasSameContent(_snapshot))
                    {
                        _snapshot = snapshot;
                        changed = true;
                    }
                }
                else
                {
                    Log.Write($"git status failed in '{Root}': {status.Error}");
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
        /// In worktrees and submodules the Git directory is outside the working tree (or inside
        /// the parent repository's ".git", which the working tree watcher skips), so it gets its own
        /// watcher; otherwise stage-only changes and commits would go unnoticed.
        /// </summary>
        private void ResolveGitDirectories()
        {
            var result = RunGit("rev-parse --absolute-git-dir --git-common-dir");
            if (!result.Success)
            {
                return;
            }

            var lines = result.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                return;
            }

            var gitDir = PathUtil.Normalize(lines[0].Trim());
            var commonDir = lines[1].Trim();
            commonDir = PathUtil.Normalize(Path.IsPathRooted(commonDir) ? commonDir : Path.Combine(Root, commonDir));
            if (gitDir == null || commonDir == null)
            {
                return;
            }

            _gitDir = gitDir;
            _commonDir = commonDir;

            var defaultDir = Path.Combine(Root, ".git");
            var watched = new List<string>();
            foreach (var dir in new[] { commonDir, gitDir })
            {
                if (PathUtil.Comparer.Equals(dir, defaultDir) || watched.Any(w => IsSameOrUnder(dir, w)) || !Directory.Exists(dir))
                {
                    continue;
                }

                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    var watcher = CreateWatcher(dir);
                    _watchers.Add(watcher);
                    watcher.EnableRaisingEvents = true;
                }

                watched.Add(dir);
                Log.Write($"Watching the Git directory of '{Root}': {dir}");
            }
        }

        private static bool IsSameOrUnder(string path, string directory) =>
            PathUtil.Comparer.Equals(path, directory) || GetRelative(path, directory) != null;

        /// <summary>
        /// Files changed between the point where the branch left the base branch and HEAD
        /// ("git diff base...HEAD"). Returns null to keep the previous result when git fails.
        /// </summary>
        private StatusSnapshot GetBranchSnapshot()
        {
            if (!_branchChangesNeeded())
            {
                _loggedBase = null;
                return StatusSnapshot.Empty;
            }

            EnsureBaseResolved();
            var baseRef = _baseRef;
            if (baseRef == null)
            {
                return StatusSnapshot.Empty;
            }

            var result = RunGit($"--no-optional-locks diff --name-status -z --no-ext-diff {baseRef}...HEAD --");
            if (!result.Success)
            {
                if (result.Error != _lastBranchError)
                {
                    _lastBranchError = result.Error;
                    Log.Write($"Failed to compare '{Root}' with {baseRef}: {result.Error}");
                }

                return null;
            }

            _lastBranchError = null;
            return StatusSnapshot.ParseNameStatus(Root, result.Text);
        }

        private void EnsureBaseResolved()
        {
            if (_baseResolved)
            {
                return;
            }

            _baseRef = ResolveBaseRef(out var source);
            _baseResolved = true;

            var description = _baseRef == null ? "(none)" : $"{_baseRef} ({source})";
            if (description != _loggedBase)
            {
                _loggedBase = description;
                Log.Write(_baseRef == null
                    ? $"Branch changes in '{Root}': no base branch found; set one under Tools > Options > Folder Git Status " +
                      $"or with \"git config {BaseBranchConfigKey} <branch>\""
                    : $"Branch changes in '{Root}': comparing with {description}");
            }
        }

        /// <summary>
        /// The base branch set in this repository's git config, then the one in the options (which
        /// applies to every repository, so it may not exist in all of them), then the remote's default
        /// branch, then the first of <see cref="BaseBranchCandidates"/> that exists.
        /// </summary>
        private string ResolveBaseRef(out string source)
        {
            var config = RunGit($"config --get {BaseBranchConfigKey}");
            var configured = config.Success ? config.Text.Trim() : null;
            if (!string.IsNullOrEmpty(configured))
            {
                if (GitOutput.IsSafeRefName(configured) && RefExists(configured))
                {
                    source = "git config";
                    return configured;
                }

                Log.Write($"Base branch \"{configured}\" from the git config not found in '{Root}'");
            }

            configured = _options.BaseBranch?.Trim();
            if (!string.IsNullOrEmpty(configured))
            {
                if (GitOutput.IsSafeRefName(configured) && RefExists(configured))
                {
                    source = "options";
                    return configured;
                }

                Log.Write($"Base branch \"{configured}\" not found in '{Root}'; detecting it automatically");
            }

            source = "detected";
            var originHead = RunGit("symbolic-ref --quiet --short refs/remotes/origin/HEAD");
            if (originHead.Success)
            {
                var name = originHead.Text.Trim();
                if (GitOutput.IsSafeRefName(name) && RefExists(name))
                {
                    return name;
                }
            }

            return BaseBranchCandidates.FirstOrDefault(RefExists);
        }

        private bool RefExists(string name) => RunGit($"rev-parse --verify --quiet {name}^{{commit}}").Success;

        /// <summary>
        /// Writes the version of <paramref name="fullPath"/> at the point where the current branch left
        /// the base branch to a temporary file, for the "Compare with Base Branch" command.
        /// Runs git, so call it from a background thread.
        /// </summary>
        public BaseVersion GetBaseVersion(string fullPath)
        {
            EnsureBaseResolved();
            var baseRef = _baseRef;
            if (baseRef == null)
            {
                throw new InvalidOperationException(
                    $"No base branch found in '{Root}'. Set one under Tools > Options > Folder Git Status, " +
                    $"or run \"git config {BaseBranchConfigKey} <branch>\" in the repository.");
            }

            var mergeBase = RunGit($"merge-base {baseRef} HEAD");
            if (!mergeBase.Success)
            {
                throw new InvalidOperationException($"Could not find where the branch left {baseRef}: {mergeBase.Error}");
            }

            var commit = mergeBase.Text.Trim();
            var relativePath = GitOutput.ToGitPath(Root, fullPath);

            // A file renamed on the branch is compared with its original name.
            var diff = RunGit($"--no-optional-locks diff --name-status -z -M --no-ext-diff {commit} --");
            var basePath = (diff.Success ? GitOutput.FindRenameSource(diff.Text, relativePath) : null) ?? relativePath;

            var content = RunGit($"show {GitOutput.Quote(commit + ":" + basePath)}");
            if (!content.Success)
            {
                // Windows paths are case-insensitive, git paths are not.
                var tree = RunGit($"ls-tree -r -z --name-only {commit}");
                var match = tree.Success
                    ? tree.Text.Split('\0').FirstOrDefault(p => string.Equals(p, basePath, StringComparison.OrdinalIgnoreCase))
                    : null;
                content = match != null ? RunGit($"show {GitOutput.Quote(commit + ":" + match)}") : null;
            }

            var directory = Path.Combine(Path.GetTempPath(), "FolderGitStatus", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(directory);
            var tempFile = Path.Combine(directory, Path.GetFileName(fullPath));
            File.WriteAllBytes(tempFile, content?.Success == true ? content.Output : Array.Empty<byte>());

            return new BaseVersion(tempFile, baseRef, commit.Substring(0, Math.Min(7, commit.Length)),
                existed: content?.Success == true, renamedFrom: basePath != relativePath ? basePath : null);
        }

        private GitProcess.Result RunGit(string arguments)
        {
            var result = GitProcess.Run(_options.GitExecutable, Root, arguments, GitTimeout);
            if (result.TimedOut)
            {
                Log.Write($"'{Root}': {result.Error}");
            }

            return result;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (var watcher in _watchers)
                {
                    watcher.Dispose();
                }
            }

            _debounce.Dispose();
        }
    }

    internal sealed class BaseVersion
    {
        public BaseVersion(string tempFile, string baseRef, string commit, bool existed, string renamedFrom)
        {
            TempFile = tempFile;
            BaseRef = baseRef;
            Commit = commit;
            Existed = existed;
            RenamedFrom = renamedFrom;
        }

        public string TempFile { get; }

        public string BaseRef { get; }

        public string Commit { get; }

        /// <summary>
        /// False when the file was created on the branch (the temporary file is empty).
        /// </summary>
        public bool Existed { get; }

        public string RenamedFrom { get; }
    }
}
