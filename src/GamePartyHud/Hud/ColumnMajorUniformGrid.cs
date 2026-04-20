using System;
using System.Windows;
using System.Windows.Controls;

namespace GamePartyHud.Hud;

/// <summary>
/// Column-major equivalent of <see cref="System.Windows.Controls.Primitives.UniformGrid"/>:
/// the first <c>Rows</c> items populate column 0 top-to-bottom, the next <c>Rows</c> items
/// populate column 1, and so on. Used by the HUD so that an 11th member spills into a
/// second column instead of interleaving (the default UniformGrid is row-major).
/// </summary>
public sealed class ColumnMajorUniformGrid : Panel
{
    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(nameof(Rows), typeof(int), typeof(ColumnMajorUniformGrid),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(nameof(Columns), typeof(int), typeof(ColumnMajorUniformGrid),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = Math.Max(1, Rows);
        int cols = Math.Max(1, Columns);

        double cellW = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / cols;
        double cellH = double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : availableSize.Height / rows;
        var cellSize = new Size(cellW, cellH);

        double maxW = 0;
        double maxH = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(cellSize);
            if (child.DesiredSize.Width > maxW) maxW = child.DesiredSize.Width;
            if (child.DesiredSize.Height > maxH) maxH = child.DesiredSize.Height;
        }

        return new Size(maxW * cols, maxH * rows);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int rows = Math.Max(1, Rows);
        int cols = Math.Max(1, Columns);
        double cellW = finalSize.Width / cols;
        double cellH = finalSize.Height / rows;

        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            int col = i / rows;
            int row = i % rows;
            if (col < cols)
            {
                child.Arrange(new Rect(col * cellW, row * cellH, cellW, cellH));
            }
            else
            {
                // Children beyond rows*cols aren't visible but must still be arranged
                // to avoid WPF layout exceptions.
                child.Arrange(new Rect(0, 0, 0, 0));
            }
            i++;
        }

        return finalSize;
    }
}
