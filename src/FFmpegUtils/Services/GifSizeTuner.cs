using FFmpegUtils.Models;

namespace FFmpegUtils.Services;

public static class GifSizeTuner
{
    private static readonly int[] ColorSteps = [256, 192, 128, 96, 64, 48, 32];

    public static GifSizeParameters Reduce(GifSizeParameters current, long actualBytes, long targetBytes, int minimumWidth = 240)
    {
        if (actualBytes <= 0 || targetBytes <= 0 || actualBytes <= targetBytes)
        {
            return current;
        }

        var ratio = targetBytes / (double)actualBytes;
        var widthFactor = Math.Clamp(Math.Sqrt(ratio) * 0.96, 0.55, 0.92);
        minimumWidth = Math.Max(2, minimumWidth);
        var nextWidth = MakeEven(Math.Max(minimumWidth, (int)Math.Floor(current.Width * widthFactor)));

        var fpsFactor = Math.Clamp(Math.Pow(ratio, 0.2), 0.72, 0.95);
        var nextFps = Math.Max(5, (int)Math.Floor(current.FrameRate * fpsFactor));

        var nextColors = current.Colors;
        if (ratio < 0.72 || (nextWidth == current.Width && nextFps == current.FrameRate))
        {
            nextColors = ColorSteps.FirstOrDefault(value => value < current.Colors);
            if (nextColors == 0)
            {
                nextColors = 32;
            }
        }

        if (nextWidth == current.Width && nextFps == current.FrameRate && nextColors == current.Colors)
        {
            nextColors = Math.Max(32, current.Colors - 16);
        }

        return new GifSizeParameters(nextWidth, nextFps, nextColors);
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
}
