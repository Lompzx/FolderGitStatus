using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;

namespace FolderGitStatus
{
    /// <summary>
    /// Logs the WPF structure of Solution Explorer (control types, containers and DataContexts)
    /// to find out how items are represented in the running VS version.
    /// </summary>
    internal static class SolutionExplorerProbe
    {
        private const int MaxItemsPerControl = 6;

        public static void Dump(string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var expected = typeof(IVsHierarchyItem);
            Log.Debug($"--- Diagnostics ({reason}) --- expected IVsHierarchyItem: {expected.FullName} [{expected.Assembly.GetName().Name} {expected.Assembly.GetName().Version}]");

            foreach (Window window in Application.Current.Windows)
            {
                var itemsControls = VisualTree.Descendants<ItemsControl>(window).Where(IsInteresting).ToList();
                Log.Debug($"Window {window.GetType().FullName}: {itemsControls.Count} candidate ItemsControl(s)");

                foreach (var itemsControl in itemsControls.Take(10))
                {
                    Log.Debug($"ItemsControl: {Chain(itemsControl.GetType())}; Items={itemsControl.Items.Count}");

                    for (var i = 0; i < Math.Min(itemsControl.Items.Count, MaxItemsPerControl); i++)
                    {
                        var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                        var labels = container == null ? "" : string.Join(" | ", VisualTree.Descendants<TextBlock>(container)
                            .Where(t => !string.IsNullOrEmpty(t.Text)).Select(t => $"{t.GetType().Name}:'{t.Text}'"));

                        Log.Debug($"  [{i}] texts={labels}");
                        var value = container?.DataContext;
                        for (var level = 0; level < 4 && value != null; level++)
                        {
                            Log.Debug($"       level {level}: {Describe(value)}");
                            value = value.GetType().GetProperty("Item", Type.EmptyTypes)?.GetValue(value);
                        }
                    }
                }
            }
        }

        private static bool IsInteresting(ItemsControl control)
        {
            var name = control.GetType().FullName ?? "";
            return control is TreeView
                || name.IndexOf("Pivot", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Solution", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Hierarchy", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Chain(Type type)
        {
            var names = new List<string>();
            for (var t = type; t != null && t != typeof(FrameworkElement); t = t.BaseType)
            {
                names.Add(t.FullName);
            }

            return names.Count == 0 ? "(null)" : string.Join(" : ", names);
        }

        private static string Describe(object value)
        {
            if (value == null)
            {
                return "(null)";
            }

            var type = value.GetType();
            var interfaces = string.Join(", ", type.GetInterfaces()
                .Where(i => i.Name.IndexOf("Hierarchy", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(i => $"{i.FullName} [{i.Assembly.GetName().Name} {i.Assembly.GetName().Version}]"));
            var properties = string.Join(", ", type.GetProperties().Where(p => p.GetIndexParameters().Length == 0)
                .Select(p => $"{p.Name}:{p.PropertyType.Name}"));

            return $"{type.FullName} [{type.Assembly.GetName().Name}] (IVsHierarchyItem={value is IVsHierarchyItem}) " +
                $"interfaces=[{interfaces}] props=[{properties}]";
        }
    }
}
