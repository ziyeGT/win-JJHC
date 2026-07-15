using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Huaci.App.Models;
using MediaBrush = System.Windows.Media.Brush;

namespace Huaci.App.Views;

public enum SettingsSection
{
    Capture,
    Service
}

public partial class SettingsWindow : Window
{
    private bool _allowClose;
    private bool _hasApiKey;
    private bool _offlineAvailable;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public event Action<SettingsWindowInput>? SaveSettingsRequested;

    public void LoadSettings(AppSettings settings, bool hasApiKey, bool offlineAvailable = true)
    {
        _hasApiKey = hasApiKey;
        _offlineAvailable = offlineAvailable;
        AutoCaptureCheckBox.IsChecked = settings.AutoCaptureEnabled;
        ClipboardFallbackCheckBox.IsChecked = settings.ClipboardFallbackEnabled;
        CaptureDelayTextBox.Text = settings.CaptureDelayMs.ToString();
        PopupDurationTextBox.Text = settings.PopupDurationSeconds.ToString();
        ApiBaseUrlTextBox.Text = settings.ApiBaseUrl;
        ModelTextBox.Text = settings.Model;
        TranslationModeComboBox.SelectedIndex = settings.TranslationMode switch
        {
            TranslationRouteMode.OfflineOnly => 1,
            TranslationRouteMode.OnlineOnly => 2,
            _ => 0
        };
        ApiKeyPasswordBox.Password = string.Empty;
        UpdateServiceState(hasApiKey);
        UpdateOfflineState(offlineAvailable);
        UpdateCaptureState(settings.AutoCaptureEnabled);
    }

    public void SetAutoCaptureState(bool enabled)
    {
        AutoCaptureCheckBox.IsChecked = enabled;
        UpdateCaptureState(enabled);
    }

    public void SetServiceConfigured(bool configured)
    {
        _hasApiKey = configured;
        UpdateServiceState(configured);
    }

    public void SetOfflineAvailability(bool available, string? message = null)
    {
        _offlineAvailable = available;
        UpdateOfflineState(available, message);
    }

    public void ShowAndActivate(SettingsSection section = SettingsSection.Capture)
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
        Topmost = false;
        Focus();
        ShowSection(section);
    }

    public void ShowSection(SettingsSection section)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (section == SettingsSection.Service)
                {
                    ServiceSettingsSection.BringIntoView();
                    ApiKeyPasswordBox.Focus();
                }
                else
                {
                    SettingsScrollViewer.ScrollToTop();
                    AutoCaptureCheckBox.Focus();
                }
            }));
    }

    public void SetStatus(string text, bool isError = false)
    {
        StatusTextBlock.Text = text;
        StatusTextBlock.Foreground = (MediaBrush)FindResource(isError ? "ErrorBrush" : "TextSecondaryBrush");
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
            Hide();
            return;
        }

        base.OnClosing(e);
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
        Hide();
    }

    private void SaveSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CaptureDelayTextBox.Text, out var delay))
        {
            SetStatus("取词响应需要填写数字", true);
            CaptureDelayTextBox.Focus();
            return;
        }

        if (!int.TryParse(PopupDurationTextBox.Text, out var duration))
        {
            SetStatus("弹窗停留时间需要填写数字", true);
            PopupDurationTextBox.Focus();
            return;
        }

        var input = new SettingsWindowInput
        {
            AutoCaptureEnabled = AutoCaptureCheckBox.IsChecked == true,
            ClipboardFallbackEnabled = ClipboardFallbackCheckBox.IsChecked == true,
            CaptureDelayMs = Math.Clamp(delay, 50, 100),
            PopupDurationSeconds = Math.Clamp(duration, 2, 60),
            TranslationMode = TranslationModeComboBox.SelectedIndex switch
            {
                1 => TranslationRouteMode.OfflineOnly,
                2 => TranslationRouteMode.OnlineOnly,
                _ => TranslationRouteMode.OfflineFirst
            },
            ApiBaseUrl = ApiBaseUrlTextBox.Text.Trim(),
            Model = ModelTextBox.Text.Trim(),
            ApiKey = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password) ? null : ApiKeyPasswordBox.Password
        };

        SaveSettingsRequested?.Invoke(input);
        ApiKeyPasswordBox.Password = string.Empty;
    }

    private void UpdateCaptureState(bool enabled)
    {
        var brush = (MediaBrush)FindResource(enabled ? "SuccessBrush" : "TextWeakBrush");
        CaptureStateTextBlock.Text = enabled ? "运行中" : "已暂停";
        CaptureStateTextBlock.Foreground = brush;
    }

    private void UpdateServiceState(bool configured)
    {
        ServiceStateDot.Fill = (MediaBrush)FindResource(configured ? "SuccessBrush" : "WarningBrush");
        ApiKeyHintTextBlock.Text = configured
            ? "已安全保存密钥；留空表示继续使用现有密钥"
            : "尚未配置；离线英译中不需要密钥";
    }

    private void UpdateOfflineState(bool available, string? message = null)
    {
        OfflineStateDot.Fill = (MediaBrush)FindResource(available ? "SuccessBrush" : "WarningBrush");
        OfflineStateTextBlock.Text = message ?? (available
            ? "内置英语 → 简体中文模型已就绪，可断网使用"
            : "内置模型暂时不可用，请检查完整 ZIP 与 WebView2 运行库");
    }
}

public sealed class SettingsWindowInput
{
    public bool AutoCaptureEnabled { get; init; }
    public bool ClipboardFallbackEnabled { get; init; }
    public int CaptureDelayMs { get; init; }
    public int PopupDurationSeconds { get; init; }
    public TranslationRouteMode TranslationMode { get; init; }
    public required string ApiBaseUrl { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }
}
