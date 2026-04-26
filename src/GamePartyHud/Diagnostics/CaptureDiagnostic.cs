using System;
using GamePartyHud.Capture;

namespace GamePartyHud.Diagnostics;

/// <summary>
/// HSV averaging utility used by <see cref="GamePartyHud.Party.PartyOrchestrator"/>
/// to summarise the captured HP-bar region in tick log lines. Pure compute over
/// the BGRA pixel buffer the analyzer already produced — no filesystem,
/// no allocations beyond the result.
///
/// Used to also expose <c>RunAsync</c> which wrote
/// <c>capture-{timestamp}.png</c> / <c>.txt</c> files for forensic inspection.
/// That entry point and its callers were removed; nothing in the codebase
/// reads those files, and they accumulated in <c>%AppData%\GamePartyHud\</c>
/// without bound.
/// </summary>
public static class CaptureDiagnostic
{
    public static Hsv AverageHsv(byte[] bgra, int w, int h, int colStart, int colEnd,
                                  int rowStart = 0, int rowEnd = int.MaxValue)
    {
        rowEnd = Math.Min(rowEnd, h);
        double sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int y = rowStart; y < rowEnd; y++)
        {
            for (int x = colStart; x < colEnd; x++)
            {
                int i = (y * w + x) * 4;
                sb += bgra[i];
                sg += bgra[i + 1];
                sr += bgra[i + 2];
                n++;
            }
        }
        if (n == 0) return new Hsv(0, 0, 0);
        return Hsv.FromBgra((byte)(sb / n), (byte)(sg / n), (byte)(sr / n));
    }
}
