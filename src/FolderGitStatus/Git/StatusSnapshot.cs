using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderGitStatus
{
    /// <summary>
    /// Immutable result of a "git status": status per file and, already propagated, per folder.
    /// </summary>
    internal sealed class StatusSnapshot
    {
        public static readonly StatusSnapshot Empty = new StatusSnapshot(
            new Dictionary<string, GitStatus>(PathUtil.Comparer),
            new Dictionary<string, GitStatus>(PathUtil.Comparer));

        private readonly Dictionary<string, GitStatus> _files;
        private readonly Dictionary<string, GitStatus> _directories;

        private StatusSnapshot(Dictionary<string, GitStatus> files, Dictionary<string, GitStatus> directories)
        {
            _files = files;
            _directories = directories;
        }

        public GitStatus GetFileStatus(string path) =>
            _files.TryGetValue(path, out var status) ? status : GitStatus.None;

        public GitStatus GetDirectoryStatus(string path) =>
            _directories.TryGetValue(path, out var status) ? status : GitStatus.None;

        public bool HasSameContent(StatusSnapshot other) =>
            SameContent(_files, other._files) && SameContent(_directories, other._directories);

        /// <summary>
        /// Parses the output of "git status --porcelain=v1 -z".
        /// Each entry is "XY path"; for renames/copies the next entry is the source path.
        /// </summary>
        public static StatusSnapshot Parse(string root, string output)
        {
            var files = new Dictionary<string, GitStatus>(PathUtil.Comparer);
            var directories = new Dictionary<string, GitStatus>(PathUtil.Comparer);
            var entries = output.Split(new[] { '\0' }, System.StringSplitOptions.RemoveEmptyEntries);

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

                var status = Classify(x, y);
                var path = Path.Combine(root, entry.Substring(3).Replace('/', '\\')).TrimEnd('\\');

                Merge(files, path, status);
                for (var dir = Path.GetDirectoryName(path); dir != null && dir.Length >= root.Length; dir = Path.GetDirectoryName(dir))
                {
                    Merge(directories, dir, status);
                }
            }

            return new StatusSnapshot(files, directories);
        }

        private static GitStatus Classify(char x, char y)
        {
            if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
            {
                return GitStatus.Conflict;
            }

            return x == '?' || x == 'A' ? GitStatus.Added : GitStatus.Modified;
        }

        private static void Merge(Dictionary<string, GitStatus> map, string path, GitStatus status)
        {
            if (!map.TryGetValue(path, out var current) || status > current)
            {
                map[path] = status;
            }
        }

        private static bool SameContent(Dictionary<string, GitStatus> a, Dictionary<string, GitStatus> b) =>
            a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var status) && status == pair.Value);
    }
}
