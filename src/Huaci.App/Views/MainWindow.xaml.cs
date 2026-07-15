using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Huaci.App.Models;
using MediaBrush = System.Windows.Media.Brush;

namespace Huaci.App.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _hasApiKey;

    public MainWindow()
    {
        InitializeComponent();
    }

    public event Action? HideRequested;
    public event Action<bool>? PinChanged;
    public event Action<string>? ManualTranslateRequested;
    public event Action<MainWindowSettingsInput>? SaveSettingsRequested;

    public void LoadSettings(AppSettings settings, bool hasApiKey)
    {
        _hasApiKey = hasApiKey;
        AutoCaptureCheckBox.IsChecked = settings.AutoCaptureEnabled;
        ClipboardFallbackCheckBox.IsChecked = settings.ClipboardFallbackEnabled;
        CaptureDelayTextBox.Text = settings.CaptureDelayMs.ToString();
        PopupDurationTextBox.Text = settings.PopupDurationSeconds.ToString();
        ApiBaseUrlTextBox.Text = settings.ApiBaseUrl;
        ModelTextBox.Text = settings.Model;
        ApiKeyPasswordBox.Password = string.Empty;
        ApiKeyHintTextBlock.Text = hasApiKey
            ? "已安全保存密钥；留空表示继续使用现有密钥"
            : "尚未配置；密钥只保存在 Windows 凭据管理器";
        UpdateOverview(settings);

        if (settings.MainWindowLeft is double left && settings.MainWindowTop is double top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        // The compact tool panel has a deliberate fixed footprint; migrate away
        // from dimensions saved by earlier, larger window designs.
        Width = 344;
        Height = 438;
    }

    public void ShowSettingsSection()
    {
        MainTabs.SelectedItem = CaptureTab;
        ShowAndActivate();
    }

    public void ShowServiceSettingsSection()
    {
        MainTabs.SelectedItem = ServiceTab;
        ShowAndActivate();
    }

    public void SetAutoCaptureState(bool enabled)
    {
        AutoCaptureCheckBox.IsChecked = enabled;
        UpdateAutoCaptureVisuals(enabled);
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

    public void SetTranslation(string source, string result)
    {
        SourceTextBox.Text = source;
        ResultTextBox.Text = result;
    }

    public void SetStatus(string text, bool isError = false)
    {
        StatusTextBlock.Text = text;
        StatusTextBlock.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("ErrorBrush")
            : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
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
            HideRequested?.Invoke();
            return;
        }

        base.OnClosing(e);
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.LeftButton == MouseButtonState.Pressed)
        {
            e.Handled = true;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // A synthetic or interrupted mouse press can end before DragMove starts.
            }
        }
    }

    private void OpenManualTranslationButton_OnClick(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = TranslateTab;
        SourceTextBox.Focus();
    }

    private void AutoCaptureCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        UpdateAutoCaptureVisuals(AutoCaptureCheckBox.IsChecked == true);
        SaveSettingsButton_OnClick(sender, e);
    }

    private void HideButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        HideRequested?.Invoke();
    }

    private void PinButton_OnChanged(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
        PinChanged?.Invoke(Topmost);
    }

    private void TranslateButton_OnClick(object sender, RoutedEventArgs e)
    {
        var text = SourceTextBox.Text.Trim();
        if (text.Length == 0)
        {
            SetStatus("请先输入或划选文字", true);
            return;
        }

        ManualTranslateRequested?.Invoke(text);
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultTextBox.Text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(ResultTextBox.Text);
            SetStatus("译文已复制");
        }
        catch (Exception)
        {
            SetStatus("剪贴板暂时不可用", true);
        }
    }

    private void ClearButton_OnClick(object sender, RoutedEventArgs e)
    {
        SourceTextBox.Clear();
        ResultTextBox.Clear();
        SetStatus("已清空");
    }

    private void SaveSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CaptureDelayTextBox.Text, out var delay))
        {
            SetStatus("取词延迟需要填写数字", true);
            return;
        }

        if (!int.TryParse(PopupDurationTextBox.Text, out var duration))
        {
            SetStatus("弹窗停留时间需要填写数字", true);
            return;
        }

        var input = new MainWindowSettingsInput
        {
            AutoCaptureEnabled = AutoCaptureCheckBox.IsChecked == true,
            ClipboardFallbackEnabled = ClipboardFallbackCheckBox.IsChecked == true,
            CaptureDelayMs = Math.Clamp(delay, 50, 100),
            PopupDurationSeconds = Math.Clamp(duration, 2, 60),
            ApiBaseUrl = ApiBaseUrlTextBox.Text.Trim(),
            Model = ModelTextBox.Text.Trim(),
            ApiKey = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password) ? null : ApiKeyPasswordBox.Password
        };

        SaveSettingsRequested?.Invoke(input);
        ApiKeyPasswordBox.Password = string.Empty;
    }

    private void UpdateOverview(AppSettings settings)
    {
        UpdateAutoCaptureVisuals(settings.AutoCaptureEnabled);
        CaptureDelaySummaryTextBlock.Text = $"{settings.CaptureDelayMs} ms";
        PopupDurationSummaryTextBlock.Text = $"{settings.PopupDurationSeconds} 秒";
        UpdateServiceVisuals(_hasApiKey);
    }

    private void UpdateAutoCaptureVisuals(bool enabled)
    {
        var stateBrush = (MediaBrush)FindResource(enabled ? "SuccessBrush" : "TextWeakBrush");
        var stateBackground = (MediaBrush)FindResource(enabled ? "SuccessSoftBrush" : "DisabledSoftBrush");

        HeaderStatusDot.Fill = stateBrush;
        AutoCaptureModuleDot.Fill = stateBrush;
        AutoCaptureStatePillBorder.Background = stateBackground;
        AutoCaptureStateTextBlock.Foreground = stateBrush;
        AutoCaptureStateTextBlock.Text = enabled ? "运行中" : "已暂停";
        AutoCaptureModuleDot.ToolTip = enabled ? "自动划词已开启" : "自动划词已暂停";
    }

    private void UpdateServiceVisuals(bool hasApiKey)
    {
        var stateBrush = (MediaBrush)FindResource(hasApiKey ? "SuccessBrush" : "WarningBrush");
        ServiceModuleDot.Fill = stateBrush;
        ServiceModuleDot.ToolTip = hasApiKey ? "翻译服务已配置" : "翻译服务未配置";
        ServiceSummaryTextBlock.Foreground = stateBrush;
        ServiceSummaryTextBlock.Text = hasApiKey ? "已配置" : "未配置";
    }
}

public sealed class MainWindowSettingsInput
{
    public bool AutoCaptureEnabled { get; init; }
    public bool ClipboardFallbackEnabled { get; init; }
    public int CaptureDelayMs { get; init; }
    public int PopupDurationSeconds { get; init; }
    public required string ApiBaseUrl { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }
}
