using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Huaci.App.Models;
using MediaBrush = System.Windows.Media.Brush;

namespace Huaci.App.Views;

public partial class ManualTranslationWindow : Window
{
    private bool _allowClose;
    private TranslationDirection _direction = TranslationDirection.EnglishToSimplifiedChinese;

    public ManualTranslationWindow()
    {
        InitializeComponent();
        UpdateDirectionUi();
    }

    public event Action<string>? TranslateRequested;

    public event Action<TranslationDirection>? DirectionChanged;

    public TranslationDirection Direction => _direction;

    public string SourceLanguage => _direction == TranslationDirection.EnglishToSimplifiedChinese
        ? "en"
        : "zh-CN";

    public string TargetLanguage => _direction == TranslationDirection.EnglishToSimplifiedChinese
        ? "zh-CN"
        : "en";

    public TranslationRequest CreateTranslationRequest(string text) =>
        new(text, SourceLanguage, TargetLanguage);

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
        Topmost = false;
        Focus();
        SourceTextBox.Focus();
    }

    public void SetTranslation(string source, string result)
    {
        SourceTextBox.Text = source;
        ResultTextBox.Text = result;
    }

    public void SetStatus(string text, bool isError = false)
    {
        StatusTextBlock.Text = text;
        StatusTextBlock.Foreground = (MediaBrush)FindResource(isError ? "ErrorBrush" : "TextSecondaryBrush");
    }

    public void SetBusy(bool busy)
    {
        TranslateButton.IsEnabled = !busy;
        DirectionButton.IsEnabled = !busy;
        TranslateButton.Content = busy ? "翻译中…" : "翻译";
    }

    public void SetDirection(TranslationDirection direction)
    {
        if (_direction == direction)
        {
            return;
        }

        _direction = direction;
        UpdateDirectionUi();
        DirectionChanged?.Invoke(direction);
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

    private void DirectionButton_OnClick(object sender, RoutedEventArgs e)
    {
        string previousSource = SourceTextBox.Text;
        string previousResult = ResultTextBox.Text;

        SetDirection(_direction == TranslationDirection.EnglishToSimplifiedChinese
            ? TranslationDirection.SimplifiedChineseToEnglish
            : TranslationDirection.EnglishToSimplifiedChinese);

        if (!string.IsNullOrWhiteSpace(previousResult))
        {
            SourceTextBox.Text = previousResult;
            ResultTextBox.Text = previousSource;
        }

        SetStatus(_direction == TranslationDirection.EnglishToSimplifiedChinese
            ? "已切换为英译中"
            : "已切换为中译英");
        SourceTextBox.Focus();
        SourceTextBox.CaretIndex = SourceTextBox.Text.Length;
    }

    private void TranslateButton_OnClick(object sender, RoutedEventArgs e)
    {
        var value = SourceTextBox.Text.Trim();
        if (value.Length == 0)
        {
            SetStatus("请先输入需要翻译的内容", true);
            SourceTextBox.Focus();
            return;
        }

        TranslateRequested?.Invoke(value);
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
        SourceTextBox.Focus();
    }

    private void UpdateDirectionUi()
    {
        bool toChinese = _direction == TranslationDirection.EnglishToSimplifiedChinese;
        string sourceName = toChinese ? "英语" : "简体中文";
        string targetName = toChinese ? "简体中文" : "英语";
        string nextDirection = toChinese ? "中译英" : "英译中";

        SourceLanguageTextBlock.Text = sourceName;
        TargetLanguageTextBlock.Text = targetName;
        SourceLabelTextBlock.Text = $"{sourceName}原文";
        ResultLabelTextBlock.Text = $"{targetName}译文";
        DirectionSummaryTextBlock.Text = $"{sourceName} → {targetName}";
        DirectionButton.ToolTip = $"切换为{nextDirection}";
        System.Windows.Automation.AutomationProperties.SetName(DirectionButton, $"切换为{nextDirection}");
        System.Windows.Automation.AutomationProperties.SetName(SourceTextBox, $"{sourceName}原文");
        System.Windows.Automation.AutomationProperties.SetName(ResultTextBox, $"{targetName}译文");
    }
}
