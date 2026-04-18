using System;
using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class HpBarDetectorTests
{
    /// <summary>Build a synthetic "nickname above, HP bar below" image.</summary>
    private static byte[] NameAndBar(int width, int height,
        int nameEndY, int barStartY, int barEndY,
        (byte b, byte g, byte r) bar,
        (byte b, byte g, byte r) bg,
        (byte b, byte g, byte r) textGlyph)
    {
        var buf = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                (byte B, byte G, byte R) c;
                if (y <= nameEndY)
                {
                    // "Text" row: background, with sparse text glyphs every 8 px.
                    c = (x % 8 < 2) ? textGlyph : bg;
                }
                else if (y >= barStartY && y <= barEndY)
                {
                    c = bar;
                }
                else
                {
                    c = bg;
                }
                buf[i] = c.B; buf[i + 1] = c.G; buf[i + 2] = c.R; buf[i + 3] = 255;
            }
        }
        return buf;
    }

    [Fact]
    public void FindTopBar_StandardLayout_ReturnsBarRows()
    {
        // 60 wide, 40 tall. Name rows 0–15, gap 16–17, bar 18–25, gap 26–39.
        var buf = NameAndBar(60, 40,
            nameEndY: 15, barStartY: 18, barEndY: 25,
            bar: (0, 0, 220),           // red
            bg: (20, 20, 20),           // dark background
            textGlyph: (240, 240, 240)); // white text (unsaturated)

        var result = HpBarDetector.FindTopBar(buf, 60, 40);
        Assert.NotNull(result);
        Assert.Equal(18, result!.Value.YStart);
        Assert.Equal(25, result.Value.YEnd);
    }

    [Fact]
    public void FindTopBar_NoColouredBand_ReturnsNull()
    {
        var buf = NameAndBar(40, 20,
            nameEndY: 10, barStartY: 99, barEndY: 99,  // no bar
            bar: (0, 0, 0),
            bg: (20, 20, 20),
            textGlyph: (240, 240, 240));
        var result = HpBarDetector.FindTopBar(buf, 40, 20);
        Assert.Null(result);
    }

    [Fact]
    public void FindTopBar_PicksFirstBarWhenMultipleBarsPresent()
    {
        // Two horizontal bars. HP bar on top (red), shield bar below (blue).
        var buf = new byte[60 * 30 * 4];
        Fill(buf, 60, y: 0, h: 10, (20, 20, 20));     // top bg
        Fill(buf, 60, y: 10, h: 5, (0, 0, 220));       // red bar
        Fill(buf, 60, y: 15, h: 5, (20, 20, 20));      // gap
        Fill(buf, 60, y: 20, h: 5, (220, 0, 0));       // blue bar
        Fill(buf, 60, y: 25, h: 5, (20, 20, 20));      // bottom bg

        var result = HpBarDetector.FindTopBar(buf, 60, 30);
        Assert.NotNull(result);
        Assert.Equal(10, result!.Value.YStart);
        Assert.Equal(14, result.Value.YEnd);
    }

    [Fact]
    public void FindTopBar_PicksTallestRun_NotTheFirst()
    {
        // Reproduces the in-the-field bug where the game UI had a ~5px
        // red decorative strip between the nickname and the real HP bar.
        // The first-match detector latched onto that strip (5 rows ≥
        // MinBarRows=3) and reported it as the HP bar, so the stored
        // region captured pixels that were NOT the actual bar.
        //
        //   rows  0-10  bg         (text area)
        //   rows 11-15  red, 5 rows  (decoration — should be skipped)
        //   rows 16-19  bg         (gap)
        //   rows 20-39  red, 20 rows (the real HP bar — should be picked)
        var buf = new byte[60 * 40 * 4];
        Fill(buf, 60, y: 0,  h: 11, (20, 20, 20));
        Fill(buf, 60, y: 11, h: 5,  (0, 0, 220));   // decoration
        Fill(buf, 60, y: 16, h: 4,  (20, 20, 20));
        Fill(buf, 60, y: 20, h: 20, (0, 0, 220));   // real HP bar

        var result = HpBarDetector.FindTopBar(buf, 60, 40);
        Assert.NotNull(result);
        Assert.Equal(20, result!.Value.YStart);
        Assert.Equal(39, result.Value.YEnd);
    }

    [Fact]
    public void FindTopBar_ShortSpuriousBand_IsIgnored()
    {
        // 2px coloured line (e.g. separator) followed by a proper 6px bar. Must pick the bar.
        var buf = new byte[40 * 30 * 4];
        Fill(buf, 40, y: 0, h: 5,  (20, 20, 20));
        Fill(buf, 40, y: 5, h: 2,  (0, 0, 220));   // spurious 2px
        Fill(buf, 40, y: 7, h: 5,  (20, 20, 20));
        Fill(buf, 40, y: 12, h: 6, (0, 0, 220));   // real 6px bar
        Fill(buf, 40, y: 18, h: 12, (20, 20, 20));

        var result = HpBarDetector.FindTopBar(buf, 40, 30);
        Assert.NotNull(result);
        Assert.Equal(12, result!.Value.YStart);
        Assert.Equal(17, result.Value.YEnd);
    }

    private static void Fill(byte[] buf, int width, int y, int h, (byte B, byte G, byte R) c)
    {
        for (int yy = y; yy < y + h; yy++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (yy * width + x) * 4;
                buf[i] = c.B; buf[i + 1] = c.G; buf[i + 2] = c.R; buf[i + 3] = 255;
            }
        }
    }
}
