using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace FolderGitStatus
{
    /// <summary>
    /// Solution Explorer has no public API to decorate folders, so the items' WPF containers
    /// are intercepted and the name TextBlock is colored directly.
    /// Containers are virtualized and recycled, so the DataContext is re-evaluated on every change.
    /// </summary>
    internal sealed class SolutionExplorerDecorator
    {
        private static readonly DependencyProperty IsHookedProperty =
            DependencyProperty.RegisterAttached("IsHooked", typeof(bool), typeof(SolutionExplorerDecorator));

        private static readonly DependencyProperty LabelProperty =
            DependencyProperty.RegisterAttached("Label", typeof(TextBlock), typeof(SolutionExplorerDecorator));

        private static readonly string[] WrapperPropertyNames = { "Item", "SourceItem", "Value", "Content" };

        private readonly GitStatusService _git;
        private readonly OptionsPage _options;
        private readonly HierarchyStatusResolver _resolver;
        private readonly JoinableTaskFactory _uiThread;
        private readonly HashSet<FrameworkElement> _containers = new HashSet<FrameworkElement>();
        private readonly Dictionary<Type, PropertyInfo> _wrapperProperties = new Dictionary<Type, PropertyInfo>();
        private readonly HashSet<Type> _reportedTypes = new HashSet<Type>();
        private readonly ConditionalWeakTable<TextBlock, OriginalLook> _originals = new ConditionalWeakTable<TextBlock, OriginalLook>();
        private Brush _addedBrush, _modifiedBrush, _conflictBrush;
        private readonly HashSet<ListBox> _treeViews = new HashSet<ListBox>();
        private readonly HashSet<ListBox> _pendingSweeps = new HashSet<ListBox>();
        private readonly DispatcherTimer _discoveryTimer;
        private int _discoveryAttempts;
        private int _repaintQueued;
        private int _sweepCount, _hookedCount, _paintedCount, _noLabelCount;

        public SolutionExplorerDecorator(GitStatusService git, OptionsPage options, JoinableTaskFactory joinableTaskFactory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _git = git;
            _options = options;
            _resolver = new HierarchyStatusResolver(git, options);

            // "Loaded" priority: runs after the template bindings reflect the current item.
            _uiThread = joinableTaskFactory.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.Loaded);
            UpdateBrushes();

            _git.StatusChanged += (s, e) => QueueRepaint();
            _options.Applied += (s, e) =>
            {
                UpdateBrushes();
                QueueRepaint();
            };

            // The Solution Explorer tree may not exist yet (window closed or solution still loading).
            _discoveryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _discoveryTimer.Tick += (s, e) => DiscoverTreeViews();
            StartDiscovery();
        }

        /// <summary>
        /// Looks for the Solution Explorer tree for a few seconds (called on load and when a solution is opened).
        /// </summary>
        public void StartDiscovery()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _discoveryAttempts = 0;
            DiscoverTreeViews();
            _discoveryTimer.Start();
        }

        public void LogStatistics()
        {
            Log.Debug($"Statistics: trees={_treeViews.Count}, sweeps={_sweepCount}, hooked={_hookedCount}, " +
                $"active={_containers.Count}, painted={_paintedCount}, no label={_noLabelCount}");
        }

        private void DiscoverTreeViews()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var found = false;
            foreach (Window window in Application.Current.Windows)
            {
                foreach (var treeView in VisualTree.Descendants<ListBox>(window).Where(IsHierarchyTreeView).ToList())
                {
                    found = true;
                    TrackTreeView(treeView);
                }
            }

            if (found || ++_discoveryAttempts >= 15)
            {
                _discoveryTimer.Stop();
            }
        }

        // SolutionPivotTreeView : HierarchyPivotTreeView : PivotTreeView : VirtualizingTreeView : ListBox
        private static bool IsHierarchyTreeView(ListBox listBox) =>
            listBox.GetType().Name.IndexOf("PivotTreeView", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// The Loaded event does not reliably reach containers created later (WPF only raises it
        /// for subtrees with instance handlers), so the tree is swept whenever the generator
        /// creates containers: on expand, scroll or solution reload.
        /// </summary>
        private void TrackTreeView(ListBox treeView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!_treeViews.Add(treeView))
            {
                return;
            }

            Log.Debug($"Tree found: {treeView.GetType().FullName}");

            var generator = treeView.ItemContainerGenerator;
            generator.StatusChanged += (s, e) =>
            {
                if (generator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                {
                    QueueSweep(treeView);
                }
            };
            generator.ItemsChanged += (s, e) => QueueSweep(treeView);
            treeView.Loaded += (s, e) => QueueSweep(treeView);
            QueueSweep(treeView);
        }

        private void QueueSweep(ListBox treeView)
        {
            if (_pendingSweeps.Add(treeView))
            {
                RunOnUIThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    _pendingSweeps.Remove(treeView);
                    Sweep(treeView);
                }, "Sweep");
            }
        }

        private void Sweep(ListBox treeView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _sweepCount++;
            foreach (var container in VisualTree.Descendants<ListBoxItem>(treeView).ToList())
            {
                if (!_containers.Contains(container))
                {
                    HookContainer(container);
                }
            }
        }

        private void HookContainer(FrameworkElement container)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(bool)container.GetValue(IsHookedProperty))
            {
                if (!IsSolutionExplorerItem(container))
                {
                    return;
                }

                if (_hookedCount++ < 5)
                {
                    Log.Debug($"Container hooked: {container.GetType().FullName}, DataContext: {container.DataContext?.GetType().FullName}");
                }

                container.SetValue(IsHookedProperty, true);
                container.DataContextChanged += OnContainerDataContextChanged;
                container.Unloaded += (s, _) => _containers.Remove((FrameworkElement)s);
            }

            _containers.Add(container);
            Apply(container);
        }

        private void OnContainerDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var container = (FrameworkElement)sender;
            RunOnUIThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Apply(container);
            }, "Apply");
        }

        private bool IsSolutionExplorerItem(FrameworkElement container)
        {
            if (ResolveHierarchyItem(container.DataContext) != null)
            {
                return true;
            }

            var owner = ItemsControl.ItemsControlFromItemContainer(container);
            if (owner != null && owner.GetType().Name.IndexOf("PivotTreeView", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ReportUnresolvedItem(container, owner);
                return true;
            }

            return false;
        }

        private void QueueRepaint()
        {
            if (Interlocked.Exchange(ref _repaintQueued, 1) == 0)
            {
                RunOnUIThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    _repaintQueued = 0;
                    foreach (var container in _containers.ToList())
                    {
                        Apply(container);
                    }
                }, "Repaint");
            }
        }

        private void RunOnUIThread(Action action, string name)
        {
            _uiThread.RunAsync(async () =>
            {
                await _uiThread.SwitchToMainThreadAsync(alwaysYield: true);
                action();
            }).Task.FileAndForget($"FolderGitStatus/{name}");
        }

        private void Apply(FrameworkElement container)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var item = ResolveHierarchyItem(container.DataContext);
            var label = FindLabel(container, item);
            if (label == null)
            {
                _noLabelCount++;
                return;
            }

            var status = GitStatus.None;
            if (item != null && _options.Enabled)
            {
                try
                {
                    status = _resolver.GetStatus(item);
                    if (status != GitStatus.None && _paintedCount++ < 10)
                    {
                        Log.Debug($"Painting '{item.Text}' as {status}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Failed to resolve '{item.Text}': {ex.Message}");
                }
            }

            Paint(label, status);
        }

        private void Paint(TextBlock label, GitStatus status)
        {
            var brush = status == GitStatus.Conflict ? _conflictBrush
                : status == GitStatus.Modified ? _modifiedBrush
                : status == GitStatus.Added ? _addedBrush
                : null;

            if (brush == null)
            {
                if (_originals.TryGetValue(label, out var original))
                {
                    original.Restore(label);
                    _originals.Remove(label);
                }

                return;
            }

            var look = _originals.GetValue(label, l => new OriginalLook(l));
            label.Foreground = brush;
            if (_options.UseBold)
            {
                label.FontWeight = FontWeights.Bold;
            }
            else
            {
                look.RestoreFontWeight(label);
            }
        }

        /// <summary>
        /// The name TextBlock is located by the item's text the first time and reused
        /// as long as the container's template stays the same.
        /// </summary>
        private static TextBlock FindLabel(FrameworkElement container, IVsHierarchyItem item)
        {
            if (container.GetValue(LabelProperty) is TextBlock cached && cached.IsDescendantOf(container))
            {
                return cached;
            }

            if (item == null)
            {
                return null;
            }

            var label = VisualTree.Descendants<TextBlock>(container).FirstOrDefault(t => string.Equals(t.Text, item.Text, StringComparison.Ordinal))
                ?? VisualTree.Descendants<TextBlock>(container).FirstOrDefault(t => !string.IsNullOrEmpty(t.Text));

            if (label != null)
            {
                container.SetValue(LabelProperty, label);
            }

            return label;
        }

        /// <summary>
        /// The DataContext arrives wrapped; in VS 2022/2026 the chain is
        /// VirtualizingTreeView.TreeNode.Item → AggregateAttachedCollectionSource.SourceItem → IVsHierarchyItem.
        /// </summary>
        private IVsHierarchyItem ResolveHierarchyItem(object dataContext)
        {
            var value = dataContext;
            for (var level = 0; level < 4 && value != null; level++)
            {
                if (value is IVsHierarchyItem item)
                {
                    return item;
                }

                var type = value.GetType();
                if (!_wrapperProperties.TryGetValue(type, out var property))
                {
                    property = FindWrapperProperty(type);
                    _wrapperProperties[type] = property;
                }

                try
                {
                    value = property?.GetValue(value);
                }
                catch (TargetInvocationException)
                {
                    return null;
                }
            }

            return null;
        }

        private static PropertyInfo FindWrapperProperty(Type type)
        {
            foreach (var name in WrapperPropertyNames)
            {
                var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0);

                if (property != null && (property.PropertyType == typeof(object) || typeof(IVsHierarchyItem).IsAssignableFrom(property.PropertyType)))
                {
                    return property;
                }
            }

            return null;
        }

        private void ReportUnresolvedItem(FrameworkElement container, ItemsControl owner)
        {
            var dataContextType = container.DataContext?.GetType();
            if (dataContextType == null || !_reportedTypes.Add(dataContextType))
            {
                return;
            }

            var properties = string.Join(", ", dataContextType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => $"{p.Name}: {p.PropertyType.Name}"));

            Log.Debug($"Unrecognized Solution Explorer item. Container: {container.GetType().FullName}; " +
                $"Owner: {owner.GetType().FullName}; DataContext: {dataContextType.FullName} {{ {properties} }}");
        }

        private void UpdateBrushes()
        {
            _addedBrush = ToBrush(_options.AddedColor);
            _modifiedBrush = ToBrush(_options.ModifiedColor);
            _conflictBrush = ToBrush(_options.ConflictColor);
        }

        private static Brush ToBrush(System.Drawing.Color color)
        {
            var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// The TextBlock's local values before painting, restored when the item no longer has changes.
        /// </summary>
        private sealed class OriginalLook
        {
            private readonly object _foreground;
            private readonly object _fontWeight;

            public OriginalLook(TextBlock label)
            {
                _foreground = label.ReadLocalValue(TextBlock.ForegroundProperty);
                _fontWeight = label.ReadLocalValue(TextBlock.FontWeightProperty);
            }

            public void Restore(TextBlock label)
            {
                Restore(label, TextBlock.ForegroundProperty, _foreground);
                Restore(label, TextBlock.FontWeightProperty, _fontWeight);
            }

            public void RestoreFontWeight(TextBlock label) => Restore(label, TextBlock.FontWeightProperty, _fontWeight);

            private static void Restore(DependencyObject target, DependencyProperty property, object value)
            {
                if (value == DependencyProperty.UnsetValue)
                {
                    target.ClearValue(property);
                }
                else if (value is BindingExpressionBase binding)
                {
                    BindingOperations.SetBinding(target, property, binding.ParentBindingBase);
                }
                else
                {
                    target.SetValue(property, value);
                }
            }
        }
    }
}
