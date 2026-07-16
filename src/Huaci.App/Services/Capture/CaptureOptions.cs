namespace Huaci.App.Services.Capture;

public sealed class GlobalMouseHookOptions
{
    /// <summary>
    /// If true, mouse messages tagged as injected by Windows are ignored.
    /// </summary>
    public bool IgnoreInjectedInput { get; init; } = true;

    public bool ScreenshotGestureEnabled { get; init; } = true;
}

public sealed class TextSelectionOptions
{
    /// <summary>
    /// Lets the target application finish processing WM_LBUTTONUP before UIA
    /// is queried. Keep this short so the popup still feels immediate.
    /// </summary>
    public TimeSpan SelectionSettleDelay { get; init; } = TimeSpan.FromMilliseconds(35);

    public int MaxTextLength { get; init; } = 32_000;

    public int MaxAncestorDepth { get; init; } = 12;
}

public sealed class ClipboardFallbackOptions
{
    /// <summary>Clipboard fallback is opt-in because Ctrl+C is observable.</summary>
    public bool Enabled { get; init; }

    public TimeSpan CopyTimeout { get; init; } = TimeSpan.FromMilliseconds(450);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(12);

    /// <summary>
    /// Time for which the clipboard sequence must remain stable before it is
    /// read and restored.
    /// </summary>
    public TimeSpan ClipboardSettleDelay { get; init; } = TimeSpan.FromMilliseconds(24);

    public TimeSpan ClipboardAccessTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    public int MaxTextLength { get; init; } = 32_000;
}
