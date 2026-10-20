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
}
