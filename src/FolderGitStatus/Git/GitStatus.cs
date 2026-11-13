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
    /// Status shown for a Solution Explorer item: the kind of change and whether it is only in
    /// commits of the current branch (not in the working tree or the index).
    /// </summary>
    internal readonly struct ItemStatus
    {
        public static readonly ItemStatus None = default;

        public ItemStatus(GitStatus kind, bool committed)
        {
            Kind = kind;
            Committed = committed && kind != GitStatus.None;
        }

        public GitStatus Kind { get; }

        /// <summary>
        /// True when the change was committed on the current branch and is not pending anymore.
        /// </summary>
        public bool Committed { get; }

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

        public override string ToString() => Committed ? $"{Kind} (committed on the branch)" : Kind.ToString();
    }
}
