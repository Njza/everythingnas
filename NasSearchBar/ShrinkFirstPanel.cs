using System.Windows;
using System.Windows.Controls;

namespace NasSearchBar;

// A row of children where, when space runs out, only the first one shrinks (and trims its text);
// the rest keep their full width. Used so a long path trims its outer folders, never the one holding the file.
public class ShrinkFirstPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        double rest = 0, height = 0;
        foreach (UIElement c in InternalChildren)
        {
            if (c == InternalChildren[0]) continue;
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            rest += c.DesiredSize.Width;
            height = Math.Max(height, c.DesiredSize.Height);
        }
        if (InternalChildren.Count == 0) return new Size(0, 0);
        var first = InternalChildren[0];
        first.Measure(new Size(Math.Max(0, available.Width - rest), available.Height));
        return new Size(first.DesiredSize.Width + rest, Math.Max(height, first.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        double rest = 0;
        for (var i = 1; i < InternalChildren.Count; i++) rest += InternalChildren[i].DesiredSize.Width;
        double x = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            var w = i == 0 ? Math.Min(c.DesiredSize.Width, Math.Max(0, final.Width - rest)) : c.DesiredSize.Width;
            c.Arrange(new Rect(x, 0, w, final.Height));
            x += w;
        }
        return final;
    }
}
