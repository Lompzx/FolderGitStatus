using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Internal.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace FolderGitStatus
{
    /// <summary>
    /// "Branch Changes Filter" in the Solution Explorer filter drop-down: shows only the files changed
    /// on the current branch, committed (since it left the base branch) or not. Like the built-in
    /// "Pending Changes Filter", but also with what the branch already committed.
    /// </summary>
    [SolutionTreeFilterProvider(PackageIds.CommandSetString, PackageIds.BranchChangesFilterId)]
    public sealed class BranchChangesFilterProvider : HierarchyTreeFilterProvider
    {
        private readonly IVsHierarchyItemCollectionProvider _collections;

        [ImportingConstructor]
        public BranchChangesFilterProvider(IVsHierarchyItemCollectionProvider collections)
        {
            _collections = collections;
        }

        protected override HierarchyTreeFilter CreateFilter() => new BranchChangesFilter(_collections);

        private sealed class BranchChangesFilter : HierarchyTreeFilter
        {
            private readonly IVsHierarchyItemCollectionProvider _collections;
            private BranchChangesSet _set;

            public BranchChangesFilter(IVsHierarchyItemCollectionProvider collections)
            {
                _collections = collections;
            }

            protected override async Task<IReadOnlyObservableSet> GetIncludedItemsAsync(IEnumerable<IVsHierarchyItem> rootItems)
            {
                var root = HierarchyUtilities.FindCommonAncestor(rootItems);
                var git = await FolderGitStatusPackage.GetGitServiceAsync(CancellationToken);
                var source = await _collections.GetDescendantsAsync(root.HierarchyIdentity.NestedHierarchy, CancellationToken);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken);
                _set?.Dispose();
                _set = new BranchChangesSet(source, git);
                Log.Write("Branch Changes filter on");
                return _set;
            }

            protected override void DisposeManagedResources()
            {
                if (_set != null)
                {
                    _set.Dispose();
                    _set = null;
                    Log.Write("Branch Changes filter off");
                }

                base.DisposeManagedResources();
            }
        }
    }

    /// <summary>
    /// The items of a hierarchy whose files are changed on the branch. Unlike the sets made by
    /// IVsHierarchyItemCollectionProvider, it is re-evaluated when the Git status changes, so the
    /// filtered tree follows edits, commits and checkouts while the filter is on.
    /// </summary>
    internal sealed class BranchChangesSet : IReadOnlyObservableSet<IVsHierarchyItem>, IDisposable
    {
        private readonly IReadOnlyObservableSet<IVsHierarchyItem> _source;
        private readonly GitStatusService _git;
        private readonly IDisposable _branchChanges;
        private readonly HashSet<IVsHierarchyItem> _included = new HashSet<IVsHierarchyItem>();
        private int _reevaluateQueued;
        private bool _disposed;

        public BranchChangesSet(IReadOnlyObservableSet<IVsHierarchyItem> source, GitStatusService git)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _source = source;
            _git = git;

            // The filter needs the branch changes even with "Show branch changes" off.
            _branchChanges = git.RequestBranchChanges();

            foreach (var item in source)
            {
                if (IsIncluded(item))
                {
                    _included.Add(item);
                }
            }

            _source.CollectionChanged += OnSourceChanged;
            _git.StatusChanged += OnStatusChanged;
        }

        public event NotifyCollectionChangedEventHandler CollectionChanged;

        public int Count => _included.Count;

        public bool Contains(IVsHierarchyItem item) => item != null && _included.Contains(item);

        public bool Contains(object item) => Contains(item as IVsHierarchyItem);

        public IEnumerator<IVsHierarchyItem> GetEnumerator() => _included.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private bool IsIncluded(IVsHierarchyItem item)
        {
            try
            {
                var path = PathUtil.Normalize(item.CanonicalName);
                return path != null && _git.IsChangedOnBranch(path);
            }
            catch (Exception ex)
            {
                Log.Debug($"Branch Changes filter: failed to evaluate '{item.Text}': {ex.Message}");
                return false;
            }
        }

        private void OnSourceChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed)
            {
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                Reevaluate();
                return;
            }

            foreach (var item in e.OldItems?.OfType<IVsHierarchyItem>() ?? Enumerable.Empty<IVsHierarchyItem>())
            {
                if (_included.Remove(item))
                {
                    Raise(NotifyCollectionChangedAction.Remove, item);
                }
            }

            foreach (var item in e.NewItems?.OfType<IVsHierarchyItem>() ?? Enumerable.Empty<IVsHierarchyItem>())
            {
                if (IsIncluded(item) && _included.Add(item))
                {
                    Raise(NotifyCollectionChangedAction.Add, item);
                }
            }
        }

        private void OnStatusChanged(object sender, EventArgs e)
        {
            if (System.Threading.Interlocked.Exchange(ref _reevaluateQueued, 1) == 1)
            {
                return;
            }

#pragma warning disable VSSDK007 // fire and forget: errors are reported by FileAndForget
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                _reevaluateQueued = 0;
                Reevaluate();
            }).Task.FileAndForget("FolderGitStatus/BranchFilter");
#pragma warning restore VSSDK007
        }

        private void Reevaluate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed)
            {
                return;
            }

            var included = new HashSet<IVsHierarchyItem>(_source.Where(IsIncluded));

            foreach (var item in _included.Where(i => !included.Contains(i)).ToList())
            {
                _included.Remove(item);
                Raise(NotifyCollectionChangedAction.Remove, item);
            }

            foreach (var item in included.Where(i => !_included.Contains(i)).ToList())
            {
                _included.Add(item);
                Raise(NotifyCollectionChangedAction.Add, item);
            }
        }

        private void Raise(NotifyCollectionChangedAction action, IVsHierarchyItem item) =>
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(action, item));

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _source.CollectionChanged -= OnSourceChanged;
            _git.StatusChanged -= OnStatusChanged;
            _branchChanges.Dispose();
            (_source as IDisposable)?.Dispose();
        }
    }
}
