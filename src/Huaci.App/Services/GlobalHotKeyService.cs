using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Huaci.App.Services;

public sealed class GlobalHotKeyService : IDisposable
{
    private const int ToggleMainWindowHotKeyId = 0x4843;
    private const int WmHotKey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private readonly Window _messageWindow;
    private readonly DispatcherTimer _retryTimer;
    private HwndSource? _windowSource;
    private nint _windowHandle;
    private int _dispatchPending;
    private bool _registered;
    private bool _registrationPreviouslyFailed;
    private bool _disposed;

    public GlobalHotKeyService(Window messageWindow)
    {
        _messageWindow = messageWindow ?? throw new ArgumentNullException(nameof(messageWindow));
        _retryTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            _messageWindow.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _retryTimer.Tick += RetryTimer_OnTick;
    }

    public static Key ToggleMainWindowKey => Key.F1;

    public static ModifierKeys ToggleMainWindowModifiers => ModifierKeys.Control;

    public static string ToggleMainWindowDisplayText => "Ctrl+F1";

    public event Action? ToggleMainWindowRequested;

    public event Action? RegistrationRecovered;

    public bool IsRegistered => _registered;

    public int LastRegistrationError { get; private set; }

    public bool TryRegister()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered)
        {
            return true;
        }

        bool registered = TryRegisterCore();
        if (!registered)
        {
            _registrationPreviouslyFailed = true;
            _retryTimer.Start();
        }

        return registered;
    }

    private bool TryRegisterCore()
    {
        _windowHandle = new WindowInteropHelper(_messageWindow).EnsureHandle();
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        if (_windowSource is null)
        {
            _windowHandle = 0;
            LastRegistrationError = 0;
            return false;
        }

        _windowSource.AddHook(WindowMessageHook);
        _registered = RegisterHotKey(
            _windowHandle,
            ToggleMainWindowHotKeyId,
            ModControl | ModNoRepeat,
            checked((uint)KeyInterop.VirtualKeyFromKey(ToggleMainWindowKey)));

        if (!_registered)
        {
            LastRegistrationError = Marshal.GetLastWin32Error();
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
            _windowHandle = 0;
            return false;
        }

        LastRegistrationError = 0;
        _retryTimer.Stop();
        if (_registrationPreviouslyFailed)
        {
            _registrationPreviouslyFailed = false;
            _ = _messageWindow.Dispatcher.BeginInvoke(
                () => RegistrationRecovered?.Invoke(),
                DispatcherPriority.Background);
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _retryTimer.Stop();
        _retryTimer.Tick -= RetryTimer_OnTick;
        if (_registered && _windowHandle != 0)
        {
            _ = UnregisterHotKey(_windowHandle, ToggleMainWindowHotKeyId);
        }

        _registered = false;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _windowHandle = 0;
    }

    private nint WindowMessageHook(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmHotKey && wParam == ToggleMainWindowHotKeyId)
        {
            handled = true;
            if (Interlocked.Exchange(ref _dispatchPending, 1) == 0)
            {
                _ = _messageWindow.Dispatcher.BeginInvoke(
                    () =>
                    {
                        Interlocked.Exchange(ref _dispatchPending, 0);
                        if (!_disposed)
                        {
                            ToggleMainWindowRequested?.Invoke();
                        }
                    },
                    DispatcherPriority.Input);
            }
        }

        return 0;
    }

    private void RetryTimer_OnTick(object? sender, EventArgs e)
    {
        if (_disposed || _registered)
        {
            _retryTimer.Stop();
            return;
        }

        _ = TryRegisterCore();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint windowHandle,
        int identifier,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int identifier);
}
