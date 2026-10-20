using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace FolderGitStatus
{
    /// <summary>
    /// Maps a Solution Explorer node (IVsHierarchy + itemid) to a Git status:
    /// folders and projects use the physical directory; (virtual) solution folders aggregate their children.
    /// </summary>
    internal sealed class HierarchyStatusResolver
    {
        private static readonly Guid SolutionFolderProjectType = new Guid("2150E333-8FDC-42A3-9474-1A3956D46DE8");
        private const int MaxSolutionFolderDepth = 32;

        private readonly GitStatusService _git;
        private readonly OptionsPage _options;

        public HierarchyStatusResolver(GitStatusService git, OptionsPage options)
        {
            _git = git;
            _options = options;
        }

        public GitStatus GetStatus(IVsHierarchyItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var identity = item.HierarchyIdentity;
            if (identity.NestedItemID == VSConstants.VSITEMID_ROOT)
            {
                return GetRootStatus(identity.NestedHierarchy, 0);
            }

            var path = GetCanonicalPath(identity.NestedHierarchy, identity.NestedItemID);
            return _git.GetStatus(path, _options.ColorizeFiles);
        }

        /// <summary>
        /// Root of a hierarchy: solution, project or solution folder.
        /// </summary>
        private GitStatus GetRootStatus(IVsHierarchy hierarchy, int depth)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsSolutionFolder(hierarchy))
            {
                return GetChildrenStatus(hierarchy, VSConstants.VSITEMID_ROOT, depth);
            }

            var path = GetCanonicalPath(hierarchy, VSConstants.VSITEMID_ROOT);
            if (path == null && hierarchy is IVsSolution solution &&
                ErrorHandler.Succeeded(solution.GetSolutionInfo(out var solutionDirectory, out _, out _)))
            {
                path = PathUtil.Normalize(solutionDirectory);
            }

            if (path == null)
            {
                return GitStatus.None;
            }

            // Project/solution: the canonical name is the .csproj/.sln; what matters is its folder.
            if (!Directory.Exists(path))
            {
                path = Path.GetDirectoryName(path);
            }

            return _git.GetStatus(path, includeFiles: false);
        }

        private GitStatus GetChildrenStatus(IVsHierarchy hierarchy, uint parentId, int depth)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (depth > MaxSolutionFolderDepth)
            {
                return GitStatus.None;
            }

            var result = GitStatus.None;
            for (var child = GetItemId(hierarchy, parentId, __VSHPROPID.VSHPROPID_FirstChild);
                 child != VSConstants.VSITEMID_NIL && result != GitStatus.Conflict;
                 child = GetItemId(hierarchy, child, __VSHPROPID.VSHPROPID_NextSibling))
            {
                var nested = GetNestedHierarchy(hierarchy, child);
                var status = nested != null
                    ? GetRootStatus(nested, depth + 1)
                    : _git.GetStatus(GetCanonicalPath(hierarchy, child), includeFiles: true); // "solution items"

                if (status > result)
                {
                    result = status;
                }
            }

            return result;
        }

        /// <summary>
        /// Solution folder (virtual). VS does not always report the TypeGuid (e.g. .slnx in VS 2026),
        /// so any root that is not the solution and whose canonical name is not a path also counts.
        /// </summary>
        private static bool IsSolutionFolder(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ErrorHandler.Succeeded(hierarchy.GetGuidProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_TypeGuid, out var type))
                && type == SolutionFolderProjectType)
            {
                return true;
            }

            return !(hierarchy is IVsSolution) && GetCanonicalPath(hierarchy, VSConstants.VSITEMID_ROOT) == null;
        }

        private static string GetCanonicalPath(IVsHierarchy hierarchy, uint itemId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return ErrorHandler.Succeeded(hierarchy.GetCanonicalName(itemId, out var name))
                ? PathUtil.Normalize(name)
                : null;
        }

        private static IVsHierarchy GetNestedHierarchy(IVsHierarchy hierarchy, uint itemId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var iid = typeof(IVsHierarchy).GUID;
            if (ErrorHandler.Failed(hierarchy.GetNestedHierarchy(itemId, ref iid, out var pointer, out _)) || pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.GetObjectForIUnknown(pointer) as IVsHierarchy;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        private static uint GetItemId(IVsHierarchy hierarchy, uint itemId, __VSHPROPID property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ErrorHandler.Failed(hierarchy.GetProperty(itemId, (int)property, out var value)))
            {
                return VSConstants.VSITEMID_NIL;
            }

            switch (value)
            {
                case int i: return unchecked((uint)i);
                case uint u: return u;
                default: return VSConstants.VSITEMID_NIL;
            }
        }
    }
}
