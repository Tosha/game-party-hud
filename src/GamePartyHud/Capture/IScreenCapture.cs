using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Capture;

public interface IScreenCapture
{
    /// <summary>
    /// Capture the given region and return a BGRA byte buffer with stride = width*4
    /// and alpha = 255. The returned length is exactly <c>region.W * region.H * 4</c>.
    /// Coordinates are interpreted as physical pixels on the virtual desktop.
    /// </summary>
    ValueTask<byte[]> CaptureBgraAsync(HpRegion region, CancellationToken ct = default);
}
