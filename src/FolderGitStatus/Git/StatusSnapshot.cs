using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderGitStatus
{
    /// <summary>
    /// Immutable result of a "git status" (or of a "git diff" between commits): status per file
    /// and, already propagated, per folder; plus the paths ignored by Git when they were requested.
    /// </summary>
    internal sealed class StatusSnapshot
    {
        public static readonly StatusSnapshot Empty = new StatusSnapshot(
            new Dictionary<string, PathStatus>(PathUtil.Comparer),
            new Dictionary<string, PathStatus>(PathUtil.Comparer),
            new HashSet<string>(PathUtil.Comparer),
            null);

        private readonly Dictionary<string, PathStatus> _files;
        private readonly Dictionary<string, PathStatus> _directories;
        private readonly HashSet<string> _ignored;
        private readonly string _root;

        private StatusSnapshot(Dictionary<string, PathStatus> files, Dictionary<string, PathStatus> directories,
            HashSet<string> ignored, string root)
        {
            _files = files;
            _directories = directories;
            _ignored = ignored;
            _root = root;
        }

        public int FileCount => _files.Count;

        public PathStatus GetFile(string path) =>
            path != null && _files.TryGetValue(path, out var status) ? status : null;

        public PathStatus GetDirectory(string path) =>
            path != null && _directories.TryGetValue(path, out var status) ? status : null;

        public GitStatus GetFileStatus(string path) => GetFile(path)?.Kind ?? GitStatus.None;

        public GitStatus GetDirectoryStatus(string path) => GetDirectory(path)?.Kind ?? GitStatus.None;

        /// <summary>
        /// True if the path, or a folder above it inside the repository, is ignored by Git.
        /// </summary>
        public bool IsIgnored(string path)
        {
            if (_ignored.Count == 0 || path == null || _root == null)
            {
                return false;
            }

            for (var current = path; current != null && current.Length > _root.Length; current = Path.GetDirectoryName(current))
            {
                if (_ignored.Contains(current))
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasSameContent(StatusSnapshot other) =>
            SameContent(_files, other._files) && SameContent(_directories, other._directories) && _ignored.SetEquals(other._ignored);

        /// <summary>
        /// Parses the output of "git status --porcelain=v1 -z".
        /// Each entry is "XY path"; for renames/copies the next entry is the source path.
        /// "!!" entries (with --ignored) are ignored paths; folders end with '/'.
        /// </summary>
        public static StatusSnapshot Parse(string root, string output)
        {
            var files = new Dictionary<string, PathStatus>(PathUtil.Comparer);
            var directories = new Dictionary<string, PathStatus>(PathUtil.Comparer);
            var ignored = new HashSet<string>(PathUtil.Comparer);
            var entries = output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.Length < 4)
                {
                    continue;
                }

                char x = entry[0], y = entry[1];
                if (x == 'R' || x == 'C' || y == 'R' || y == 'C')
                {
                    i++;
                }

                if (x == '!')
                {
                    ignored.Add(ToFullPath(root, entry.Substring(3)));
                    continue;
                }

                var kind = Classify(x, y);
                var staged = kind != GitStatus.Conflict && x != '?' && x != ' ' && y == ' ';
                Add(files, directories, root, entry.Substring(3), kind, staged);
            }

            return new StatusSnapshot(files, directories, ignored, root);
        }

        /// <summary>
        /// Parses the output of "git diff --name-status -z": each entry is the status letter
        /// followed by the path; renames and copies ("R100", "C75") have the source path first.
        /// A renamed file counts as modified and a copy as added, like in <see cref="Parse"/>.
        /// </summary>
        public static StatusSnapshot ParseNameStatus(string root, string output)
        {
            var files = new Dictionary<string, PathStatus>(PathUtil.Comparer);
            var directories = new Dictionary<string, PathStatus>(PathUtil.Comparer);

            foreach (var change in GitOutput.ParseNameStatus(output))
            {
                Add(files, directories, root, change.Path,
                    change.Letter == 'A' || change.Letter == 'C' ? GitStatus.Added : GitStatus.Modified, staged: false);
            }

            return new StatusSnapshot(files, directories, new HashSet<string>(PathUtil.Comparer), root);
        }

        private static string ToFullPath(string root, string relativePath) =>
            Path.Combine(root, relativePath.Replace('/', '\\')).TrimEnd('\\');

        private static void Add(Dictionary<string, PathStatus> files, Dictionary<string, PathStatus> directories,
            string root, string relativePath, GitStatus status, bool staged)
        {
            var path = ToFullPath(root, relativePath);

            Merge(files, path, status, staged);
            for (var dir = Path.GetDirectoryName(path); dir != null && dir.Length >= root.Length; dir = Path.GetDirectoryName(dir))
            {
                Merge(directories, dir, status, staged);
            }
        }

        private static GitStatus Classify(char x, char y)
        {
            if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
            {
                return GitStatus.Conflict;
            }

            return x == '?' || x == 'A' ? GitStatus.Added : GitStatus.Modified;
        }

        private static void Merge(Dictionary<string, PathStatus> map, string path, GitStatus status, bool staged)
        {
            if (!map.TryGetValue(path, out var current))
            {
                current = new PathStatus();
                map[path] = current;
            }

            current.Include(status, staged);
        }

        private static bool SameContent(Dictionary<string, PathStatus> a, Dictionary<string, PathStatus> b) =>
            a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var status) && status.SameAs(pair.Value));
    }
}
