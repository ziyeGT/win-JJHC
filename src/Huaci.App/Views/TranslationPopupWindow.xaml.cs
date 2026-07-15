using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Huaci.App.Models;

namespace Huaci.App.Views;

public partial class TranslationPopupWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExToolWindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private const double InitialCursorProximity = 24;
    private static readonly TimeSpan PresencePollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly System.Windows.Media.Brush ResultBrush = System.Windows.Media.Brushes.White;
    private static readonly System.Windows.Media.Brush ErrorBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 178, 178));

    private readonly DispatcherTimer _presenceTimer = new();
    private readonly ToastDismissPolicy _dismissPolicy = new();
    private int _lastDuration = 5;
    private bool _updatingPinState;
    private bool _allowClose;

    public TranslationPopupWindow()
    {
        InitializeComponent();
        _presenceTimer.Interval = PresencePollInterval;
        _presenceTimer.Tick += PresenceTimer_OnTick;
        MouseEnter += (_, _) => _dismissPolicy.PointerEntered();
        MouseLeave += (_, _) =>
        {
            if (_dismissPolicy.PointerLeft(DateTimeOffset.UtcNow))
            {
                DismissFromUser();
            }
        };
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// Raised when the user dismisses an unpinned toast so in-flight work can be cancelled.
    /// </summary>
    public event Action? Dismissed;

    public string CurrentSource { get; private set; } = string.Empty;
    public string CurrentResult { get; private set; } = string.Empty;
    public TranslationOrigin? CurrentOrigin { get; private set; }
    public bool IsPinned => _dismissPolicy.IsPinned;

    public void ShowLoading(string source, Rect anchor, int autoHideSeconds)
    {
        CurrentSource = source;
        CurrentResult = string.Empty;
        CurrentOrigin = null;
        OriginBadge.Visibility = Visibility.Collapsed;
        OriginBadge.ToolTip = null;
        SourceTextBlock.Text = source;
        ResultTextBlock.Text = string.Empty;
        ResultTextBlock.Foreground = ResultBrush;
        ResultScrollViewer.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        CopyButton.IsEnabled = false;
        ShowAt(anchor, autoHideSeconds);
    }

    public void ShowResult(
        string source,
        string result,
        Rect anchor,
        int autoHideSeconds,
        TranslationOrigin origin = TranslationOrigin.Online,
        bool usedFallback = false)
    {
        CurrentSource = source;
        CurrentResult = result;
        CurrentOrigin = origin;
        OriginTextBlock.Text = origin == TranslationOrigin.Offline ? "离线" : "在线";
        OriginBadge.ToolTip = usedFallback
            ? "离线不可用，本次使用在线服务"
            : origin == TranslationOrigin.Offline
                ? "译文由内置模型在本机生成"
                : "译文由在线服务生成";
        OriginBadge.Visibility = Visibility.Visible;
        SourceTextBlock.Text = source;
        ResultTextBlock.Text = result;
        ResultTextBlock.Foreground = ResultBrush;
        ResultScrollViewer.Visibility = Visibility.Visible;
        ResultScrollViewer.ScrollToHome();
        LoadingPanel.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = result.Length > 0;
        ShowAt(anchor, autoHideSeconds);
    }

    public void ShowError(string source, string error, Rect anchor, int autoHideSeconds)
    {
        CurrentSource = source;
        CurrentResult = string.Empty;
        CurrentOrigin = null;
        OriginBadge.Visibility = Visibility.Collapsed;
        OriginBadge.ToolTip = null;
        SourceTextBlock.Text = source;
        ResultTextBlock.Text = error;
        ResultTextBlock.Foreground = ErrorBrush;
        ResultScrollViewer.Visibility = Visibility.Visible;
        ResultScrollViewer.ScrollToHome();
        LoadingPanel.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
        ShowAt(anchor, autoHideSeconds);
    }

    /// <summary>
    /// Force-hides the popup for pause/exit flows and clears any pinned state.
    /// </summary>
    public void HidePopup()
    {
        _presenceTimer.Stop();
        ResetPinState();
        Hide();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _presenceTimer.Stop();
        ResetPinState();
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            DismissFromUser();
            return;
        }

        base.OnClosing(e);
    }

    private void ShowAt(Rect anchor, int autoHideSeconds)
    {
        _lastDuration = Math.Clamp(autoHideSeconds, 2, 60);
        var shouldPosition = !IsVisible || !_dismissPolicy.IsPinned;

        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        if (shouldPosition)
        {
            PositionWindow(anchor);
        }

        if (_dismissPolicy.IsPinned)
        {
            _presenceTimer.Stop();
            return;
        }

        ResetPresenceTracking();
    }

    private void ResetPresenceTracking()
    {
        var cursorStartsNear = IsMouseOver
            || (TryGetCursorDistanceFromWindow(out var distance) && distance <= InitialCursorProximity);
        _dismissPolicy.BeginPresentation(DateTimeOffset.UtcNow, cursorStartsNear);
        _presenceTimer.Stop();
        _presenceTimer.Start();
    }

    private void PresenceTimer_OnTick(object? sender, EventArgs e)
    {
        if (_dismissPolicy.IsPinned || !IsVisible)
        {
            _presenceTimer.Stop();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        double? cursorDistance = TryGetCursorDistanceFromWindow(out var distance) ? distance : null;
        if (_dismissPolicy.ShouldDismiss(
                now,
                IsMouseOver,
                cursorDistance,
                TimeSpan.FromSeconds(_lastDuration)))
        {
            DismissFromUser();
        }
    }

    private bool TryGetCursorDistanceFromWindow(out double distance)
    {
        distance = double.PositiveInfinity;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero || !GetCursorPos(out var cursor) || !GetWindowRect(hwnd, out var window))
        {
            return false;
        }

        var dx = cursor.X < window.Left
            ? window.Left - cursor.X
            : cursor.X > window.Right
                ? cursor.X - window.Right
                : 0;
        var dy = cursor.Y < window.Top
            ? window.Top - cursor.Y
            : cursor.Y > window.Bottom
                ? cursor.Y - window.Bottom
                : 0;
        distance = Math.Sqrt((dx * dx) + (dy * dy));
        return true;
    }

    private void DismissFromUser()
    {
        if (_dismissPolicy.IsPinned)
        {
            return;
        }

        _presenceTimer.Stop();
        Hide();
        Dismissed?.Invoke();
    }

    private void ResetPinState()
    {
        _dismissPolicy.SetPinned(false, DateTimeOffset.UtcNow, pointerIsOver: false);
        PinButton.ToolTip = "固定窗口";
        if (PinButton.IsChecked != true)
        {
            return;
        }

        _updatingPinState = true;
        PinButton.IsChecked = false;
        _updatingPinState = false;
    }

    private void PositionWindow(Rect anchor)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        RECT anchorRect;
        if (!anchor.IsEmpty && anchor.Width >= 0 && anchor.Height >= 0)
        {
            anchorRect = new RECT
            {
                Left = (int)Math.Floor(anchor.Left),
                Top = (int)Math.Floor(anchor.Top),
                Right = (int)Math.Ceiling(anchor.Right),
                Bottom = (int)Math.Ceiling(anchor.Bottom)
            };
        }
        else
        {
            GetCursorPos(out var cursor);
            anchorRect = new RECT { Left = cursor.X, Top = cursor.Y, Right = cursor.X + 1, Bottom = cursor.Y + 1 };
        }

        var monitor = MonitorFromRect(ref anchorRect, 2);
        var info = new MONITORINFO { CbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);

        uint dpi = 0;
        if (GetDpiForMonitor(monitor, 0, out var monitorDpiX, out _) == 0)
        {
            dpi = monitorDpiX;
        }

        if (dpi == 0)
        {
            dpi = GetDpiForWindow(hwnd);
        }

        if (dpi == 0)
        {
            dpi = 96;
        }

        var scale = dpi / 96d;
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));

        var x = anchorRect.Left;
        var y = anchorRect.Bottom + 6;
        if (x + width > info.Work.Right)
        {
            x = info.Work.Right - width;
        }

        if (y + height > info.Work.Bottom)
        {
            y = anchorRect.Top - height - 6;
        }

        x = Math.Max(info.Work.Left, x);
        y = Math.Max(info.Work.Top, y);
        SetWindowPos(hwnd, new nint(-1), x, y, width, height, SwpNoActivate | SwpShowWindow);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var styles = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, styles | WsExNoActivate | WsExToolWindow);
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (CurrentResult.Length == 0)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(CurrentResult);
        }
        catch (Exception)
        {
            // Clipboard contention is transient; keep the popup non-intrusive.
        }
    }

    private void PinButton_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingPinState)
        {
            return;
        }

        _dismissPolicy.SetPinned(true, DateTimeOffset.UtcNow, IsMouseOver);
        PinButton.ToolTip = "取消固定";
        _presenceTimer.Stop();
    }

    private void PinButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingPinState)
        {
            return;
        }

        _dismissPolicy.SetPinned(false, DateTimeOffset.UtcNow, IsMouseOver);
        PinButton.ToolTip = "固定窗口";
        if (IsVisible)
        {
            ResetPresenceTracking();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        ResetPinState();
        DismissFromUser();
    }

    private static nint GetWindowLongPtr(nint hwnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hwnd, index)
        : new nint(GetWindowLong32(hwnd, index));

    private static nint SetWindowLongPtr(nint hwnd, int index, nint value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(hwnd, index, value)
        : new nint(SetWindowLong32(hwnd, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(nint hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int CbSize;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }
}
