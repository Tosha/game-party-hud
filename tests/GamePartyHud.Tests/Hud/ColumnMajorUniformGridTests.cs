using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using GamePartyHud.Hud;
using Xunit;

namespace GamePartyHud.Tests.Hud;

/// <summary>
/// Pure-logic coverage for <see cref="ColumnMajorUniformGrid"/>'s Measure/Arrange
/// arithmetic. WPF Panel APIs require STA thread affinity — we bounce each test
/// through a short-lived STA thread so the standard xunit (MTA) runner works.
/// </summary>
public class ColumnMajorUniformGridTests
{
    private const double CardW = 200;
    private const double CardH = 24;

    private static T RunOnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
        return result;
    }

    /// <summary>Builds a panel with <paramref name="childCount"/> cards, measures,
    /// arranges, and returns each child's visual offset relative to the panel.</summary>
    private static Vector[] ArrangeAndGetOffsets(int childCount, int rows, int columns)
    {
        return RunOnSta(() =>
        {
            var panel = new ColumnMajorUniformGrid { Rows = rows, Columns = columns };
            var children = new FrameworkElement[childCount];
            for (int i = 0; i < childCount; i++)
            {
                children[i] = new FrameworkElement { Width = CardW, Height = CardH };
                panel.Children.Add(children[i]);
            }
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            panel.Arrange(new Rect(new Point(0, 0), panel.DesiredSize));

            var offsets = new Vector[childCount];
            for (int i = 0; i < childCount; i++)
            {
                offsets[i] = VisualTreeHelper.GetOffset(children[i]);
            }
            return offsets;
        });
    }

    [Fact]
    public void SingleChild_AtOrigin()
    {
        var offsets = ArrangeAndGetOffsets(childCount: 1, rows: 10, columns: 1);

        Assert.Equal(0.0, offsets[0].X, precision: 3);
        Assert.Equal(0.0, offsets[0].Y, precision: 3);
    }

    [Fact]
    public void ThreeChildren_StackedOneCardHeightApart_NoOverlap()
    {
        // Regression: a 3-member party used to render as overlapping cards clustered
        // at the top because ArrangeOverride divided finalSize.Height by the declared
        // row count (10) rather than the actual visible row count (3). That gave each
        // card a 7.8-px vertical slot while its own fixed Height=24 caused it to
        // overflow downward into the next slot.
        var offsets = ArrangeAndGetOffsets(childCount: 3, rows: 10, columns: 1);

        Assert.Equal(0.0,        offsets[0].Y, precision: 3);
        Assert.Equal(CardH,      offsets[1].Y, precision: 3);
        Assert.Equal(CardH * 2,  offsets[2].Y, precision: 3);
    }

    [Fact]
    public void TenChildren_StackedOneCardHeightApart()
    {
        var offsets = ArrangeAndGetOffsets(childCount: 10, rows: 10, columns: 1);

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(0.0,      offsets[i].X, precision: 3);
            Assert.Equal(i * CardH, offsets[i].Y, precision: 3);
        }
    }

    [Fact]
    public void ElevenChildren_FirstTenFillColumnZero_EleventhStartsColumnOne()
    {
        var offsets = ArrangeAndGetOffsets(childCount: 11, rows: 10, columns: 2);

        // First 10 items stacked in column 0.
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(0.0,       offsets[i].X, precision: 3);
            Assert.Equal(i * CardH, offsets[i].Y, precision: 3);
        }
        // Item 11 at (column 1, row 0).
        Assert.Equal(CardW, offsets[10].X, precision: 3);
        Assert.Equal(0.0,   offsets[10].Y, precision: 3);
    }

    [Fact]
    public void EmptyPanel_MeasuresToZero()
    {
        var desired = RunOnSta(() =>
        {
            var panel = new ColumnMajorUniformGrid { Rows = 10, Columns = 1 };
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return panel.DesiredSize;
        });

        Assert.Equal(0.0, desired.Width, precision: 3);
        Assert.Equal(0.0, desired.Height, precision: 3);
    }
}
