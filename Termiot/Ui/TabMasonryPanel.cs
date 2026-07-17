using System.Windows;
using System.Windows.Controls;

namespace Termiot.Ui;

// Two-lane masonry for the overflow tab rows. A tall tab (resource bars + title) is about twice the height of a short tab (title only), so a band one tall-tab high has room for two stacked short tabs. This packer flows tabs left-to-right into such bands: a tall tab spans the whole band; short tabs drop into whichever lane (top/bottom) is currently shorter, so a run of short tabs fills both lanes beside a tall one instead of leaving the space under them empty. That's what lets a would-be second row of short tabs slot into the gap under the first row. When there are no tall tabs at all, it degrades to a plain single-row wrap (pairing equal-height tabs would only make it taller).
public sealed class TabMasonryPanel : Panel
{
    private double _chromeWidth;
    // Width of the window chrome overlaid at the top-right; the first band's top lane (and full-height tabs) stop short of it so tabs don't slide under the settings/readout controls.
    public double ChromeWidth
    {
        get => _chromeWidth;
        set
        {
            if (_chromeWidth != value)
            {
                _chromeWidth = value;
                InvalidateMeasure();
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        return new Size(width, Pack(width, arrange: false));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Pack(finalSize.Width, arrange: true);
        return finalSize;
    }

    private double Pack(double width, bool arrange)
    {
        if (width < 1)
        {
            width = 1;
        }
        double shortH = double.MaxValue;
        double tallH = 0;
        int count = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            count++;
            double h = child.DesiredSize.Height;
            if (h < shortH)
            {
                shortH = h;
            }
            if (h > tallH)
            {
                tallH = h;
            }
        }
        if (count == 0)
        {
            return 0;
        }
        // No meaningful height variation → plain single-row wrap (stacking equal tabs would only add height).
        if (tallH - shortH <= 8)
        {
            return PackSingleRow(width, arrange);
        }
        double tallCut = (shortH + tallH) / 2;
        double bandH = System.Math.Max(tallH, 2 * shortH);
        double tx = 0, bx = 0;      // top / bottom lane x-cursors within the current band
        double bandTop = 0;
        double maxY = 0;
        // The chrome only reserves the top-right of the very first band's top lane; the bottom lane and later bands use the full width.
        double topLimit = ChromeWidth > 0 && ChromeWidth < width ? width - ChromeWidth : width;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            double w = System.Math.Min(child.DesiredSize.Width, width);
            double h = child.DesiredSize.Height;
            if (h > tallCut)
            {
                // Tall tab: spans the whole band, aligns both lanes past it. Constrained by the top limit (it occupies the top lane).
                double x = System.Math.Max(tx, bx);
                if (x > 0 && x + w > topLimit)
                {
                    bandTop += bandH;
                    x = 0;
                    topLimit = width;
                }
                if (arrange)
                {
                    child.Arrange(new Rect(x, bandTop, w, h));
                }
                tx = bx = x + w;
                maxY = System.Math.Max(maxY, bandTop + h);
            }
            else
            {
                // Short tab: prefer the shorter lane, but respect each lane's limit (top lane is shortened by the chrome in the first band).
                bool topFits = tx + w <= topLimit;
                bool botFits = bx + w <= width;
                bool top;
                if (topFits && botFits)
                {
                    top = tx <= bx;
                }
                else if (topFits)
                {
                    top = true;
                }
                else if (botFits)
                {
                    top = false;
                }
                else
                {
                    // Neither lane has room — new band (full width).
                    bandTop += bandH;
                    tx = bx = 0;
                    topLimit = width;
                    top = true;
                }
                double cx = top ? tx : bx;
                double cy = bandTop + (top ? 0 : shortH);
                if (arrange)
                {
                    child.Arrange(new Rect(cx, cy, w, h));
                }
                if (top)
                {
                    tx = cx + w;
                }
                else
                {
                    bx = cx + w;
                }
                maxY = System.Math.Max(maxY, cy + h);
            }
        }
        return maxY;
    }

    private double PackSingleRow(double width, bool arrange)
    {
        double x = 0, rowTop = 0, rowH = 0, maxY = 0;
        double limit = ChromeWidth > 0 && ChromeWidth < width ? width - ChromeWidth : width;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            double w = System.Math.Min(child.DesiredSize.Width, width);
            double h = child.DesiredSize.Height;
            if (x > 0 && x + w > limit)
            {
                rowTop += rowH;
                x = 0;
                rowH = 0;
                limit = width;
            }
            if (arrange)
            {
                child.Arrange(new Rect(x, rowTop, w, h));
            }
            x += w;
            if (h > rowH)
            {
                rowH = h;
            }
            if (rowTop + h > maxY)
            {
                maxY = rowTop + h;
            }
        }
        return maxY;
    }
}
