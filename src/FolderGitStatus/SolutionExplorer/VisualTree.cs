using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace FolderGitStatus
{
    internal static class VisualTree
    {
        public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!(current is Visual) && !(current is System.Windows.Media.Media3D.Visual3D))
                {
                    continue;
                }

                for (var i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--)
                {
                    var child = VisualTreeHelper.GetChild(current, i);
                    if (child is T match)
                    {
                        yield return match;
                    }

                    pending.Push(child);
                }
            }
        }
    }
}
