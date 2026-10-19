namespace Launchpad.Core;

public readonly record struct PixelBounds(int X, int Y, int Width, int Height);

public static class LauncherGeometry
{
    public static PixelBounds Center(int x, int y, int width, int height, double scaleX, double scaleY)
    {
        var w = Math.Min((int)Math.Round(1080 * scaleX), (int)(width * .92));
        var h = Math.Min((int)Math.Round(720 * scaleY), (int)(height * .92));
        return new PixelBounds(x + (width - w) / 2, y + (height - h) / 2, w, h);
    }

    public static (int Columns, int Rows) Grid(double width, double height) =>
        (Math.Clamp((int)(width / 132), 1, 7), Math.Clamp((int)(height / 124), 1, 4));
}
