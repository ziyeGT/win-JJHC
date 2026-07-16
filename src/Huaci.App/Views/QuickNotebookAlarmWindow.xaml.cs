using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Huaci.App.Models;
using Huaci.App.Services.Notebook;
using MediaBrush = System.Windows.Media.Brush;

namespace Huaci.App.Views;

public partial class QuickNotebookAlarmWindow : Window
{
    private readonly IQuickNotebookAlarmService _alarmService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _isBusy;

    public QuickNotebookAlarmWindow(
        IQuickNotebookAlarmService alarmService,
        string initialMessage)
    {
        _alarmService = alarmService ?? throw new ArgumentNullException(nameof(alarmService));
        InitializeComponent();

        AlarmHourComboBox.ItemsSource = Enumerable.Range(0, 24);
        AlarmMinuteComboBox.ItemsSource = Enumerable.Range(0, 60);
        AlarmMessageTextBox.Text = initialMessage?.Trim() ?? string.Empty;
        AlarmStatusTextBlock.ToolTip = _alarmService.AlarmStoragePath;
        SetSelectedTime(DateTimeOffset.Now.AddMinutes(10));
    }

    public string AlarmStoragePath => _alarmService.AlarmStoragePath;

    protected override void OnClosed(EventArgs e)
    {
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        base.OnClosed(e);
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        AlarmMessageTextBox.Focus();
        AlarmMessageTextBox.CaretIndex = AlarmMessageTextBox.Text.Length;
    }

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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

    private void QuickTimeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string minuteText }
            || !int.TryParse(
                minuteText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int minutes))
        {
            return;
        }

        SetSelectedTime(DateTimeOffset.Now.AddMinutes(minutes));
        SetStatus($"已选择 {minutes} 分钟后");
    }

    private async void ScheduleAlarmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        string message = AlarmMessageTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            SetStatus("请先输入提醒文字；图片笔记需要手动填写提醒内容。", true);
            AlarmMessageTextBox.Focus();
            return;
        }

        if (!TryGetSelectedTime(out DateTimeOffset dueAt, out string? validationError))
        {
            SetStatus(validationError ?? "请选择有效的闹铃时间。", true);
            return;
        }

        try
        {
            SetBusy(true);
            QuickNotebookAlarm alarm = await _alarmService.ScheduleAlarmAsync(
                message,
                dueAt,
                _lifetimeCancellation.Token);
            SetStatus($"闹铃已设置：{alarm.DueAt.ToLocalTime():MM-dd HH:mm}");
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"设置闹铃失败：{exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetSelectedTime(DateTimeOffset value)
    {
        DateTime local = value.ToLocalTime().DateTime;
        AlarmDatePicker.SelectedDate = local.Date;
        AlarmHourComboBox.SelectedItem = local.Hour;
        AlarmMinuteComboBox.SelectedItem = local.Minute;
    }

    private bool TryGetSelectedTime(
        out DateTimeOffset dueAt,
        out string? validationError)
    {
        dueAt = default;
        validationError = null;
        if (AlarmDatePicker.SelectedDate is not DateTime selectedDate
            || AlarmHourComboBox.SelectedItem is not int hour
            || AlarmMinuteComboBox.SelectedItem is not int minute)
        {
            validationError = "请选择完整的日期和时间。";
            return false;
        }

        DateTime localTime = new(
            selectedDate.Year,
            selectedDate.Month,
            selectedDate.Day,
            hour,
            minute,
            second: 0,
            DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localTime))
        {
            validationError = "该时间处于夏令时跳转区间，请选择其他时间。";
            return false;
        }

        dueAt = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));
        if (dueAt <= DateTimeOffset.Now)
        {
            validationError = "闹铃时间必须晚于当前时间。";
            return false;
        }

        return true;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        ScheduleAlarmButton.IsEnabled = !busy;
    }

    private void SetStatus(string text, bool isError = false)
    {
        AlarmStatusTextBlock.Text = text;
        AlarmStatusTextBlock.Foreground = (MediaBrush)FindResource(
            isError ? "ErrorBrush" : "TextSecondaryBrush");
    }
}
