using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Huaci.App.Models;
using MediaBrush = System.Windows.Media.Brush;

namespace Huaci.App.Views;

public partial class LauncherWindow : Window
{
    private bool _allowClose;
    private bool _autoCaptureEnabled;

    public LauncherWindow()
    {
        InitializeComponent();
    }

    public event Action? HideRequested;
    public event Action<bool>? AutoCaptureChanged;
    public event Action? OpenManualTranslationRequested;
    public event Action? OpenSettingsRequested;

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

        Width = 292;
        Height = 156;
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

        Activate();
        Topmost = true;
        Topmost = PinButton.IsChecked == true;
        Focus();
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

    private void PinButton_OnChanged(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
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

    private void SettingsModuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke();
    }
}
