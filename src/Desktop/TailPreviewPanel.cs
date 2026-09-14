using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RealtimeTranscription.Core;
using Windows.Foundation;

namespace RealtimeTranscription.Desktop;

/// <summary>Fits the newest text using its real, rooted WinUI TextBlock and the parent's layout width.</summary>
internal sealed class TailPreviewPanel : Panel
{
    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);
    private readonly TextBlock label;
    private readonly RectangleGeometry clip = new();
    private string source = "";
    private long revision, fittedRevision = -1;
    private double fittedWidth = double.NaN, fittedScale = double.NaN;
    private XamlRoot? fittedRoot;

    public TailPreviewPanel(TextBlock text)
    {
        label = text;
        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.None;
        Children.Add(label);
        Clip = clip;
    }

    public string Text
    {
        get => source;
        set
        {
            string next = UiPresentation.LatestText(value ?? "", 80);
            if (source == next) return;
            source = next;
            InvalidateTextMetrics();
        }
    }

    public void InvalidateTextMetrics()
    {
        revision++;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsNaN(availableSize.Width) ? 0 : Math.Max(0, availableSize.Width);
        var xamlRoot = XamlRoot;
        double scale = xamlRoot?.RasterizationScale ?? 1;
        if (revision != fittedRevision || width != fittedWidth || scale != fittedScale || !ReferenceEquals(xamlRoot, fittedRoot))
        {
            label.InvalidateMeasure();
            Size natural = MeasureText(source);
            // A hidden or initial zero-width pass must not consume the source.
            if (width > 0 && double.IsFinite(width) && natural.Width > width)
            {
                var starts = StringInfo.ParseCombiningCharacters(source);
                bool found = false;
                for (int skip = 1; skip < starts.Length; skip++)
                {
                    if (MeasureText("…" + source[starts[skip]..]).Width <= width)
                    {
                        found = true;
                        break;
                    }
                }
                // Preserve a complete real grapheme even if its ellipsis prefix
                // cannot fit. Only an exceptionally narrow viewport clips it.
                if (!found && starts.Length > 0) MeasureText(source[starts[^1]..]);
            }
            fittedRevision = revision;
            fittedWidth = width;
            fittedScale = scale;
            fittedRoot = xamlRoot;
        }
        else label.Measure(Unbounded);
        Size desired = label.DesiredSize;
        return new Size(double.IsFinite(width) ? Math.Min(width, desired.Width) : desired.Width,
            double.IsFinite(availableSize.Height) ? Math.Min(Math.Max(0, availableSize.Height), desired.Height) : desired.Height);
    }

    private Size MeasureText(string text)
    {
        if (label.Text != text)
        {
            label.Text = text;
            label.InvalidateMeasure();
        }
        label.Measure(Unbounded);
        return label.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        clip.Rect = new Rect(0, 0, Math.Max(0, finalSize.Width), Math.Max(0, finalSize.Height));
        Size desired = label.DesiredSize;
        label.Arrange(new Rect(Math.Max(0, (finalSize.Width - desired.Width) / 2),
            Math.Max(0, (finalSize.Height - desired.Height) / 2), desired.Width, desired.Height));
        return finalSize;
    }
}
