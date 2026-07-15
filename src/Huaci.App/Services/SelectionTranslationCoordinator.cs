using System.Windows;
using System.Windows.Threading;
using Huaci.App.Models;
using Huaci.App.Services.Capture;
using Huaci.App.Services.Translation;
using Huaci.App.Views;

namespace Huaci.App.Services;

/// <summary>
/// Connects global mouse gestures to selection capture and translation while
/// keeping all popup access on the WPF dispatcher.
/// </summary>
public sealed class SelectionTranslationCoordinator : IDisposable
{
    private readonly IGlobalMouseHook _mouseHook;
    private readonly ITextSelectionService _textSelection;
    private readonly IClipboardFallbackService _clipboardFallback;
    private readonly ITranslationService _translationService;
    private readonly TranslationPopupWindow _popup;
    private readonly Dispatcher _dispatcher;
    private readonly Func<AppSettings> _getSettings;
    private readonly object _requestGate = new();

    private CancellationTokenSource? _activeRequest;
    private string? _lastText;
    private DateTimeOffset _lastTextAt;
    private bool _disposed;

    public SelectionTranslationCoordinator(
        IGlobalMouseHook mouseHook,
        ITextSelectionService textSelection,
        IClipboardFallbackService clipboardFallback,
        ITranslationService translationService,
        TranslationPopupWindow popup,
        Dispatcher dispatcher,
        Func<AppSettings> getSettings)
    {
        _mouseHook = mouseHook;
        _textSelection = textSelection;
        _clipboardFallback = clipboardFallback;
        _translationService = translationService;
        _popup = popup;
        _dispatcher = dispatcher;
        _getSettings = getSettings;
        _mouseHook.SelectionTriggered += OnSelectionTriggered;
        _popup.Dismissed += OnPopupDismissed;
    }

    public void CancelPending(bool hidePopup = false)
    {
        CancellationTokenSource? request;
        lock (_requestGate)
        {
            request = _activeRequest;
            _activeRequest = null;
        }

        TryCancel(request);
        if (hidePopup)
        {
            _dispatcher.BeginInvoke(_popup.HidePopup);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mouseHook.SelectionTriggered -= OnSelectionTriggered;
        _popup.Dismissed -= OnPopupDismissed;
        CancelPending();
    }

    private void OnSelectionTriggered(object? sender, MouseSelectionTriggerEventArgs e)
    {
        if (_disposed || !_getSettings().AutoCaptureEnabled)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? superseded;
        lock (_requestGate)
        {
            superseded = _activeRequest;
            _activeRequest = cancellation;
        }

        TryCancel(superseded);

        _ = ProcessSelectionAsync(e, cancellation);
    }

    private async Task ProcessSelectionAsync(MouseSelectionTriggerEventArgs gesture, CancellationTokenSource request)
    {
        var sourceText = string.Empty;
        var popupAnchor = ToScreenRect(null, gesture.End);
        var popupDuration = _getSettings().PopupDurationSeconds;

        try
        {
            var settings = _getSettings();
            popupDuration = settings.PopupDurationSeconds;
            await Task.Delay(settings.CaptureDelayMs, request.Token).ConfigureAwait(false);

            SelectionCaptureResult capture = await _textSelection
                .CaptureAsync(gesture.End, request.Token)
                .ConfigureAwait(false);

            if (!capture.IsSuccess && settings.ClipboardFallbackEnabled && ShouldTryClipboard(capture.Status))
            {
                capture = await _clipboardFallback.CaptureAsync(request.Token).ConfigureAwait(false);
            }

            if (!capture.IsSuccess || !TextHeuristics.TryPrepareForTranslation(capture.Text, out var text))
            {
                return;
            }

            if (IsDuplicate(text))
            {
                return;
            }

            sourceText = text;
            await OnUiAsync(
                () => _popup.ShowLoading(text, popupAnchor, popupDuration),
                request.Token).ConfigureAwait(false);

            var translationRequest = new TranslationRequest(text, settings.SourceLanguage, settings.TargetLanguage);
            var result = await _translationService
                .TranslateAsync(translationRequest, settings, request.Token)
                .ConfigureAwait(false);

            if (!IsCurrentRequest(request))
            {
                return;
            }

            await OnUiAsync(
                () =>
                {
                    if (IsCurrentRequest(request))
                    {
                        _popup.ShowResult(
                            text,
                            result.TranslatedText,
                            popupAnchor,
                            popupDuration,
                            result.Origin,
                            result.UsedFallback);
                    }
                },
                request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this one.
        }
        catch (TranslationProviderException exception)
        {
            await ShowErrorIfCurrentAsync(
                sourceText,
                exception.Message,
                popupAnchor,
                popupDuration,
                request).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ShowErrorIfCurrentAsync(
                sourceText,
                "翻译失败，请稍后重试。",
                popupAnchor,
                popupDuration,
                request).ConfigureAwait(false);
        }
        finally
        {
            lock (_requestGate)
            {
                if (ReferenceEquals(_activeRequest, request))
                {
                    _activeRequest = null;
                }
            }

            request.Dispose();
        }
    }

    private void OnPopupDismissed() => CancelPending();

    private static void TryCancel(CancellationTokenSource? request)
    {
        try
        {
            request?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion may race a pointer-leave or a newer selection.
        }
    }

    private bool IsCurrentRequest(CancellationTokenSource request)
    {
        lock (_requestGate)
        {
            return !_disposed && ReferenceEquals(_activeRequest, request) && !request.IsCancellationRequested;
        }
    }

    private bool IsDuplicate(string text)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_requestGate)
        {
            if (string.Equals(_lastText, text, StringComparison.Ordinal) && now - _lastTextAt < TimeSpan.FromSeconds(2))
            {
                return true;
            }

            _lastText = text;
            _lastTextAt = now;
            return false;
        }
    }

    private static bool ShouldTryClipboard(SelectionCaptureStatus status) => status is
        SelectionCaptureStatus.NoElement
        or SelectionCaptureStatus.TextPatternUnavailable
        or SelectionCaptureStatus.NoSelection
        or SelectionCaptureStatus.EmptyText;

    private async Task ShowErrorAsync(
        string source,
        string message,
        Rect anchor,
        int duration,
        CancellationToken cancellationToken) =>
        await OnUiAsync(
            () => _popup.ShowError(source, message, anchor, duration),
            cancellationToken).ConfigureAwait(false);

    private async Task ShowErrorIfCurrentAsync(
        string source,
        string message,
        Rect anchor,
        int duration,
        CancellationTokenSource request)
    {
        if (!IsCurrentRequest(request))
        {
            return;
        }

        try
        {
            await OnUiAsync(
                () =>
                {
                    if (IsCurrentRequest(request))
                    {
                        _popup.ShowError(source, message, anchor, duration);
                    }
                },
                request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The toast was dismissed or a newer selection superseded this error.
        }
        catch (Exception)
        {
            // The application may be shutting down.
        }
    }

    private Task OnUiAsync(Action action, CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;

    private static Rect ToScreenRect(ScreenRect? rectangle, ScreenPoint fallback)
    {
        if (rectangle is { Width: >= 0, Height: >= 0 } value)
        {
            return new Rect(value.X, value.Y, value.Width, value.Height);
        }

        return new Rect(fallback.X, fallback.Y, 1, 1);
    }
}
