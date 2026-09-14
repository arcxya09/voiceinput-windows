using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace RealtimeTranscription.Desktop;

/// <summary>A native XAML action panel that wraps without changing keyboard order.</summary>
public sealed class ResponsivePanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ResponsivePanel),
        new PropertyMetadata(8d, (owner, _) => ((ResponsivePanel)owner).InvalidateMeasure()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : double.PositiveInfinity;
        foreach (var child in Children) child.Measure(new Size(width, double.PositiveInfinity));
        return Layout(width, false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(Math.Max(0, finalSize.Width), true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
        double gap = double.IsFinite(Spacing) ? Math.Max(0, Spacing) : 8;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            double childWidth = Math.Min(width, child.DesiredSize.Width);
            double childHeight = child.DesiredSize.Height;
            if (x > 0 && x + childWidth > width + 0.5)
            {
                y += rowHeight + gap;
                x = 0;
                rowHeight = 0;
            }
            if (arrange) child.Arrange(new Rect(x, y, childWidth, childHeight));
            usedWidth = Math.Max(usedWidth, x + childWidth);
            x += childWidth + gap;
            rowHeight = Math.Max(rowHeight, childHeight);
        }
        return new Size(usedWidth, y + rowHeight);
    }
}
