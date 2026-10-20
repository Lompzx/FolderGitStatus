using System;
using System.Collections.Generic;
using System.IO;

namespace FolderGitStatus
{
    /// <summary>
    /// Discovers on demand the repositories of the paths shown in Solution Explorer
    /// (supports multiple repositories and submodules) and answers the status of files and folders.
    /// </summary>
    internal sealed class GitStatusService : IDisposable
    {
        private readonly Func<string> _gitExecutable;
        private readonly object _gate = new object();

        // null value = path outside any repository
        private readonly Dictionary<string, string> _rootByDirectory = new Dictionary<string, string>(PathUtil.Comparer);
        private readonly Dictionary<string, GitRepository> _repositories = new Dictionary<string, GitRepository>(PathUtil.Comparer);

        public GitStatusService(Func<string> gitExecutable)
        {
            _gitExecutable = gitExecutable;
        }

        /// <summary>
        /// Raised (on a background thread) when the status of any repository changes.
        /// </summary>
        public event EventHandler StatusChanged;

        /// <param name="path">Normalized path (<see cref="PathUtil.Normalize"/>).</param>
        /// <param name="includeFiles">If false, only folders get a status.</param>
        public GitStatus GetStatus(string path, bool includeFiles)
        {
            var snapshot = FindRepository(path)?.Snapshot;
            if (snapshot == null)
            {
                return GitStatus.None;
            }

            var status = snapshot.GetDirectoryStatus(path);
            return status == GitStatus.None && includeFiles ? snapshot.GetFileStatus(path) : status;
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
                    repository = new GitRepository(root, _gitExecutable);
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
            }

            return root;
        }
    }
}
