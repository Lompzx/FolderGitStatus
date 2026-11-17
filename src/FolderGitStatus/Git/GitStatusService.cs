using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace FolderGitStatus
{
    /// <summary>
    /// Discovers on demand the repositories of the paths shown in Solution Explorer
    /// (supports multiple repositories and submodules) and answers the status of files and folders.
    /// </summary>
    internal sealed class GitStatusService : IDisposable
    {
        /// <summary>
        /// How long a path is remembered as being outside any repository; after that ".git" is
        /// looked up again, so a repository created (git init, clone) while the solution is open is found.
        /// </summary>
        private static readonly TimeSpan NoRepositoryLifetime = TimeSpan.FromSeconds(10);

        private readonly OptionsPage _options;
        private readonly object _gate = new object();

        private readonly Dictionary<string, string> _rootByDirectory = new Dictionary<string, string>(PathUtil.Comparer);
        private readonly HashSet<string> _noRepository = new HashSet<string>(PathUtil.Comparer);
        private readonly Stopwatch _noRepositoryAge = Stopwatch.StartNew();
        private readonly Dictionary<string, GitRepository> _repositories = new Dictionary<string, GitRepository>(PathUtil.Comparer);
        private int _branchChangeRequests;

        public GitStatusService(OptionsPage options)
        {
            _options = options;
        }

        /// <summary>
        /// Raised (on a background thread) when the status of any repository changes.
        /// </summary>
        public event EventHandler StatusChanged;

        /// <summary>
        /// Computes the branch changes even with "Show branch changes" off, until the result is
        /// disposed (used while the "Branch Changes" Solution Explorer filter is on).
        /// </summary>
        public IDisposable RequestBranchChanges()
        {
            if (Interlocked.Increment(ref _branchChangeRequests) == 1 && !_options.ShowBranchChanges)
            {
                Refresh();
            }

            return new Release(() => Interlocked.Decrement(ref _branchChangeRequests));
        }

        private bool BranchChangesNeeded() => _options.ShowBranchChanges || Volatile.Read(ref _branchChangeRequests) > 0;

        private sealed class Release : IDisposable
        {
            private Action _action;

            public Release(Action action) => _action = action;

            public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
        }

        /// <param name="path">Normalized path (<see cref="PathUtil.Normalize"/>).</param>
        /// <param name="includeFiles">If false, only folders get a status.</param>
        /// <remarks>Uncommitted changes win; committed branch changes only show where there are none.</remarks>
        public ItemStatus GetStatus(string path, bool includeFiles)
        {
            var repository = FindRepository(path);
            if (repository == null)
            {
                return ItemStatus.None;
            }

            var pending = Lookup(repository.Snapshot, path, includeFiles, out var pendingIsFile);
            var branchIsFile = false;
            var branch = _options.ShowBranchChanges ? Lookup(repository.BranchSnapshot, path, includeFiles, out branchIsFile) : null;
            var baseRef = branch != null ? repository.BaseRef : null;

            if (pending != null)
            {
                return new ItemStatus(pending.Kind, committed: false, staged: pending.Staged, pending: pending, branch: branch,
                    isFile: pendingIsFile, baseRef: baseRef);
            }

            if (branch != null)
            {
                return new ItemStatus(branch.Kind, committed: true, branch: branch, isFile: branchIsFile, baseRef: baseRef);
            }

            return _options.DimIgnoredItems && repository.Snapshot.IsIgnored(path) ? ItemStatus.IgnoredItem : ItemStatus.None;
        }

        /// <summary>
        /// True if the file has uncommitted changes or was changed by the commits of the branch
        /// (used by the "Branch Changes" filter). Safe to call from any thread.
        /// </summary>
        public bool IsChangedOnBranch(string path)
        {
            var repository = FindRepository(path);
            return repository != null &&
                (repository.Snapshot.GetFile(path) != null || repository.BranchSnapshot.GetFile(path) != null);
        }

        /// <summary>
        /// The repository that contains the path, or null.
        /// </summary>
        public GitRepository GetRepository(string path) => FindRepository(path);

        /// <summary>
        /// Base branches in use ("origin/main"), for the toolbar button's tooltip.
        /// </summary>
        public IReadOnlyList<string> GetBaseBranches()
        {
            lock (_gate)
            {
                return _repositories.Values.Select(r => r.BaseRef).Where(b => b != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        /// <summary>
        /// Recomputes every repository (after the options change).
        /// </summary>
        public void Refresh()
        {
            lock (_gate)
            {
                foreach (var repository in _repositories.Values)
                {
                    repository.Invalidate();
                }
            }
        }

        private static PathStatus Lookup(StatusSnapshot snapshot, string path, bool includeFiles, out bool isFile)
        {
            var directory = snapshot.GetDirectory(path);
            isFile = directory == null && includeFiles;
            return directory ?? (includeFiles ? snapshot.GetFile(path) : null);
        }

        public void Reset()
        {
            lock (_gate)
            {
                foreach (var repository in _repositories.Values)
                {
                    repository.Dispose();
                }

                _repositories.Clear();
                _rootByDirectory.Clear();
                _noRepository.Clear();
            }
        }

        public void Dispose() => Reset();

        private GitRepository FindRepository(string path)
        {
            if (path == null)
            {
                return null;
            }

            lock (_gate)
            {
                var root = FindRepositoryRoot(path);
                if (root == null)
                {
                    return null;
                }

                if (!_repositories.TryGetValue(root, out var repository))
                {
                    repository = new GitRepository(root, _options, BranchChangesNeeded);
                    repository.Changed += (s, e) => StatusChanged?.Invoke(this, EventArgs.Empty);
                    _repositories.Add(root, repository);
                    Log.Write($"Watching repository: {root}");
                    repository.Start();
                }

                return repository;
            }
        }

        private string FindRepositoryRoot(string path)
        {
            if (_noRepository.Count > 0 && _noRepositoryAge.Elapsed > NoRepositoryLifetime)
            {
                foreach (var dir in _noRepository)
                {
                    _rootByDirectory.Remove(dir);
                }

                _noRepository.Clear();
            }

            var visited = new List<string>();
            string root = null;

            for (var dir = path; dir != null; dir = Path.GetDirectoryName(dir))
            {
                if (_rootByDirectory.TryGetValue(dir, out root))
                {
                    break;
                }

                visited.Add(dir);

                // ".git" is a file in worktrees and submodules
                var dotGit = Path.Combine(dir, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit))
                {
                    root = dir;
                    break;
                }
            }

            foreach (var dir in visited)
            {
                _rootByDirectory[dir] = root;
                if (root == null)
                {
                    if (_noRepository.Count == 0)
                    {
                        _noRepositoryAge.Restart();
                    }

                    _noRepository.Add(dir);
                }
            }

            return root;
        }
    }
}
