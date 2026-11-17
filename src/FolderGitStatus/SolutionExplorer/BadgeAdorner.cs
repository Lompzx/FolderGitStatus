using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FolderGitStatus
{
    /// <summary>
    /// Draws a short text ("M", "3") right after the name of a Solution Explorer item.
    /// It is an adorner, so the item template is left untouched; the adorner layer of the
    /// tree's ScrollContentPresenter clips it to the visible area.
    /// </summary>
    internal sealed class BadgeAdorner : Adorner
    {
        private const double Spacing = 6;

        private string _text;
        private string _name;
        private Brush _brush;

        public BadgeAdorner(TextBlock label)
            : base(label)
        {
            IsHitTestVisible = false;
        }

        public void Update(string text, Brush brush)
        {
            // The container may have been recycled for another item with the same badge, so the
            // position (after the name) is recomputed when the name changes too.
            var name = (AdornedElement as TextBlock)?.Text;
            if (_text == text && Equals(_brush, brush) && _name == name)
            {
                return;
            }

            _text = text;
            _brush = brush;
            _name = name;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (string.IsNullOrEmpty(_text) || _brush == null || !(AdornedElement is TextBlock label))
            {
                return;
            }

            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // The TextBlock may be wider than its text (stretched in its column), so the text is measured.
            var name = new FormattedText(label.Text ?? string.Empty, CultureInfo.CurrentUICulture, label.FlowDirection,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, _brush, pixelsPerDip);

            var badge = new FormattedText(_text, CultureInfo.CurrentUICulture, label.FlowDirection,
                new Typeface(label.FontFamily, FontStyles.Normal, FontWeights.SemiBold, label.FontStretch),
                Math.Max(8, label.FontSize - 1), _brush, pixelsPerDip);

            var x = Math.Min(name.WidthIncludingTrailingWhitespace + label.Padding.Left, label.ActualWidth) + Spacing;
            var y = Math.Max(0, (label.ActualHeight - badge.Height) / 2);
            drawingContext.DrawText(badge, new Point(x, y));
        }
    }
}
