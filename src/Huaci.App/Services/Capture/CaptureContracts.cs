namespace Huaci.App.Services.Capture;

/// <summary>
/// A physical-screen coordinate in pixels. Coordinates can be negative on a
/// monitor positioned to the left or above the primary monitor.
/// </summary>
public readonly record struct ScreenPoint(int X, int Y);

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

public interface IGlobalMouseHook : IDisposable
{
    event EventHandler<MouseSelectionTriggerEventArgs>? SelectionTriggered;

    bool IsRunning { get; }

    void Start();

    void Stop();
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
