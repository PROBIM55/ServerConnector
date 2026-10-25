namespace Connector.Desktop.Services;

/// <summary>
/// Fits a window that is about to open into the work area of its monitor (all values in DIP). The size never exceeds
/// the work area unless the minimum size does; a centred window never starts above or left of the work area, so the
/// page title bar (the only way to move the window) stays on screen even when the minimum size is larger.
/// </summary>
public static class WindowWorkAreaFit
{
    public readonly record struct Placement(double Width, double Height, double? Left, double? Top);

    public static Placement Fit(double width, double height, double minWidth, double minHeight,
        double workLeft, double workTop, double workWidth, double workHeight, bool centre)
    {
        double fittedWidth = width > workWidth ? Math.Max(minWidth, workWidth) : width;
        double fittedHeight = height > workHeight ? Math.Max(minHeight, workHeight) : height;
        if (!centre || (fittedWidth == width && fittedHeight == height)) return new(fittedWidth, fittedHeight, null, null);
        return new(fittedWidth, fittedHeight,
            Math.Max(workLeft, workLeft + (workWidth - fittedWidth) / 2),
            Math.Max(workTop, workTop + (workHeight - fittedHeight) / 2));
    }
}
