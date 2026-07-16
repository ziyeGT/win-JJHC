namespace Huaci.App.Services.Capture;

/// <summary>
/// A physical-screen coordinate in pixels. Coordinates can be negative on a
/// monitor positioned to the left or above the primary monitor.
/// </summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// A snapshot of the Ctrl+Alt+left-button screenshot gesture. The low-level
/// hook retains this state so an overlay that appears after the initial mouse
/// down can continue the same drag without requiring a second click.
/// </summary>
public readonly record struct ScreenshotDragState(
    long GestureId,
    ScreenPoint Start,
    ScreenPoint Current,
    bool IsButtonDown,
    uint NativeTimestamp);

/// <summary>A rectangle in physical-screen coordinates.</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

public enum MouseSelectionTriggerKind
{
    Drag,
    DoubleClick,
}

/// <summary>
/// Raised after a left-button drag or the second mouse-up of a double-click.
/// The event is dispatched on a thread-pool thread so subscribers never block
/// the low-level hook callback.
/// </summary>
public sealed class MouseSelectionTriggerEventArgs : EventArgs
{
    public MouseSelectionTriggerEventArgs(
        MouseSelectionTriggerKind kind,
        ScreenPoint start,
        ScreenPoint end,
        uint nativeTimestamp)
    {
        Kind = kind;
        Start = start;
        End = end;
        NativeTimestamp = nativeTimestamp;
    }

    public MouseSelectionTriggerKind Kind { get; }

    public ScreenPoint Start { get; }

    public ScreenPoint End { get; }

    /// <summary>
    /// Milliseconds since Windows started, as supplied by MSLLHOOKSTRUCT.
    /// The value wraps after approximately 49.7 days.
    /// </summary>
    public uint NativeTimestamp { get; }
}

/// <summary>
/// Raised when the user presses the left mouse button while both Ctrl and Alt
/// are held. The triggering click is consumed by the low-level hook and is not
/// reported as a normal text-selection gesture.
/// </summary>
public sealed class ScreenshotRequestedEventArgs : EventArgs
{
    public ScreenshotRequestedEventArgs(
        ScreenPoint triggerPoint,
        uint nativeTimestamp,
        long gestureId)
    {
        TriggerPoint = triggerPoint;
        NativeTimestamp = nativeTimestamp;
        GestureId = gestureId;
    }

    /// <summary>The physical-screen location at which capture was requested.</summary>
    public ScreenPoint TriggerPoint { get; }

    public uint NativeTimestamp { get; }

    /// <summary>Identifies the continuous drag that triggered this request.</summary>
    public long GestureId { get; }
}

public interface IGlobalMouseHook : IDisposable
{
    event EventHandler<MouseSelectionTriggerEventArgs>? SelectionTriggered;

    event EventHandler<ScreenshotRequestedEventArgs>? ScreenshotRequested;

    bool IsRunning { get; }

    /// <summary>
    /// Enables the Ctrl+Alt+left-click screenshot gesture independently from
    /// ordinary text-selection monitoring. When false, the chord is passed to
    /// the target application without being consumed.
    /// </summary>
    bool ScreenshotGestureEnabled { get; set; }

    void Start();

    void Stop();

    /// <summary>
    /// Prevents ordinary drag/double-click selection events until the returned
    /// lease is disposed. Screenshot capture uses this while its overlay owns
    /// the pointer, so the box-selection drag cannot start a text translation.
    /// Leases may be nested.
    /// </summary>
    IDisposable SuppressSelectionTriggers();

    /// <summary>
    /// Temporarily lets Ctrl+Alt+left-button input pass through without
    /// queuing another screenshot request. The capture overlay holds this
    /// lease so users can keep the modifiers pressed while dragging.
    /// </summary>
    IDisposable SuppressScreenshotRequests();

    /// <summary>
    /// Reads the latest physical coordinates and button state for a screenshot
    /// drag. The final released state remains available until a newer gesture
    /// starts, allowing fast drags to finish before the overlay is visible.
    /// </summary>
    bool TryGetScreenshotDragState(long gestureId, out ScreenshotDragState state);
}

public enum SelectionCaptureSource
{
    UiAutomation,
    Clipboard,
}

public enum SelectionCaptureStatus
{
    Success,
    Disabled,
    NoElement,
    OwnProcess,
    PasswordField,
    TextPatternUnavailable,
    NoSelection,
    EmptyText,
    ClipboardUnchanged,
    ClipboardUnavailable,
    ForegroundChanged,
    Superseded,
    Cancelled,
    Disposed,
    Failed,
}

/// <summary>
/// A non-throwing capture result. <see cref="Diagnostic"/> is intended for
/// local logging and must not be shown as selected text.
/// </summary>
public sealed record SelectionCaptureResult(
    SelectionCaptureSource Source,
    SelectionCaptureStatus Status,
    string? Text,
    IReadOnlyList<ScreenRect> BoundingRectangles,
    int? ProcessId = null,
    string? Diagnostic = null)
{
    private static readonly IReadOnlyList<ScreenRect> NoRectangles = Array.Empty<ScreenRect>();

    public bool IsSuccess => Status == SelectionCaptureStatus.Success && !string.IsNullOrWhiteSpace(Text);

    public ScreenRect? BoundingRectangle
    {
        get
        {
            if (BoundingRectangles.Count == 0)
            {
                return null;
            }

            var left = BoundingRectangles[0].X;
            var top = BoundingRectangles[0].Y;
            var right = BoundingRectangles[0].Right;
            var bottom = BoundingRectangles[0].Bottom;

            for (var index = 1; index < BoundingRectangles.Count; index++)
            {
                var rectangle = BoundingRectangles[index];
                left = Math.Min(left, rectangle.X);
                top = Math.Min(top, rectangle.Y);
                right = Math.Max(right, rectangle.Right);
                bottom = Math.Max(bottom, rectangle.Bottom);
            }

            return new ScreenRect(left, top, right - left, bottom - top);
        }
    }

    public static SelectionCaptureResult WithoutText(
        SelectionCaptureSource source,
        SelectionCaptureStatus status,
        int? processId = null,
        string? diagnostic = null) =>
        new(source, status, null, NoRectangles, processId, diagnostic);
}

public interface ITextSelectionService : IDisposable
{
    /// <summary>
    /// Reads the UI Automation selection at <paramref name="point"/>. All UIA
    /// calls run on the service's dedicated MTA thread.
    /// </summary>
    Task<SelectionCaptureResult> CaptureAsync(
        ScreenPoint point,
        CancellationToken cancellationToken = default);
}

public interface IClipboardFallbackService : IDisposable
{
    bool Enabled { get; set; }

    /// <summary>
    /// Sends Ctrl+C to the current foreground application and restores the
    /// previous clipboard only if its sequence number is still unchanged.
    /// </summary>
    Task<SelectionCaptureResult> CaptureAsync(CancellationToken cancellationToken = default);
}
