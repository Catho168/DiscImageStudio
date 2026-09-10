using System.Windows;
using System.Windows.Controls;

namespace DiscImageStudio.Controls;

/// <summary>
/// WrapPanel that centers every row. The stock WrapPanel left-aligns each row, so a partly
/// filled row — the lone fifth "快速创建" tile once the window is narrow — hangs off the left
/// edge of a grid whose other rows are full.
/// </summary>
public class CenteringWrapPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double rowWidth = 0;
        double rowHeight = 0;
        double widestRow = 0;
        double height = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size desired = child.DesiredSize;
            if (rowWidth > 0 && rowWidth + desired.Width > availableSize.Width)
            {
                widestRow = Math.Max(widestRow, rowWidth);
                height += rowHeight;
                rowWidth = 0;
                rowHeight = 0;
            }

            rowWidth += desired.Width;
            rowHeight = Math.Max(rowHeight, desired.Height);
        }

        widestRow = Math.Max(widestRow, rowWidth);
        height += rowHeight;
        return new Size(
            double.IsPositiveInfinity(availableSize.Width) ? widestRow : Math.Min(widestRow, availableSize.Width),
            height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        List<(UIElement Child, Size Size)> row = [];
        double rowWidth = 0;
        double rowHeight = 0;
        double top = 0;

        foreach (UIElement child in InternalChildren)
        {
            Size desired = child.DesiredSize;
            if (rowWidth > 0 && rowWidth + desired.Width > finalSize.Width)
            {
                top = ArrangeRow(row, rowWidth, rowHeight, top, finalSize.Width);
                rowWidth = 0;
                rowHeight = 0;
            }

            row.Add((child, desired));
            rowWidth += desired.Width;
            rowHeight = Math.Max(rowHeight, desired.Height);
        }

        ArrangeRow(row, rowWidth, rowHeight, top, finalSize.Width);
        return finalSize;
    }

    private static double ArrangeRow(
        List<(UIElement Child, Size Size)> row,
        double rowWidth,
        double rowHeight,
        double top,
        double width)
    {
        double left = Math.Max(0, (width - rowWidth) / 2);
        foreach ((UIElement child, Size size) in row)
        {
            child.Arrange(new Rect(left, top, size.Width, size.Height));
            left += size.Width;
        }

        row.Clear();
        return top + rowHeight;
    }
}
