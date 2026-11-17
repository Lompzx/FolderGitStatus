using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
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

        private static readonly DependencyProperty IsTrackedProperty =
            DependencyProperty.RegisterAttached("IsTracked", typeof(bool), typeof(SolutionExplorerDecorator));

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
        private readonly ConditionalWeakTable<TextBlock, BadgeAdorner> _badges = new ConditionalWeakTable<TextBlock, BadgeAdorner>();
        private const double IgnoredOpacity = 0.45;
        private Brush _addedBrush, _modifiedBrush, _conflictBrush;
        private Brush _committedAddedBrush, _committedModifiedBrush, _committedConflictBrush;
        private readonly HashSet<ListBox> _pendingSweeps = new HashSet<ListBox>();
        private static readonly TimeSpan FastDiscoveryInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan SlowDiscoveryInterval = TimeSpan.FromSeconds(10);
        private const int FastDiscoveryAttempts = 15;

        private readonly DispatcherTimer _discoveryTimer;
        private readonly Stopwatch _discoveryClock = new Stopwatch();
        private int _discoveryAttempts;
        private bool _discoveryQuiet;
        private int _repaintQueued;
        private int _treeCount, _sweepCount, _hookedCount, _paintedCount, _noLabelCount;

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
            _discoveryTimer = new DispatcherTimer(DispatcherPriority.Background);
            _discoveryTimer.Tick += (s, e) => DiscoverTreeViews();
            StartDiscovery("package loaded");
        }

        /// <summary>
        /// Looks for the Solution Explorer tree until it is found (called on load and when a solution
        /// or folder is opened). The first attempts are frequent; after that it keeps looking less often,
        /// because the first launch after an install can take a long time while VS rebuilds its caches.
        /// </summary>
        /// <param name="quiet">Only log the search with "Diagnostic log" on (used when the tree is
        /// merely hidden, e.g. another tab of its dock group was selected).</param>
        public void StartDiscovery(string reason, bool quiet = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _discoveryQuiet = quiet;
            LogDiscovery($"Looking for the Solution Explorer tree ({reason})");
            _discoveryTimer.Stop();
            _discoveryTimer.Interval = FastDiscoveryInterval;
            _discoveryAttempts = 0;
            _discoveryClock.Restart();

            if (!DiscoverTreeViews())
            {
                _discoveryTimer.Start();
            }
        }

        public void LogStatistics()
        {
            Log.Debug($"Statistics: trees={_treeCount}, sweeps={_sweepCount}, hooked={_hookedCount}, " +
                $"active={_containers.Count}, painted={_paintedCount}, no label={_noLabelCount}");
        }

        private bool DiscoverTreeViews()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _discoveryAttempts++;
            var found = FindAndTrackTrees(treeView =>
                Log.Write($"Solution Explorer tree found after {_discoveryClock.Elapsed.TotalSeconds:0.0}s " +
                    $"({_discoveryAttempts} attempt(s)): {treeView.GetType().FullName}"));

            if (found)
            {
                _discoveryTimer.Stop();
            }
            else if (_discoveryAttempts == FastDiscoveryAttempts)
            {
                LogDiscovery($"Solution Explorer tree not found after {_discoveryClock.Elapsed.TotalSeconds:0}s; " +
                    $"checking every {SlowDiscoveryInterval.TotalSeconds:0}s");
                _discoveryTimer.Interval = SlowDiscoveryInterval;
            }

            return found;
        }

        /// <returns>True if any Solution Explorer tree exists (tracked before or now).</returns>
        private bool FindAndTrackTrees(Action<ListBox> onNewTree)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var found = false;
            foreach (Window window in Application.Current.Windows)
            {
                foreach (var treeView in VisualTree.Descendants<ListBox>(window).Where(IsHierarchyTreeView).ToList())
                {
                    found = true;
                    if (TrackTreeView(treeView))
                    {
                        onNewTree(treeView);
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Called when a tool window is shown. Each "New Solution Explorer View" window has its own
        /// tree, which the discovery (that stops at the first tree) would not find. The window's content
        /// may load a moment after it is shown, so it is looked up twice.
        /// </summary>
        public void ScanForNewTrees(string reason)
        {
            foreach (var delay in new[] { 300, 1500 })
            {
                _uiThread.RunAsync(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(delay);
                    await _uiThread.SwitchToMainThreadAsync(alwaysYield: true);
                    FindAndTrackTrees(treeView => Log.Write($"Another Solution Explorer tree found ({reason}): {treeView.GetType().FullName}"));
                }).Task.FileAndForget("FolderGitStatus/ScanForNewTrees");
            }
        }

        private void LogDiscovery(string message)
        {
            if (_discoveryQuiet)
            {
                Log.Debug(message);
            }
            else
            {
                Log.Write(message);
            }
        }

        /// <summary>
        /// VS can discard the Solution Explorer tree and create a new one (e.g. when it rebuilds the
        /// window layout on the first launch after an update, or when the window is redocked), and
        /// the new tree would never be colored. Whenever a tracked tree leaves the screen the search
        /// starts again; if it was only hidden, it ends as soon as the same tree is shown again.
        /// </summary>
        private void OnTreeViewUnloaded(ListBox treeView)
        {
            RunOnUIThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!treeView.IsLoaded && !_discoveryTimer.IsEnabled)
                {
                    StartDiscovery("tree left the screen", quiet: true);
                }
            }, "Rediscover");
        }

        // SolutionPivotTreeView : HierarchyPivotTreeView : PivotTreeView : VirtualizingTreeView : ListBox
        private static bool IsHierarchyTreeView(ListBox listBox) =>
            listBox.GetType().Name.IndexOf("PivotTreeView", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// The Loaded event does not reliably reach containers created later (WPF only raises it
        /// for subtrees with instance handlers), so the tree is swept whenever the generator
        /// creates containers: on expand, scroll or solution reload.
        /// </summary>
        /// <returns>True if the tree was not tracked yet.</returns>
        private bool TrackTreeView(ListBox treeView)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if ((bool)treeView.GetValue(IsTrackedProperty))
            {
                return false;
            }

            treeView.SetValue(IsTrackedProperty, true);
            _treeCount++;

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
            treeView.Unloaded += (s, e) => OnTreeViewUnloaded(treeView);
            treeView.IsKeyboardFocusWithinChanged += (s, e) => OnTreeViewFocusChanged(treeView);
            QueueSweep(treeView);
            return true;
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
                if (container is ListBoxItem item)
                {
                    item.Selected += OnContainerSelectionChanged;
                    item.Unselected += OnContainerSelectionChanged;
                }
            }

            _containers.Add(container);
            Apply(container);
        }

        private void OnContainerSelectionChanged(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, sender))
            {
                // After the selection style has been applied.
                var container = (FrameworkElement)sender;
                RunOnUIThread(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    Apply(container);
                }, "SelectionApply");
            }
        }

        /// <summary>
        /// The selection highlight changes when the tree gains or loses the focus.
        /// </summary>
        private void OnTreeViewFocusChanged(ListBox treeView)
        {
            RunOnUIThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                foreach (var container in _containers.OfType<ListBoxItem>().Where(c => c.IsSelected).ToList())
                {
                    Apply(container);
                }
            }, "SelectionRepaint");
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

            var status = ItemStatus.None;
            if (item != null && _options.Enabled)
            {
                try
                {
                    status = _resolver.GetStatus(item);
                    if (!status.IsNone && _paintedCount++ < 10)
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

        private void Paint(TextBlock label, ItemStatus status)
        {
            var brush = GetBrush(status);

            // On a strong selection highlight (e.g. blue in the Dark and Blue themes) the status
            // color would be unreadable, so the text keeps the theme's color there.
            var readable = brush == null || IsReadable(label, brush);
            var values = new Dictionary<DependencyProperty, object>
            {
                [TextBlock.ForegroundProperty] = readable ? brush : null,
                [TextBlock.FontWeightProperty] = _options.UseBold && brush != null ? (object)FontWeights.Bold : null,
                [TextBlock.FontStyleProperty] = _options.ItalicStaged && status.Staged ? (object)FontStyles.Italic : null,
                [UIElement.OpacityProperty] = status.Ignored ? (object)IgnoredOpacity : null,
                [FrameworkElement.ToolTipProperty] = _options.ShowTooltip ? status.Describe() : null,
            };

            if (values.Values.All(v => v == null))
            {
                if (_originals.TryGetValue(label, out var original))
                {
                    original.Restore(label);
                    _originals.Remove(label);
                }
            }
            else
            {
                var look = _originals.GetValue(label, l => new OriginalLook(l));
                foreach (var pair in values)
                {
                    look.Set(label, pair.Key, pair.Value);
                }
            }

            UpdateBadge(label, GetBadgeText(status), readable ? brush : label.Foreground);
        }

        /// <summary>
        /// On a selection highlight the status color is kept only if it stays about as readable as on
        /// the tree's own background, or readable enough on its own (contrast ratio as in WCAG).
        /// Subtle highlights (light gray in the VS 2026 themes) keep the color; strong ones (blue in
        /// the Dark and Blue themes) fall back to the theme's text color.
        /// </summary>
        private const double MinimumContrast = 2.5;
        private const double MinimumRelativeContrast = 0.75;

        private static bool IsReadable(TextBlock label, Brush brush)
        {
            if (!(brush is SolidColorBrush solid))
            {
                return true;
            }

            FindBackgrounds(label, out var item, out var tree);
            if (item == null || tree == null || item.Value == tree.Value)
            {
                return true;
            }

            var onItem = Contrast(solid.Color, item.Value);
            return onItem >= MinimumContrast || onItem >= Contrast(solid.Color, tree.Value) * MinimumRelativeContrast;
        }

        /// <summary>
        /// The first opaque background between the label and the tree (the selection or hover
        /// highlight, if any) and the background the tree is drawn on.
        /// </summary>
        private static void FindBackgrounds(DependencyObject label, out Color? item, out Color? tree)
        {
            item = null;
            tree = null;
            var insideItem = true;

            for (var current = VisualTreeHelper.GetParent(label); current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is ListBox)
                {
                    insideItem = false;
                }

                Brush background;
                switch (current)
                {
                    case Panel panel: background = panel.Background; break;
                    case Border border: background = border.Background; break;
                    case Control control: background = control.Background; break;
                    default: background = null; break;
                }

                if (background is SolidColorBrush solid && solid.Color.A >= 0x80 && solid.Opacity >= 0.5)
                {
                    if (insideItem)
                    {
                        item = item ?? solid.Color;
                    }
                    else
                    {
                        tree = solid.Color;
                        return;
                    }
                }
            }
        }

        private static double Contrast(Color foreground, Color background)
        {
            var a = foreground.A / 255.0;
            var blended = Color.FromRgb(
                (byte)Math.Round(foreground.R * a + background.R * (1 - a)),
                (byte)Math.Round(foreground.G * a + background.G * (1 - a)),
                (byte)Math.Round(foreground.B * a + background.B * (1 - a)));

            double l1 = Luminance(blended), l2 = Luminance(background);
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }

        private static double Luminance(Color color)
        {
            double Channel(byte value)
            {
                var c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }

        private string GetBadgeText(ItemStatus status)
        {
            switch (_options.Badge)
            {
                case BadgeStyle.Letter:
                    return status.Letter;
                case BadgeStyle.Count:
                    var count = status.FileCount;
                    return status.IsFile || count == 0 ? status.Letter : count.ToString(System.Globalization.CultureInfo.CurrentCulture);
                default:
                    return null;
            }
        }

        private void UpdateBadge(TextBlock label, string text, Brush brush)
        {
            _badges.TryGetValue(label, out var badge);
            if (text == null || brush == null)
            {
                badge?.Update(null, null);
                return;
            }

            if (badge == null)
            {
                var layer = AdornerLayer.GetAdornerLayer(label);
                if (layer == null)
                {
                    return;
                }

                badge = new BadgeAdorner(label);
                layer.Add(badge);
                _badges.Add(label, badge);
            }

            badge.Update(text, brush);
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

        private Brush GetBrush(ItemStatus status)
        {
            switch (status.Kind)
            {
                case GitStatus.Conflict: return status.Committed ? _committedConflictBrush : _conflictBrush;
                case GitStatus.Modified: return status.Committed ? _committedModifiedBrush : _modifiedBrush;
                case GitStatus.Added: return status.Committed ? _committedAddedBrush : _addedBrush;
                default: return null;
            }
        }

        /// <summary>
        /// Committed branch changes use the same colors with reduced opacity, so they blend
        /// towards the background in both light and dark themes.
        /// </summary>
        private void UpdateBrushes()
        {
            var committedOpacity = Math.Max(10, Math.Min(100, _options.CommittedOpacity)) / 100.0;

            _addedBrush = ToBrush(_options.AddedColor, 1);
            _modifiedBrush = ToBrush(_options.ModifiedColor, 1);
            _conflictBrush = ToBrush(_options.ConflictColor, 1);
            _committedAddedBrush = ToBrush(_options.AddedColor, committedOpacity);
            _committedModifiedBrush = ToBrush(_options.ModifiedColor, committedOpacity);
            _committedConflictBrush = ToBrush(_options.ConflictColor, committedOpacity);
        }

        private static Brush ToBrush(System.Drawing.Color color, double opacity)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.A * opacity), color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// The TextBlock's local values before painting, restored when the item no longer has changes.
        /// </summary>
        private sealed class OriginalLook
        {
            private static readonly DependencyProperty[] Properties =
            {
                TextBlock.ForegroundProperty,
                TextBlock.FontWeightProperty,
                TextBlock.FontStyleProperty,
                UIElement.OpacityProperty,
                FrameworkElement.ToolTipProperty,
            };

            private readonly Dictionary<DependencyProperty, object> _values = new Dictionary<DependencyProperty, object>();

            public OriginalLook(TextBlock label)
            {
                foreach (var property in Properties)
                {
                    _values[property] = label.ReadLocalValue(property);
                }
            }

            public void Restore(TextBlock label)
            {
                foreach (var pair in _values)
                {
                    Restore(label, pair.Key, pair.Value);
                }
            }

            /// <summary>
            /// Sets the value, or restores the original one when <paramref name="value"/> is null.
            /// </summary>
            public void Set(TextBlock label, DependencyProperty property, object value)
            {
                if (value != null)
                {
                    if (!Equals(label.ReadLocalValue(property), value))
                    {
                        label.SetValue(property, value);
                    }
                }
                else if (_values.TryGetValue(property, out var original))
                {
                    Restore(label, property, original);
                }
            }

            private static void Restore(DependencyObject target, DependencyProperty property, object value)
            {
                var current = target.ReadLocalValue(property);
                if (value == DependencyProperty.UnsetValue)
                {
                    if (current != DependencyProperty.UnsetValue)
                    {
                        target.ClearValue(property);
                    }
                }
                else if (value is BindingExpressionBase binding)
                {
                    if (!(current is BindingExpressionBase currentBinding) || currentBinding.ParentBindingBase != binding.ParentBindingBase)
                    {
                        BindingOperations.SetBinding(target, property, binding.ParentBindingBase);
                    }
                }
                else if (!Equals(current, value))
                {
                    target.SetValue(property, value);
                }
            }
        }
    }
}
