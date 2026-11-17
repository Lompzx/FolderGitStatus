using System.Collections.Generic;

namespace FolderGitStatus
{
    /// <summary>
    /// Ordered by priority: a folder takes the highest status among its descendants.
    /// </summary>
    internal enum GitStatus
    {
        None = 0,
        Added = 1,
        Modified = 2,
        Conflict = 3,
    }

    /// <summary>
    /// Status of a file or folder in one snapshot: the most severe kind, whether every change
    /// is fully staged, and (for folders) how many changed files of each kind are inside it.
    /// Immutable once the snapshot is built.
    /// </summary>
    internal sealed class PathStatus
    {
        public GitStatus Kind { get; private set; }

        /// <summary>
        /// True when every change is in the index and the working tree matches it.
        /// </summary>
        public bool Staged { get; private set; } = true;

        public int Added { get; private set; }

        public int Modified { get; private set; }

        public int Conflict { get; private set; }

        public int FileCount => Added + Modified + Conflict;

        internal void Include(GitStatus kind, bool staged)
        {
            if (kind > Kind)
            {
                Kind = kind;
            }

            Staged &= staged;
            switch (kind)
            {
                case GitStatus.Added: Added++; break;
                case GitStatus.Modified: Modified++; break;
                case GitStatus.Conflict: Conflict++; break;
            }
        }

        /// <summary>
        /// "2 modified, 1 new".
        /// </summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Conflict > 0)
            {
                parts.Add($"{Conflict} with conflicts");
            }

            if (Modified > 0)
            {
                parts.Add($"{Modified} modified");
            }

            if (Added > 0)
            {
                parts.Add($"{Added} new");
            }

            return string.Join(", ", parts);
        }

        public bool SameAs(PathStatus other) =>
            other != null && Kind == other.Kind && Staged == other.Staged &&
            Added == other.Added && Modified == other.Modified && Conflict == other.Conflict;
    }

    /// <summary>
    /// Status shown for a Solution Explorer item: the kind of change, whether it is only in
    /// commits of the current branch (not in the working tree or the index), whether it is
    /// fully staged, and whether the item is ignored by Git.
    /// </summary>
    internal readonly struct ItemStatus
    {
        public static readonly ItemStatus None = default;

        public ItemStatus(GitStatus kind, bool committed, bool staged = false, bool ignored = false,
            PathStatus pending = null, PathStatus branch = null, bool isFile = false, bool aggregate = false, string baseRef = null)
        {
            Kind = kind;
            Committed = committed && kind != GitStatus.None;
            Staged = staged && kind != GitStatus.None && !committed;
            Ignored = ignored && kind == GitStatus.None;
            Pending = pending;
            Branch = branch;
            IsFile = isFile;
            Aggregate = aggregate;
            BaseRef = baseRef;
        }

        public static ItemStatus IgnoredItem => new ItemStatus(GitStatus.None, false, ignored: true);

        /// <summary>
        /// True when the status is of a file (not of a folder, project or solution).
        /// </summary>
        public bool IsFile { get; }

        /// <summary>
        /// True for solution folders: the status is the most relevant of their children,
        /// so <see cref="Pending"/> and <see cref="Branch"/> do not describe the whole folder.
        /// </summary>
        public bool Aggregate { get; }

        /// <summary>
        /// Base branch of the repository when <see cref="Branch"/> is set.
        /// </summary>
        public string BaseRef { get; }

        /// <summary>
        /// The same status, marked as the summary of a solution folder.
        /// </summary>
        public ItemStatus AsAggregate() =>
            IsNone ? this : new ItemStatus(Kind, Committed, Staged, Ignored, null, null, isFile: false, aggregate: true, baseRef: BaseRef);

        /// <summary>
        /// Number of changed files the status refers to (0 when unknown, e.g. for solution folders).
        /// </summary>
        public int FileCount => Aggregate ? 0 : (Committed ? Branch : Pending)?.FileCount ?? 0;

        public string Letter
        {
            get
            {
                switch (Kind)
                {
                    case GitStatus.Conflict: return "C";
                    case GitStatus.Modified: return "M";
                    case GitStatus.Added: return "A";
                    default: return null;
                }
            }
        }

        /// <summary>
        /// Tooltip text: what is pending and what the branch committed.
        /// </summary>
        public string Describe()
        {
            if (IsNone)
            {
                return Ignored ? "Ignored by Git" : null;
            }

            if (Aggregate)
            {
                var what = Kind == GitStatus.Conflict ? "files with conflicts" : Kind == GitStatus.Modified ? "modified files" : "new files";
                return Committed ? $"Contains {what} committed on the branch" : $"Contains {what}";
            }

            var lines = new List<string>();
            if (Pending != null)
            {
                var staged = Pending.Staged ? (IsFile ? " (staged)" : " (all staged)") : string.Empty;
                lines.Add(IsFile ? $"Uncommitted: {KindName(Pending.Kind)}{staged}" : $"Uncommitted: {Pending.Describe()}{staged}");
            }

            if (Branch != null)
            {
                var against = BaseRef != null ? $" (vs {BaseRef})" : string.Empty;
                lines.Add(IsFile ? $"Changed on the branch{against}: {KindName(Branch.Kind)}" : $"Changed on the branch{against}: {Branch.Describe()}");
            }

            return string.Join("\n", lines);
        }

        private static string KindName(GitStatus kind) =>
            kind == GitStatus.Conflict ? "conflict" : kind == GitStatus.Modified ? "modified" : "new";

        public GitStatus Kind { get; }

        /// <summary>
        /// True when the change was committed on the current branch and is not pending anymore.
        /// </summary>
        public bool Committed { get; }

        /// <summary>
        /// True when every uncommitted change of the item is staged.
        /// </summary>
        public bool Staged { get; }

        /// <summary>
        /// True when the item has no changes and is ignored by Git (.gitignore).
        /// </summary>
        public bool Ignored { get; }

        /// <summary>
        /// Uncommitted changes (null if none).
        /// </summary>
        public PathStatus Pending { get; }

        /// <summary>
        /// Changes committed on the branch (null if none or with "Show branch changes" off).
        /// </summary>
        public PathStatus Branch { get; }

        public bool IsNone => Kind == GitStatus.None;

        /// <summary>
        /// Uncommitted changes win over committed ones; within the same group the more severe kind wins.
        /// </summary>
        public bool IsMoreRelevantThan(ItemStatus other)
        {
            if (IsNone || other.IsNone)
            {
                return !IsNone && other.IsNone;
            }

            return Committed != other.Committed ? !Committed : Kind > other.Kind;
        }

        public override string ToString()
        {
            if (Ignored)
            {
                return "Ignored";
            }

            return Committed ? $"{Kind} (committed on the branch)" : Staged ? $"{Kind} (staged)" : Kind.ToString();
        }
    }
}
