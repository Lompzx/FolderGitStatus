using System;
using System.IO;
using System.Linq;

namespace FolderGitStatus
{
    internal static class PathUtil
    {
        public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

        /// <summary>
        /// Absolute path, with '\' and no trailing separator (except for a drive root, e.g. "C:\").
        /// Returns null if it is not a valid absolute path.
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                if (!Path.IsPathRooted(path))
                {
                    return null;
                }

                var full = Path.GetFullPath(path.Replace('/', '\\'));
                return full.Length > 3 ? full.TrimEnd('\\') : full;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        /// <summary>
        /// The path with the casing it has on disk (Visual Studio reports some paths in lowercase,
        /// and git paths are case-sensitive). Returns the path unchanged if it does not exist.
        /// </summary>
        public static string GetActualCase(string path)
        {
            try
            {
                var parent = Path.GetDirectoryName(path);
                if (parent == null)
                {
                    return path.ToUpperInvariant();
                }

                var name = Path.GetFileName(path);
                var actualParent = GetActualCase(parent);
                var entry = new DirectoryInfo(actualParent).EnumerateFileSystemInfos(name).FirstOrDefault();
                return Path.Combine(actualParent, entry?.Name ?? name);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                return path;
            }
        }
    }
}
