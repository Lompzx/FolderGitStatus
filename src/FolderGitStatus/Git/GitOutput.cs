using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderGitStatus
{
    /// <summary>
    /// Parsing helpers for git output and for values that go into git command lines.
    /// </summary>
    internal static class GitOutput
    {
        /// <summary>
        /// Folders ignored by default by the file watcher (see the "Ignored folders" option).
        /// </summary>
        public const string DefaultIgnoredFolders = "bin;obj;.vs;node_modules";

        internal readonly struct NameStatus
        {
            public NameStatus(char letter, string path, string sourcePath)
            {
                Letter = letter;
                Path = path;
                SourcePath = sourcePath;
            }

            public char Letter { get; }

            /// <summary>
            /// Path relative to the repository root, with '/'.
            /// </summary>
            public string Path { get; }

            /// <summary>
            /// For renames and copies, the original path; otherwise null.
            /// </summary>
            public string SourcePath { get; }
        }

        /// <summary>
        /// Parses "git diff --name-status -z": each entry is the status letter followed by the path;
        /// renames and copies ("R100", "C75") have the source path first.
        /// </summary>
        public static IEnumerable<NameStatus> ParseNameStatus(string output)
        {
            var entries = output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < entries.Length; i++)
            {
                var letter = entries[i][0];
                string source = null;
                if (letter == 'R' || letter == 'C')
                {
                    if (i + 2 >= entries.Length)
                    {
                        yield break;
                    }

                    source = entries[i + 1];
                    i += 2;
                }
                else
                {
                    if (i + 1 >= entries.Length)
                    {
                        yield break;
                    }

                    i += 1;
                }

                yield return new NameStatus(letter, entries[i], source);
            }
        }

        /// <summary>
        /// The original path of a file renamed in "git diff --name-status -z -M" output, or null.
        /// </summary>
        public static string FindRenameSource(string output, string relativePath) =>
            ParseNameStatus(output)
                .Where(c => c.Letter == 'R' && string.Equals(c.Path, relativePath, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.SourcePath)
                .FirstOrDefault();

        /// <summary>
        /// The name goes into a command line, so only characters valid in Git ref names are accepted.
        /// </summary>
        public static bool IsSafeRefName(string name)
        {
            if (string.IsNullOrEmpty(name) || name[0] == '-' || name.Contains("..") || name.EndsWith("/", StringComparison.Ordinal))
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

        /// <summary>
        /// "bin; obj;.vs" → { @"\bin\", @"\obj\", @"\.vs\" } (path segments matched by the file watcher).
        /// </summary>
        public static string[] ParseIgnoredFolders(string value) =>
            (value ?? string.Empty)
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().Trim('\\', '/'))
                .Where(s => s.Length > 0)
                .Select(s => "\\" + s.Replace('/', '\\') + "\\")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        /// <summary>
        /// Path relative to the repository root as git expects it ("src/App.cs").
        /// </summary>
        public static string ToGitPath(string root, string fullPath) =>
            fullPath.Substring(root.TrimEnd('\\').Length).TrimStart('\\').Replace('\\', '/');

        /// <summary>
        /// Quotes an argument for the Windows command line (paths with spaces).
        /// </summary>
        public static string Quote(string argument) =>
            argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";
    }
}
