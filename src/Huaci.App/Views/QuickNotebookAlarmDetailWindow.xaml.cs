using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Huaci.App.Models;
using Huaci.App.Services.Notebook;
using MediaBrush = System.Windows.Media.Brush;

namespace Huaci.App.Views;

public partial class QuickNotebookAlarmDetailWindow : Window
{
    private readonly IQuickNotebookAlarmService _alarmService;
    private readonly QuickNotebookAlarm _alarm;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _isBusy;

    public QuickNotebookAlarmDetailWindow(
        IQuickNotebookAlarmService alarmService,
        QuickNotebookAlarm alarm)
    {
        _alarmService = alarmService ?? throw new ArgumentNullException(nameof(alarmService));
        _alarm = alarm ?? throw new ArgumentNullException(nameof(alarm));
        InitializeComponent();

        AlarmMessageTextBlock.Text = alarm.Message;
        AlarmDueTimeTextBlock.Text = alarm.DueAt
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss");
        AlarmDetailStatusTextBlock.ToolTip = _alarmService.AlarmStoragePath;
    }

    public Guid AlarmId => _alarm.Id;

    public DateTimeOffset DueAt => _alarm.DueAt;

    public string Message => _alarm.Message;

    protected override void OnClosed(EventArgs e)
    {
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        base.OnClosed(e);
    }

    private void Window_OnPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
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
            // The pointer can be released before DragMove starts.
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void CancelAlarmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            SetBusy(true);
            bool removed = await _alarmService.DeleteAlarmAsync(
                _alarm.Id,
                _lifetimeCancellation.Token);
            if (!removed)
            {
                SetStatus("这个闹铃已经不存在", true);
                return;
            }

            DialogResult = true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"取消闹铃失败：{exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        CancelAlarmButton.IsEnabled = !busy;
    }

    private void SetStatus(string text, bool isError = false)
    {
        AlarmDetailStatusTextBlock.Text = text;
        AlarmDetailStatusTextBlock.Foreground = (MediaBrush)FindResource(
            isError ? "ErrorBrush" : "TextSecondaryBrush");
    }
}
