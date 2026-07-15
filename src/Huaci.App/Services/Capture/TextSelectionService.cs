using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Huaci.App.Services.Capture;

/// <summary>
/// Serializes desktop UI Automation work onto a long-lived, windowless MTA
/// thread. UIA objects never cross the worker-thread boundary.
/// </summary>
public sealed class TextSelectionService : ITextSelectionService
{
    private const int MaximumReturnedRectangles = 2_048;

    private readonly object _enqueueGate = new();
    private readonly TextSelectionOptions _options;
    private readonly BlockingCollection<CaptureRequest> _requests = new(
        new ConcurrentQueue<CaptureRequest>(),
        boundedCapacity: 1);
    private readonly Thread _workerThread;
    private int _isDisposed;

    public TextSelectionService(TextSelectionOptions? options = null)
    {
        _options = options ?? new TextSelectionOptions();
        ValidateOptions(_options);

        _workerThread = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "Huaci UI Automation capture",
        };
        _workerThread.SetApartmentState(ApartmentState.MTA);
        _workerThread.Start();
    }

    public Task<SelectionCaptureResult> CaptureAsync(
        ScreenPoint point,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return Task.FromResult(SelectionCaptureResult.WithoutText(
                SelectionCaptureSource.UiAutomation,
                SelectionCaptureStatus.Disposed));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(SelectionCaptureResult.WithoutText(
                SelectionCaptureSource.UiAutomation,
                SelectionCaptureStatus.Cancelled));
        }

        var request = new CaptureRequest(point, cancellationToken);

        lock (_enqueueGate)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _requests.IsAddingCompleted)
            {
                request.Complete(SelectionCaptureResult.WithoutText(
                    SelectionCaptureSource.UiAutomation,
                    SelectionCaptureStatus.Disposed));
                return request.Task;
            }

            // Selection changes are ephemeral. If a request is still queued,
            // replace it with the newest mouse gesture instead of capturing
            // stale text after a slow provider call.
            if (!_requests.TryAdd(request))
            {
                if (_requests.TryTake(out var superseded))
                {
                    superseded.Complete(SelectionCaptureResult.WithoutText(
                        SelectionCaptureSource.UiAutomation,
                        SelectionCaptureStatus.Superseded));
                }

                if (!_requests.TryAdd(request))
                {
                    request.Complete(SelectionCaptureResult.WithoutText(
                        SelectionCaptureSource.UiAutomation,
                        SelectionCaptureStatus.Superseded));
                }
            }
        }

        return request.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        lock (_enqueueGate)
        {
            _requests.CompleteAdding();
        }

        if (_workerThread.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            // A third-party UIA provider can occasionally block inside COM;
            // never hold application shutdown indefinitely in that case.
            _ = _workerThread.Join(TimeSpan.FromSeconds(2));
        }

        GC.SuppressFinalize(this);
    }

    private void WorkerMain()
    {
        try
        {
            foreach (var request in _requests.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref _isDisposed) != 0)
                {
                    request.Complete(SelectionCaptureResult.WithoutText(
                        SelectionCaptureSource.UiAutomation,
                        SelectionCaptureStatus.Disposed));
                    continue;
                }

                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Complete(SelectionCaptureResult.WithoutText(
                        SelectionCaptureSource.UiAutomation,
                        SelectionCaptureStatus.Cancelled));
                    continue;
                }

                try
                {
                    request.Complete(CaptureCore(request.Point, request.CancellationToken));
                }
                catch (Exception exception)
                {
                    request.Complete(Failed(SelectionCaptureStatus.Failed, exception));
                }
            }
        }
        finally
        {
            while (_requests.TryTake(out var pending))
            {
                pending.Complete(SelectionCaptureResult.WithoutText(
                    SelectionCaptureSource.UiAutomation,
                    SelectionCaptureStatus.Disposed));
            }

            _requests.Dispose();
        }
    }

    private SelectionCaptureResult CaptureCore(ScreenPoint point, CancellationToken cancellationToken)
    {
        if (_options.SelectionSettleDelay > TimeSpan.Zero &&
            cancellationToken.WaitHandle.WaitOne(_options.SelectionSettleDelay))
        {
            return SelectionCaptureResult.WithoutText(
                SelectionCaptureSource.UiAutomation,
                SelectionCaptureStatus.Cancelled);
        }

        try
        {
            AutomationElement? hitElement = null;
            try
            {
                hitElement = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            }
            catch (ElementNotAvailableException)
            {
                // The element can disappear between mouse-up and this query;
                // the focused element below is still a useful fallback.
            }

            SelectionCaptureResult? hitResult = null;
            if (hitElement is not null)
            {
                hitResult = CaptureFromElement(hitElement, cancellationToken);
                if (hitResult.IsSuccess || IsSecurityBoundary(hitResult.Status))
                {
                    return hitResult;
                }
            }

            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is not null)
            {
                var focusedResult = CaptureFromElement(focusedElement, cancellationToken);
                if (focusedResult.IsSuccess || IsSecurityBoundary(focusedResult.Status))
                {
                    return focusedResult;
                }

                // NoSelection/EmptyText is more informative than a pattern
                // miss on the element directly under the pointer.
                if (focusedResult.Status is SelectionCaptureStatus.NoSelection or SelectionCaptureStatus.EmptyText)
                {
                    return focusedResult;
                }

                hitResult ??= focusedResult;
            }

            return hitResult ?? SelectionCaptureResult.WithoutText(
                SelectionCaptureSource.UiAutomation,
                SelectionCaptureStatus.NoElement);
        }
        catch (ElementNotAvailableException exception)
        {
            return Failed(SelectionCaptureStatus.NoElement, exception);
        }
        catch (InvalidOperationException exception)
        {
            return Failed(SelectionCaptureStatus.Failed, exception);
        }
        catch (COMException exception)
        {
            return Failed(SelectionCaptureStatus.Failed, exception);
        }
        catch (Exception exception)
        {
            return Failed(SelectionCaptureStatus.Failed, exception);
        }
    }

    private SelectionCaptureResult CaptureFromElement(
        AutomationElement element,
        CancellationToken cancellationToken)
    {
        AutomationElement? current = element;
        int? targetProcessId = null;

        for (var depth = 0; current is not null && depth <= _options.MaxAncestorDepth; depth++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return SelectionCaptureResult.WithoutText(
                    SelectionCaptureSource.UiAutomation,
                    SelectionCaptureStatus.Cancelled,
                    targetProcessId);
            }

            var information = current.Current;
            targetProcessId ??= information.ProcessId;

            if (information.ProcessId == Environment.ProcessId)
            {
                return SelectionCaptureResult.WithoutText(
                    SelectionCaptureSource.UiAutomation,
                    SelectionCaptureStatus.OwnProcess,
                    information.ProcessId);
            }

            if (information.IsPassword)
            {
                return SelectionCaptureResult.WithoutText(
                    SelectionCaptureSource.UiAutomation,
                    SelectionCaptureStatus.PasswordField,
                    information.ProcessId);
            }

            if (current.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) &&
                patternObject is TextPattern textPattern)
            {
                return ReadSelection(textPattern, targetProcessId, cancellationToken);
            }

            current = TreeWalker.RawViewWalker.GetParent(current);
        }

        return SelectionCaptureResult.WithoutText(
            SelectionCaptureSource.UiAutomation,
            SelectionCaptureStatus.TextPatternUnavailable,
            targetProcessId);
    }

    private SelectionCaptureResult ReadSelection(
        TextPattern textPattern,
        int? processId,
        CancellationToken cancellationToken)
    {
        var ranges = textPattern.GetSelection();
        if (ranges is null || ranges.Length == 0)
        {
            return SelectionCaptureResult.WithoutText(
                SelectionCaptureSource.UiAutomation,
                SelectionCaptureStatus.NoSelection,
                processId);
        }

        var text = new StringBuilder(Math.Min(_options.MaxTextLength, 1_024));
        var rectangles = new List<ScreenRect>();

        foreach (var range in ranges)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return SelectionCaptureResult.WithoutText(
                    SelectionCaptureSource.UiAutomation,
                    SelectionCaptureStatus.Cancelled,
                    processId);
            }

            var separatorLength = text.Length == 0 ? 0 : Environment.NewLine.Length;
            var remaining = _options.MaxTextLength - text.Length - separatorLength;
            if (remaining > 0)
            {
                var rangeText = range.GetText(remaining);
                if (!string.IsNullOrEmpty(rangeText))
                {
                    if (separatorLength > 0)
                    {
                        text.AppendLine();
                    }

                    text.Append(rangeText);
                }
            }

            if (rectangles.Count < MaximumReturnedRectangles)
            {
                AppendRectangles(range.GetBoundingRectangles(), rectangles);
            }
        }

        var selectedText = text.ToString();
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            return SelectionCaptureResult.WithoutText(
                SelectionCaptureSource.UiAutomation,
                SelectionCaptureStatus.EmptyText,
                processId);
        }

        return new SelectionCaptureResult(
            SelectionCaptureSource.UiAutomation,
            SelectionCaptureStatus.Success,
            selectedText,
            rectangles.ToArray(),
            processId);
    }

    private static void AppendRectangles(
        System.Windows.Rect[]? values,
        ICollection<ScreenRect> destination)
    {
        if (values is null || values.Length == 0)
        {
            return;
        }

        foreach (var rectangle in values)
        {
            if (destination.Count >= MaximumReturnedRectangles)
            {
                break;
            }

            if (rectangle.IsEmpty ||
                !double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y) ||
                !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height) ||
                rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                continue;
            }

            destination.Add(new ScreenRect(
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height));
        }
    }

    private static bool IsSecurityBoundary(SelectionCaptureStatus status) =>
        status is SelectionCaptureStatus.OwnProcess or SelectionCaptureStatus.PasswordField;

    private static SelectionCaptureResult Failed(SelectionCaptureStatus status, Exception exception) =>
        SelectionCaptureResult.WithoutText(
            SelectionCaptureSource.UiAutomation,
            status,
            diagnostic: $"{exception.GetType().Name}: {exception.Message}");

    private static void ValidateOptions(TextSelectionOptions options)
    {
        if (options.SelectionSettleDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.SelectionSettleDelay));
        }

        if (options.MaxTextLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxTextLength));
        }

        if (options.MaxAncestorDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxAncestorDepth));
        }
    }

    private sealed class CaptureRequest(
        ScreenPoint point,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<SelectionCaptureResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScreenPoint Point { get; } = point;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public Task<SelectionCaptureResult> Task => _completion.Task;

        public void Complete(SelectionCaptureResult result) => _completion.TrySetResult(result);
    }
}
