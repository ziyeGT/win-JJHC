using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Huaci.App.Views;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace Huaci.App.Services.Capture;

/// <summary>
/// Captures a frozen image of each physical display, coordinates the WPF
/// selection overlays and returns the selected pixels as an in-memory PNG.
/// </summary>
public sealed class ScreenRegionCaptureService : IScreenRegionCaptureService
{
    private readonly object _stateGate = new();
    private readonly Dispatcher _dispatcher;
    private readonly IGlobalMouseHook? _mouseHook;
    private readonly ScreenRegionCaptureOptions _options;
    private readonly Action? _captureSurfaceReady;

    private CaptureSession? _currentSession;
    private bool _isDisposed;

    public ScreenRegionCaptureService(
        IGlobalMouseHook? mouseHook = null,
        ScreenRegionCaptureOptions? options = null,
        Dispatcher? dispatcher = null,
        Action? captureSurfaceReady = null)
    {
        _mouseHook = mouseHook;
        _options = options ?? new ScreenRegionCaptureOptions();
        _dispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _captureSurfaceReady = captureSurfaceReady;

        if (_options.MinimumWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MinimumWidth must be positive.");
        }

        if (_options.MinimumHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MinimumHeight must be positive.");
        }

        if (_options.DimOpacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "DimOpacity must be between 0 and 1.");
        }

        if (_options.SelectionConfirmationDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "SelectionConfirmationDelay cannot be negative.");
        }
    }

    public bool IsCapturing
    {
        get
        {
            lock (_stateGate)
            {
                return _currentSession is not null;
            }
        }
    }

    public Task<ScreenRegionCaptureResult> CaptureAsync(
        ScreenPoint? preferredScreenPoint = null,
        long? directDragGestureId = null,
        CancellationToken cancellationToken = default)
    {
        CaptureSession session;

        lock (_stateGate)
        {
            if (_isDisposed)
            {
                return Task.FromResult(ScreenRegionCaptureResult.WithoutImage(
                    ScreenRegionCaptureStatus.Disposed));
            }

            if (_currentSession is not null)
            {
                return Task.FromResult(ScreenRegionCaptureResult.WithoutImage(
                    ScreenRegionCaptureStatus.Busy));
            }

            session = new CaptureSession(preferredScreenPoint, directDragGestureId);
            _currentSession = session;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            CompleteWithoutImage(session, ScreenRegionCaptureStatus.Cancelled);
            return session.Completion.Task;
        }

        session.CancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var request = (CancellationRequest)state!;
                request.Owner.RequestCancellation(request.Session);
            },
            new CancellationRequest(this, session));

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            CompleteWithoutImage(
                session,
                ScreenRegionCaptureStatus.Failed,
                "The UI dispatcher is shutting down.");
            return session.Completion.Task;
        }

        _ = _dispatcher.InvokeAsync(
            () => StartCapture(session),
            DispatcherPriority.Send);

        return session.Completion.Task;
    }

    public void Cancel()
    {
        CaptureSession? session;
        lock (_stateGate)
        {
            session = _currentSession;
        }

        if (session is not null)
        {
            RequestCancellation(session);
        }
    }

    public void Dispose()
    {
        CaptureSession? session;
        lock (_stateGate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            session = _currentSession;
        }

        if (session is not null)
        {
            DispatchToUi(() => CompleteWithoutImage(session, ScreenRegionCaptureStatus.Disposed));
        }

        GC.SuppressFinalize(this);
    }

    private void StartCapture(CaptureSession session)
    {
        if (!IsCurrent(session))
        {
            return;
        }

        try
        {
            session.SelectionSuppression = _mouseHook?.SuppressSelectionTriggers();
            session.Snapshots.AddRange(CaptureDisplays());

            if (session.Snapshots.Count == 0)
            {
                CompleteWithoutImage(
                    session,
                    ScreenRegionCaptureStatus.Failed,
                    "Windows reported no attached displays.");
                return;
            }

            foreach (var snapshot in session.Snapshots)
            {
                var window = new ScreenshotOverlayWindow(
                    snapshot.Bounds,
                    snapshot.Preview,
                    _options.DimOpacity,
                    point => BeginSelection(session, point),
                    point => UpdateSelection(session, point),
                    point => CommitSelection(session, point),
                    () => CompleteWithoutImage(session, ScreenRegionCaptureStatus.Cancelled));

                session.Windows.Add(window);
            }

            // Every screen has already been frozen before the first overlay is
            // shown, preventing one overlay from appearing in another monitor's
            // screenshot.
            foreach (var window in session.Windows)
            {
                window.Show();
            }

            var preferred = session.PreferredPoint is { } point
                ? session.Windows.FirstOrDefault(window => window.Contains(point))
                : null;

            (preferred ?? session.Windows[0]).ActivateForInput();
            try
            {
                _captureSurfaceReady?.Invoke();
            }
            catch
            {
                // The capture surface remains usable even if a companion
                // window cannot restore its z-order.
            }

            if (session.DirectDragGestureId is { } gestureId &&
                session.PreferredPoint is { } selectionStart)
            {
                BeginSelection(session, selectionStart);
                StartDirectDragTracking(session, gestureId);
            }
        }
        catch (Exception exception)
        {
            CompleteWithoutImage(
                session,
                ScreenRegionCaptureStatus.Failed,
                exception.Message);
        }
    }

    private void BeginSelection(CaptureSession session, ScreenPoint point)
    {
        if (!IsCurrent(session) || session.PendingResult is not null)
        {
            return;
        }

        session.SelectionStart = point;
        session.SelectionEnd = point;
        ShowSelection(session, Normalize(point, point));
    }

    private void UpdateSelection(CaptureSession session, ScreenPoint point)
    {
        if (!IsCurrent(session) ||
            session.PendingResult is not null ||
            session.SelectionStart is null)
        {
            return;
        }

        session.SelectionEnd = point;
        ShowSelection(session, Normalize(session.SelectionStart.Value, point));
    }

    private void CommitSelection(CaptureSession session, ScreenPoint point)
    {
        if (!IsCurrent(session) ||
            session.PendingResult is not null ||
            session.SelectionStart is null)
        {
            return;
        }

        session.SelectionEnd = point;
        var selection = Normalize(session.SelectionStart.Value, point);
        if (selection.Width < _options.MinimumWidth || selection.Height < _options.MinimumHeight)
        {
            if (session.DirectDragGestureId is not null)
            {
                // Treat a chord click (or tiny hand jitter) as entry into the
                // existing manual capture mode. Keeping the overlay open is
                // less surprising than closing it and OCR'ing a meaningless
                // handful of pixels near the trigger point.
                session.DirectDragGestureId = null;
                session.SelectionStart = null;
                session.SelectionEnd = null;
                foreach (var window in session.Windows)
                {
                    window.UpdateSelection(null);
                }

                var activeWindow = session.Windows.FirstOrDefault(window => window.Contains(point))
                    ?? session.Windows[0];
                activeWindow.ActivateForInput();
                return;
            }

            CompleteWithoutImage(session, ScreenRegionCaptureStatus.EmptySelection);
            return;
        }

        try
        {
            // Persist the final geometry before producing the result. For a
            // fast direct drag this is often the first non-empty selection the
            // overlay receives, so closing in this same dispatcher turn would
            // prevent WPF from ever presenting the outline.
            ShowSelection(session, selection);

            var pngBytes = CropToPng(session.Snapshots, selection);
            var result = new ScreenRegionCaptureResult(
                ScreenRegionCaptureStatus.Success,
                selection,
                pngBytes,
                checked((int)selection.Width),
                checked((int)selection.Height));

            ConfirmSelectionThenComplete(session, result);
        }
        catch (Exception exception)
        {
            CompleteWithoutImage(
                session,
                ScreenRegionCaptureStatus.Failed,
                exception.Message);
        }
    }

    private void ConfirmSelectionThenComplete(
        CaptureSession session,
        ScreenRegionCaptureResult result)
    {
        session.PendingResult = result;

        if (_options.SelectionConfirmationDelay == TimeSpan.Zero)
        {
            Complete(session, result);
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = _options.SelectionConfirmationDelay,
        };
        timer.Tick += (_, _) =>
        {
            StopSelectionConfirmation(session);
            if (IsCurrent(session) && session.PendingResult is { } pendingResult)
            {
                Complete(session, pendingResult);
            }
        };
        session.SelectionConfirmationTimer = timer;
        timer.Start();
    }

    private void StartDirectDragTracking(CaptureSession session, long gestureId)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        timer.Tick += (_, _) => PollDirectDrag(session, gestureId);
        session.DirectDragTimer = timer;
        timer.Start();

        // A quick drag can be released while application windows are hiding or
        // display snapshots are being captured. Poll immediately so its final
        // coordinates are committed without waiting for another mouse action.
        PollDirectDrag(session, gestureId);
    }

    private void PollDirectDrag(CaptureSession session, long gestureId)
    {
        if (!IsCurrent(session))
        {
            StopDirectDragTracking(session);
            return;
        }

        if (_mouseHook is null ||
            !_mouseHook.TryGetScreenshotDragState(gestureId, out var dragState))
        {
            return;
        }

        UpdateSelection(session, dragState.Current);
        if (dragState.IsButtonDown)
        {
            return;
        }

        StopDirectDragTracking(session);
        CommitSelection(session, dragState.Current);
    }

    private static void StopDirectDragTracking(CaptureSession session)
    {
        var timer = session.DirectDragTimer;
        session.DirectDragTimer = null;
        timer?.Stop();
    }

    private static void StopSelectionConfirmation(CaptureSession session)
    {
        var timer = session.SelectionConfirmationTimer;
        session.SelectionConfirmationTimer = null;
        timer?.Stop();
    }

    private static void ShowSelection(CaptureSession session, ScreenRect selection)
    {
        foreach (var window in session.Windows)
        {
            window.UpdateSelection(selection);
        }
    }

    private void RequestCancellation(CaptureSession session)
    {
        DispatchToUi(() =>
        {
            if (IsCurrent(session))
            {
                CompleteWithoutImage(session, ScreenRegionCaptureStatus.Cancelled);
            }
        });
    }

    private void DispatchToUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _ = _dispatcher.BeginInvoke(action, DispatcherPriority.Send);
        }
    }

    private bool IsCurrent(CaptureSession session)
    {
        lock (_stateGate)
        {
            return ReferenceEquals(_currentSession, session);
        }
    }

    private void CompleteWithoutImage(
        CaptureSession session,
        ScreenRegionCaptureStatus status,
        string? diagnostic = null) =>
        Complete(session, ScreenRegionCaptureResult.WithoutImage(status, diagnostic));

    private void Complete(CaptureSession session, ScreenRegionCaptureResult result)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_currentSession, session))
            {
                return;
            }

            _currentSession = null;
        }

        StopDirectDragTracking(session);
        StopSelectionConfirmation(session);

        foreach (var window in session.Windows)
        {
            try
            {
                window.CloseSilently();
            }
            catch
            {
                // Continue releasing the remaining capture resources.
            }
        }

        foreach (var snapshot in session.Snapshots)
        {
            snapshot.Dispose();
        }

        session.CancellationRegistration.Dispose();
        session.SelectionSuppression?.Dispose();
        session.Completion.TrySetResult(result);
    }

    private static List<FrozenScreenSnapshot> CaptureDisplays()
    {
        var snapshots = new List<FrozenScreenSnapshot>();
        try
        {
            foreach (var screen in FormsScreen.AllScreens)
            {
                snapshots.Add(FrozenScreenSnapshot.Capture(screen.Bounds));
            }

            return snapshots;
        }
        catch
        {
            foreach (var snapshot in snapshots)
            {
                snapshot.Dispose();
            }

            throw;
        }
    }

    private static ScreenRect Normalize(ScreenPoint start, ScreenPoint end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var right = Math.Max(start.X, end.X);
        var bottom = Math.Max(start.Y, end.Y);
        return new ScreenRect(left, top, right - left, bottom - top);
    }

    private static byte[] CropToPng(
        IReadOnlyList<FrozenScreenSnapshot> snapshots,
        ScreenRect selection)
    {
        var outputWidth = checked((int)selection.Width);
        var outputHeight = checked((int)selection.Height);

        using var output = new DrawingBitmap(outputWidth, outputHeight, PixelFormat.Format24bppRgb);
        using (var graphics = DrawingGraphics.FromImage(output))
        {
            graphics.Clear(System.Drawing.Color.Black);
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;

            foreach (var snapshot in snapshots)
            {
                var intersection = Intersect(snapshot.Bounds, selection);
                if (intersection is null)
                {
                    continue;
                }

                var area = intersection.Value;
                var source = new DrawingRectangle(
                    checked((int)(area.X - snapshot.Bounds.X)),
                    checked((int)(area.Y - snapshot.Bounds.Y)),
                    checked((int)area.Width),
                    checked((int)area.Height));
                var destination = new DrawingRectangle(
                    checked((int)(area.X - selection.X)),
                    checked((int)(area.Y - selection.Y)),
                    source.Width,
                    source.Height);

                graphics.DrawImage(snapshot.Bitmap, destination, source, System.Drawing.GraphicsUnit.Pixel);
            }
        }

        using var stream = new MemoryStream();
        output.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static ScreenRect? Intersect(ScreenRect first, ScreenRect second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);

        return right <= left || bottom <= top
            ? null
            : new ScreenRect(left, top, right - left, bottom - top);
    }

    private sealed class CaptureSession
    {
        public CaptureSession(ScreenPoint? preferredPoint, long? directDragGestureId)
        {
            PreferredPoint = preferredPoint;
            DirectDragGestureId = directDragGestureId;
        }

        public ScreenPoint? PreferredPoint { get; }

        public long? DirectDragGestureId { get; set; }

        public TaskCompletionSource<ScreenRegionCaptureResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<FrozenScreenSnapshot> Snapshots { get; } = new();

        public List<ScreenshotOverlayWindow> Windows { get; } = new();

        public IDisposable? SelectionSuppression { get; set; }

        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public ScreenPoint? SelectionStart { get; set; }

        public ScreenPoint? SelectionEnd { get; set; }

        public DispatcherTimer? DirectDragTimer { get; set; }

        public DispatcherTimer? SelectionConfirmationTimer { get; set; }

        public ScreenRegionCaptureResult? PendingResult { get; set; }
    }

    private sealed record CancellationRequest(
        ScreenRegionCaptureService Owner,
        CaptureSession Session);

    private sealed class FrozenScreenSnapshot : IDisposable
    {
        private FrozenScreenSnapshot(
            ScreenRect bounds,
            DrawingBitmap bitmap,
            BitmapSource preview)
        {
            Bounds = bounds;
            Bitmap = bitmap;
            Preview = preview;
        }

        public ScreenRect Bounds { get; }

        public DrawingBitmap Bitmap { get; }

        public BitmapSource Preview { get; }

        public static FrozenScreenSnapshot Capture(DrawingRectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new InvalidOperationException("A display reported invalid pixel bounds.");
            }

            var bitmap = new DrawingBitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            try
            {
                using (var graphics = DrawingGraphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        bounds.X,
                        bounds.Y,
                        0,
                        0,
                        bounds.Size,
                        // Graphics.CopyFromScreen validates CopyPixelOperation as a
                        // single enum value on .NET 10. Combining CaptureBlt with
                        // SourceCopy throws InvalidEnumArgumentException before any
                        // pixels are captured, so use the compatible SRCCOPY mode.
                        System.Drawing.CopyPixelOperation.SourceCopy);
                }

                var preview = CreatePreview(bitmap);
                return new FrozenScreenSnapshot(
                    new ScreenRect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                    bitmap,
                    preview);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Bitmap.Dispose();
        }

        private static BitmapSource CreatePreview(DrawingBitmap bitmap)
        {
            var handle = bitmap.GetHbitmap();
            try
            {
                var preview = Imaging.CreateBitmapSourceFromHBitmap(
                    handle,
                    nint.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                preview.Freeze();
                return preview;
            }
            finally
            {
                _ = DeleteObject(handle);
            }
        }
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint objectHandle);
}
