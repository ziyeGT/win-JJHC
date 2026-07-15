using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MediaBrush = System.Windows.Media.Brush;

namespace Huaci.App.Views;

public partial class ManualTranslationWindow : Window
{
    private bool _allowClose;

    public ManualTranslationWindow()
    {
        InitializeComponent();
    }

    public event Action<string>? TranslateRequested;

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
        TranslateButton.Content = busy ? "翻译中…" : "翻译";
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
}
