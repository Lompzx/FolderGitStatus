using System;
using System.IO;

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
    }
}
