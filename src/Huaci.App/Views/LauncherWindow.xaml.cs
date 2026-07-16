using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Huaci.App.Models;
using MediaBrush = System.Windows.Media.Brush;

namespace Huaci.App.Views;

public partial class LauncherWindow : Window
{
    private const int ModulesPerRow = 4;
    private const double CompactWidth = 256;
    private const double HeaderHeight = 38;
    private const double ModuleRowHeight = 58;
    private const double ModuleAreaVerticalMargin = 8;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    private bool _allowClose;
    private bool _autoCaptureEnabled;

    public LauncherWindow()
    {
        InitializeComponent();
        UpdateCompactSize();
    }

    public event Action? HideRequested;
    public event Action<bool>? AutoCaptureChanged;
    public event Action? OpenManualTranslationRequested;
    public event Action? OpenScreenshotTranslationRequested;
    public event Action? OpenQuickNotebookRequested;
    public event Action? OpenSettingsRequested;

    public bool ShouldHideFromGlobalToggle =>
        IsVisible
        && WindowState != WindowState.Minimized;

    public void LoadSettings(AppSettings settings, bool hasApiKey)
    {
        SetAutoCaptureState(settings.AutoCaptureEnabled);
        SetServiceConfigured(hasApiKey);

        if (settings.MainWindowLeft is double left && settings.MainWindowTop is double top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        UpdateCompactSize();
    }

    public void SetAutoCaptureState(bool enabled)
    {
        _autoCaptureEnabled = enabled;
        AutoCaptureModuleButton.IsChecked = enabled;
        AutoCaptureModuleDot.Fill = (MediaBrush)FindResource(enabled ? "SuccessBrush" : "TextWeakBrush");
        AutoCaptureModuleButton.ToolTip = enabled ? "自动划词已开启，点击暂停" : "自动划词已暂停，点击开启";
        UpdateHeaderStatus();
    }

    public void SetServiceConfigured(bool configured)
    {
        SettingsModuleDot.Fill = (MediaBrush)FindResource(configured ? "SuccessBrush" : "WarningBrush");
        SettingsModuleDot.ToolTip = configured ? "翻译已就绪" : "翻译服务暂不可用";
    }

    public void SetStatus(string text, bool isError = false)
    {
        StatusTextBlock.Text = text;
        WindowShell.ToolTip = text;
        HeaderStatusDot.ToolTip = text;
        HeaderStatusDot.Fill = (MediaBrush)FindResource(isError
            ? "ErrorBrush"
            : _autoCaptureEnabled ? "SuccessBrush" : "TextWeakBrush");
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        KeepAboveWithoutActivation();
        if (!Activate())
        {
            nint handle = new WindowInteropHelper(this).Handle;
            if (handle != nint.Zero)
            {
                _ = SetForegroundWindow(handle);
            }
        }

        if (IsActive)
        {
            Focus();
        }
    }

    public void KeepAboveWithoutActivation()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        Topmost = true;
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
    }

    public void HideToTray()
    {
        if (!IsVisible)
        {
            return;
        }

        Hide();
        HideRequested?.Invoke();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    private void UpdateHeaderStatus()
    {
        HeaderStatusDot.Fill = (MediaBrush)FindResource(_autoCaptureEnabled ? "SuccessBrush" : "TextWeakBrush");
        HeaderStatusDot.ToolTip = _autoCaptureEnabled ? "自动划词运行中" : "自动划词已暂停";
    }

    private void UpdateCompactSize()
    {
        int rowCount = Math.Max(1, (ModuleGrid.Children.Count + ModulesPerRow - 1) / ModulesPerRow);
        double compactHeight = HeaderHeight + ModuleAreaVerticalMargin + (rowCount * ModuleRowHeight);

        Width = MinWidth = MaxWidth = CompactWidth;
        Height = MinHeight = MaxHeight = compactHeight;
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        e.Handled = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The pointer can be released before a synthetic test begins DragMove.
        }
    }

    private void HideButton_OnClick(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void AutoCaptureModuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        AutoCaptureChanged?.Invoke(AutoCaptureModuleButton.IsChecked == true);
    }

    private void ManualTranslationModuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenManualTranslationRequested?.Invoke();
    }

    private void ScreenshotTranslationModuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenScreenshotTranslationRequested?.Invoke();
    }

    private void QuickNotebookModuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenQuickNotebookRequested?.Invoke();
    }

    private void SettingsModuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
