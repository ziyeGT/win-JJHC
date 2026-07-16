namespace Huaci.App.Services.Capture;

public enum ScreenRegionCaptureStatus
{
    Success,
    Cancelled,
    Busy,
    EmptySelection,
    Disposed,
    Failed,
}

/// <summary>
/// Result of a screen-region capture. Coordinates and dimensions are physical
/// pixels. Successful image data is an in-memory PNG so callers can pass it to
/// OCR without creating a temporary file containing potentially private text.
/// </summary>
public sealed record ScreenRegionCaptureResult(
    ScreenRegionCaptureStatus Status,
    ScreenRect? PhysicalBounds,
    byte[]? PngBytes,
    int PixelWidth,
    int PixelHeight,
    string? Diagnostic = null)
{
    public bool IsSuccess =>
        Status == ScreenRegionCaptureStatus.Success &&
        PhysicalBounds is not null &&
        PngBytes is { Length: > 0 };

    public static ScreenRegionCaptureResult WithoutImage(
        ScreenRegionCaptureStatus status,
        string? diagnostic = null) =>
        new(status, null, null, 0, 0, diagnostic);
}

public interface IScreenRegionCaptureService : IDisposable
{
    bool IsCapturing { get; }

    /// <summary>
    /// Freezes every attached display, opens the box-selection overlay and
    /// returns PNG bytes after the user drags a region. Esc or right-click
    /// returns <see cref="ScreenRegionCaptureStatus.Cancelled"/>.
    /// </summary>
    Task<ScreenRegionCaptureResult> CaptureAsync(
        ScreenPoint? preferredScreenPoint = null,
        long? directDragGestureId = null,
        CancellationToken cancellationToken = default);

    void Cancel();
}

public sealed class ScreenRegionCaptureOptions
{
    public int MinimumWidth { get; init; } = 16;

    public int MinimumHeight { get; init; } = 10;

    public double DimOpacity { get; init; } = 0.34;

    /// <summary>
    /// Keeps the final selection outline on screen long enough to be seen,
    /// including when a direct-drag gesture finishes before the overlay has
    /// rendered its first frame. The pixels are always cropped from the frozen
    /// snapshots captured before the overlay is shown.
    /// </summary>
    public TimeSpan SelectionConfirmationDelay { get; init; } =
        TimeSpan.FromMilliseconds(160);
}
