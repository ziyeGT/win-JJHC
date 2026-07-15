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

    private readonly object _lifecycleGate = new();
    private readonly GlobalMouseHookOptions _options;
    private readonly int _dragWidth;
    private readonly int _dragHeight;
    private readonly int _doubleClickHalfWidth;
    private readonly int _doubleClickHalfHeight;
    private readonly ConcurrentQueue<MouseSelectionTriggerEventArgs> _triggerQueue = new();

    private LowLevelMouseProc? _hookProcedure;
    private Thread? _messageThread;
    private TaskCompletionSource<bool>? _threadReady;
    private Exception? _startupException;
    private nint _hookHandle;
    private uint _messageThreadId;
    private int _isRunning;
    private int _isDisposed;
    private int _triggerDrainScheduled;

    // The following fields are touched only by the hook message thread.
    private bool _leftButtonDown;
    private bool _dragDetected;
    private ScreenPoint _buttonDownPoint;
    private bool _hasPreviousClick;
    private ScreenPoint _previousClickPoint;
    private uint _previousClickTime;
    private nint _previousClickWindow;

    public GlobalMouseHook(GlobalMouseHookOptions? options = null)
    {
        _options = options ?? new GlobalMouseHookOptions();
        _dragWidth = Math.Max(1, GetSystemMetrics(SmCxDrag) / 2);
        _dragHeight = Math.Max(1, GetSystemMetrics(SmCyDrag) / 2);
        _doubleClickHalfWidth = Math.Max(1, GetSystemMetrics(SmCxDoubleClick) / 2);
        _doubleClickHalfHeight = Math.Max(1, GetSystemMetrics(SmCyDoubleClick) / 2);
    }

    public event EventHandler<MouseSelectionTriggerEventArgs>? SelectionTriggered;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

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
                    ProcessMouseMessage(unchecked((uint)wParam.ToInt64()), data);
                }
            }
        }
        catch
        {
            // Exceptions must never escape a reverse P/Invoke hook callback.
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private void ProcessMouseMessage(uint message, MsllHookStruct data)
    {
        var point = new ScreenPoint(data.Point.X, data.Point.Y);

        switch (message)
        {
            case WmLButtonDown:
                _leftButtonDown = true;
                _dragDetected = false;
                _buttonDownPoint = point;
                break;

            case WmMouseMove when _leftButtonDown:
                if (Math.Abs(point.X - _buttonDownPoint.X) >= _dragWidth ||
                    Math.Abs(point.Y - _buttonDownPoint.Y) >= _dragHeight)
                {
                    _dragDetected = true;
                }

                break;

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
        if (SelectionTriggered is null)
        {
            return;
        }

        _triggerQueue.Enqueue(eventArgs);
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
            while (_triggerQueue.TryDequeue(out var eventArgs))
            {
                var handlers = SelectionTriggered;
                if (handlers is null)
                {
                    continue;
                }

                foreach (EventHandler<MouseSelectionTriggerEventArgs> subscriber in handlers.GetInvocationList())
                {
                    try
                    {
                        subscriber(this, eventArgs);
                    }
                    catch
                    {
                        // A client callback must never terminate capture or
                        // block other subscribers from receiving the trigger.
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

    private void ResetGestureState()
    {
        _leftButtonDown = false;
        _dragDetected = false;
        _hasPreviousClick = false;
        _buttonDownPoint = default;
        _previousClickPoint = default;
        _previousClickTime = 0;
        _previousClickWindow = nint.Zero;
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
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
