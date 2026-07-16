using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Huaci.App.Services.Capture;

/// <summary>
/// Process-wide WH_MOUSE_LL hook hosted by a dedicated Win32 message thread.
/// </summary>
public sealed class GlobalMouseHook : IGlobalMouseHook
{
    private const int WhMouseLl = 14;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const uint LlmhfInjected = 0x00000001;
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private readonly object _lifecycleGate = new();
    private readonly GlobalMouseHookOptions _options;
    private readonly int _dragWidth;
    private readonly int _dragHeight;
    private readonly int _doubleClickHalfWidth;
    private readonly int _doubleClickHalfHeight;
    private readonly ConcurrentQueue<QueuedHookEvent> _triggerQueue = new();

    private LowLevelMouseProc? _hookProcedure;
    private Thread? _messageThread;
    private TaskCompletionSource<bool>? _threadReady;
    private Exception? _startupException;
    private nint _hookHandle;
    private uint _messageThreadId;
    private int _isRunning;
    private int _isDisposed;
    private int _triggerDrainScheduled;
    private int _selectionSuppressionCount;
    private int _screenshotSuppressionCount;
    private int _screenshotRequestLatched;
    private int _screenshotGestureEnabled;
    private long _nextScreenshotGestureId;
    private long _publishedScreenshotGestureId;
    private int _screenshotDragSequence;
    private int _screenshotDragStartX;
    private int _screenshotDragStartY;
    private int _screenshotDragCurrentX;
    private int _screenshotDragCurrentY;
    private int _screenshotDragButtonDown;
    private int _screenshotDragNativeTimestamp;

    // The following fields are touched only by the hook message thread.
    private bool _leftButtonDown;
    private bool _dragDetected;
    private ScreenPoint _buttonDownPoint;
    private bool _hasPreviousClick;
    private ScreenPoint _previousClickPoint;
    private uint _previousClickTime;
    private nint _previousClickWindow;
    private bool _screenshotChordClickActive;

    public GlobalMouseHook(GlobalMouseHookOptions? options = null)
    {
        _options = options ?? new GlobalMouseHookOptions();
        _dragWidth = Math.Max(1, GetSystemMetrics(SmCxDrag) / 2);
        _dragHeight = Math.Max(1, GetSystemMetrics(SmCyDrag) / 2);
        _doubleClickHalfWidth = Math.Max(1, GetSystemMetrics(SmCxDoubleClick) / 2);
        _doubleClickHalfHeight = Math.Max(1, GetSystemMetrics(SmCyDoubleClick) / 2);
        _screenshotGestureEnabled = _options.ScreenshotGestureEnabled ? 1 : 0;
    }

    public event EventHandler<MouseSelectionTriggerEventArgs>? SelectionTriggered;

    public event EventHandler<ScreenshotRequestedEventArgs>? ScreenshotRequested;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public bool ScreenshotGestureEnabled
    {
        get => Volatile.Read(ref _screenshotGestureEnabled) != 0;
        set => Volatile.Write(ref _screenshotGestureEnabled, value ? 1 : 0);
    }

    public IDisposable SuppressSelectionTriggers()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        Interlocked.Increment(ref _selectionSuppressionCount);
        return new SelectionSuppressionLease(this);
    }

    public IDisposable SuppressScreenshotRequests()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        Interlocked.Increment(ref _screenshotSuppressionCount);
        return new ScreenshotSuppressionLease(this);
    }

    public bool TryGetScreenshotDragState(long gestureId, out ScreenshotDragState state)
    {
        state = default;
        if (gestureId <= 0)
        {
            return false;
        }

        // The hook thread is the only writer. A small sequence lock keeps the
        // paired X/Y coordinates coherent for readers on the UI thread.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var sequenceBefore = Volatile.Read(ref _screenshotDragSequence);
            if ((sequenceBefore & 1) != 0)
            {
                Thread.SpinWait(1);
                continue;
            }

            var publishedGestureId = Volatile.Read(ref _publishedScreenshotGestureId);
            var start = new ScreenPoint(
                Volatile.Read(ref _screenshotDragStartX),
                Volatile.Read(ref _screenshotDragStartY));
            var current = new ScreenPoint(
                Volatile.Read(ref _screenshotDragCurrentX),
                Volatile.Read(ref _screenshotDragCurrentY));
            var isButtonDown = Volatile.Read(ref _screenshotDragButtonDown) != 0;
            var nativeTimestamp = unchecked((uint)Volatile.Read(ref _screenshotDragNativeTimestamp));
            var sequenceAfter = Volatile.Read(ref _screenshotDragSequence);

            if (sequenceBefore == sequenceAfter &&
                (sequenceAfter & 1) == 0 &&
                publishedGestureId == gestureId)
            {
                state = new ScreenshotDragState(
                    publishedGestureId,
                    start,
                    current,
                    isButtonDown,
                    nativeTimestamp);
                return true;
            }
        }

        return false;
    }

    public void Start()
    {
        TaskCompletionSource<bool> ready;

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

            if (_messageThread is { IsAlive: true })
            {
                ready = _threadReady
                    ?? throw new InvalidOperationException("The hook thread has no startup signal.");
            }
            else
            {
                ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _threadReady = ready;
                _startupException = null;
                _hookProcedure = HookCallback;

                _messageThread = new Thread(MessageThreadMain)
                {
                    IsBackground = true,
                    Name = "Huaci global mouse hook",
                };
                _messageThread.SetApartmentState(ApartmentState.MTA);
                _messageThread.Start();
            }
        }

        if (!ready.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            Stop();
            throw new TimeoutException("The global mouse hook message thread did not start in time.");
        }

        if (_startupException is not null)
        {
            var exception = _startupException;
            Stop();
            throw new InvalidOperationException("The global mouse hook could not be installed.", exception);
        }

        if (!IsRunning)
        {
            throw new InvalidOperationException("The global mouse hook stopped while it was starting.");
        }
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;
        TaskCompletionSource<bool>? ready;

        lock (_lifecycleGate)
        {
            thread = _messageThread;
            threadId = Volatile.Read(ref _messageThreadId);
            ready = _threadReady;
        }

        if (thread is null)
        {
            return;
        }

        if (thread.IsAlive && threadId == 0 && ready is not null)
        {
            _ = ready.Task.Wait(TimeSpan.FromSeconds(5));
            threadId = Volatile.Read(ref _messageThreadId);
        }

        if (thread.IsAlive && threadId != 0)
        {
            _ = PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, nint.Zero);
        }

        if (thread.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            _ = thread.Join(TimeSpan.FromSeconds(3));
        }

        lock (_lifecycleGate)
        {
            if (!thread.IsAlive && ReferenceEquals(_messageThread, thread))
            {
                _messageThread = null;
                _messageThreadId = 0;
                _hookProcedure = null;
                _threadReady = null;
            }
        }

        while (_triggerQueue.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        Stop();
        GC.SuppressFinalize(this);
    }

    private void MessageThreadMain()
    {
        try
        {
            ResetGestureState();
            _messageThreadId = GetCurrentThreadId();

            // Force creation of this thread's Win32 message queue so Stop can
            // reliably use PostThreadMessage immediately after Start returns.
            _ = PeekMessage(out _, nint.Zero, 0, 0, PmNoRemove);

            var moduleHandle = GetModuleHandle(null);
            _hookHandle = SetWindowsHookEx(WhMouseLl, _hookProcedure!, moduleHandle, 0);
            if (_hookHandle == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            Volatile.Write(ref _isRunning, 1);
            _threadReady?.TrySetResult(true);

            while (true)
            {
                var result = GetMessage(out var message, nint.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result == -1)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            _startupException ??= exception;
            _threadReady?.TrySetResult(true);
        }
        finally
        {
            var hook = Interlocked.Exchange(ref _hookHandle, nint.Zero);
            if (hook != nint.Zero)
            {
                _ = UnhookWindowsHookEx(hook);
            }

            Volatile.Write(ref _isRunning, 0);
            Volatile.Write(ref _messageThreadId, 0);
            _threadReady?.TrySetResult(true);
        }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                if (!_options.IgnoreInjectedInput || (data.Flags & LlmhfInjected) == 0)
                {
                    if (ProcessMouseMessage(unchecked((uint)wParam.ToInt64()), data))
                    {
                        return 1;
                    }
                }
            }
        }
        catch
        {
            // Exceptions must never escape a reverse P/Invoke hook callback.
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private bool ProcessMouseMessage(uint message, MsllHookStruct data)
    {
        var point = new ScreenPoint(data.Point.X, data.Point.Y);

        switch (message)
        {
            case WmLButtonDown:
                if (ScreenshotGestureEnabled &&
                    ScreenshotRequested is not null &&
                    Volatile.Read(ref _screenshotSuppressionCount) == 0 &&
                    IsKeyDown(VkControl) &&
                    IsKeyDown(VkMenu))
                {
                    _screenshotChordClickActive = true;
                    _leftButtonDown = false;
                    _dragDetected = false;
                    _hasPreviousClick = false;
                    if (Interlocked.CompareExchange(ref _screenshotRequestLatched, 1, 0) == 0)
                    {
                        var gestureId = BeginScreenshotDrag(point, data.Time);
                        QueueScreenshotRequest(new ScreenshotRequestedEventArgs(
                            point,
                            data.Time,
                            gestureId));
                    }

                    return true;
                }

                _leftButtonDown = true;
                _dragDetected = false;
                _buttonDownPoint = point;
                break;

            case WmMouseMove when _screenshotChordClickActive:
                UpdateScreenshotDrag(point, isButtonDown: true, data.Time);
                break;

            case WmMouseMove when _leftButtonDown:
                if (Math.Abs(point.X - _buttonDownPoint.X) >= _dragWidth ||
                    Math.Abs(point.Y - _buttonDownPoint.Y) >= _dragHeight)
                {
                    _dragDetected = true;
                }

                break;

            case WmLButtonUp when _screenshotChordClickActive:
                UpdateScreenshotDrag(point, isButtonDown: false, data.Time);
                _screenshotChordClickActive = false;
                return true;

            case WmLButtonUp when _leftButtonDown:
                _leftButtonDown = false;

                if (_dragDetected)
                {
                    _hasPreviousClick = false;
                    QueueTrigger(new MouseSelectionTriggerEventArgs(
                        MouseSelectionTriggerKind.Drag,
                        _buttonDownPoint,
                        point,
                        data.Time));
                    break;
                }

                if (IsSecondClick(point, data.Time))
                {
                    _hasPreviousClick = false;
                    QueueTrigger(new MouseSelectionTriggerEventArgs(
                        MouseSelectionTriggerKind.DoubleClick,
                        _previousClickPoint,
                        point,
                        data.Time));
                }
                else
                {
                    _hasPreviousClick = true;
                    _previousClickPoint = point;
                    _previousClickTime = data.Time;
                    _previousClickWindow = WindowFromPoint(data.Point);
                }

                break;
        }

        return false;
    }

    private bool IsSecondClick(ScreenPoint point, uint timestamp)
    {
        if (!_hasPreviousClick)
        {
            return false;
        }

        var elapsed = unchecked(timestamp - _previousClickTime);
        return elapsed <= GetDoubleClickTime() &&
               WindowFromPoint(new NativePoint(point.X, point.Y)) == _previousClickWindow &&
               Math.Abs(point.X - _previousClickPoint.X) <= _doubleClickHalfWidth &&
               Math.Abs(point.Y - _previousClickPoint.Y) <= _doubleClickHalfHeight;
    }

    private void QueueTrigger(MouseSelectionTriggerEventArgs eventArgs)
    {
        if (SelectionTriggered is null || Volatile.Read(ref _selectionSuppressionCount) != 0)
        {
            return;
        }

        _triggerQueue.Enqueue(QueuedHookEvent.ForSelection(eventArgs));
        ScheduleTriggerDrain();
    }

    private void QueueScreenshotRequest(ScreenshotRequestedEventArgs eventArgs)
    {
        if (ScreenshotRequested is null)
        {
            return;
        }

        _triggerQueue.Enqueue(QueuedHookEvent.ForScreenshot(eventArgs));
        ScheduleTriggerDrain();
    }

    private void ScheduleTriggerDrain()
    {
        if (Interlocked.CompareExchange(ref _triggerDrainScheduled, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(
            static state => ((GlobalMouseHook)state!).DrainTriggers(),
            this,
            preferLocal: false);
    }

    private void DrainTriggers()
    {
        try
        {
            while (_triggerQueue.TryDequeue(out var queuedEvent))
            {
                if (queuedEvent.Selection is { } selectionEvent)
                {
                    DispatchSafely(SelectionTriggered, selectionEvent);
                }

                if (queuedEvent.Screenshot is { } screenshotEvent)
                {
                    DispatchSafely(ScreenshotRequested, screenshotEvent);
                    if (Volatile.Read(ref _screenshotSuppressionCount) == 0)
                    {
                        Volatile.Write(ref _screenshotRequestLatched, 0);
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _triggerDrainScheduled, 0);
            if (!_triggerQueue.IsEmpty)
            {
                ScheduleTriggerDrain();
            }
        }
    }

    private void DispatchSafely<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs eventArgs)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> subscriber in handlers.GetInvocationList())
        {
            try
            {
                subscriber(this, eventArgs);
            }
            catch
            {
                // A client callback must never terminate capture or block
                // other subscribers from receiving the trigger.
            }
        }
    }

    private void ResetGestureState()
    {
        _leftButtonDown = false;
        _dragDetected = false;
        _hasPreviousClick = false;
        _buttonDownPoint = default;
        _previousClickPoint = default;
        _previousClickTime = 0;
        _previousClickWindow = nint.Zero;
        _screenshotChordClickActive = false;
        ClearScreenshotDrag();
        Volatile.Write(ref _screenshotRequestLatched, 0);
    }

    private long BeginScreenshotDrag(ScreenPoint point, uint nativeTimestamp)
    {
        var gestureId = Interlocked.Increment(ref _nextScreenshotGestureId);
        Interlocked.Increment(ref _screenshotDragSequence);
        Volatile.Write(ref _screenshotDragStartX, point.X);
        Volatile.Write(ref _screenshotDragStartY, point.Y);
        Volatile.Write(ref _screenshotDragCurrentX, point.X);
        Volatile.Write(ref _screenshotDragCurrentY, point.Y);
        Volatile.Write(ref _screenshotDragButtonDown, 1);
        Volatile.Write(ref _screenshotDragNativeTimestamp, unchecked((int)nativeTimestamp));
        Volatile.Write(ref _publishedScreenshotGestureId, gestureId);
        Interlocked.Increment(ref _screenshotDragSequence);
        return gestureId;
    }

    private void UpdateScreenshotDrag(
        ScreenPoint point,
        bool isButtonDown,
        uint nativeTimestamp)
    {
        if (Volatile.Read(ref _publishedScreenshotGestureId) <= 0)
        {
            return;
        }

        Interlocked.Increment(ref _screenshotDragSequence);
        Volatile.Write(ref _screenshotDragCurrentX, point.X);
        Volatile.Write(ref _screenshotDragCurrentY, point.Y);
        Volatile.Write(ref _screenshotDragButtonDown, isButtonDown ? 1 : 0);
        Volatile.Write(ref _screenshotDragNativeTimestamp, unchecked((int)nativeTimestamp));
        Interlocked.Increment(ref _screenshotDragSequence);
    }

    private void ClearScreenshotDrag()
    {
        Interlocked.Increment(ref _screenshotDragSequence);
        Volatile.Write(ref _publishedScreenshotGestureId, 0);
        Volatile.Write(ref _screenshotDragButtonDown, 0);
        Interlocked.Increment(ref _screenshotDragSequence);
    }

    private void ReleaseSelectionSuppression()
    {
        var remaining = Interlocked.Decrement(ref _selectionSuppressionCount);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _selectionSuppressionCount, 0);
        }
    }

    private void ReleaseScreenshotSuppression()
    {
        var remaining = Interlocked.Decrement(ref _screenshotSuppressionCount);
        if (remaining <= 0)
        {
            if (remaining < 0)
            {
                Interlocked.Exchange(ref _screenshotSuppressionCount, 0);
            }

            Volatile.Write(ref _screenshotRequestLatched, 0);
        }
    }

    private static bool IsKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private sealed class SelectionSuppressionLease : IDisposable
    {
        private GlobalMouseHook? _owner;

        public SelectionSuppressionLease(GlobalMouseHook owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseSelectionSuppression();
        }
    }

    private sealed class ScreenshotSuppressionLease : IDisposable
    {
        private GlobalMouseHook? _owner;

        public ScreenshotSuppressionLease(GlobalMouseHook owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseScreenshotSuppression();
        }
    }

    private sealed record QueuedHookEvent(
        MouseSelectionTriggerEventArgs? Selection,
        ScreenshotRequestedEventArgs? Screenshot)
    {
        public static QueuedHookEvent ForSelection(MouseSelectionTriggerEventArgs eventArgs) =>
            new(eventArgs, null);

        public static QueuedHookEvent ForScreenshot(ScreenshotRequestedEventArgs eventArgs) =>
            new(null, eventArgs);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MsllHookStruct
    {
        public readonly NativePoint Point;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public UIntPtr WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc hookProcedure,
        nint moduleHandle,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hookHandle);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hookHandle, int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, nint window, uint minMessage, uint maxMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        nint window,
        uint minMessage,
        uint maxMessage,
        uint removeMessage);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
