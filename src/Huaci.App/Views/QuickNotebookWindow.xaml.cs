using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Huaci.App.Models;
using Huaci.App.Services.Notebook;
using DrawingImage = System.Drawing.Image;
using MediaBrush = System.Windows.Media.Brush;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataObject = System.Windows.IDataObject;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfMessageBox = System.Windows.MessageBox;

namespace Huaci.App.Views;

public partial class QuickNotebookWindow : Window
{
    private readonly IQuickNotebookService _notebookService;
    private readonly ObservableCollection<QuickNotebookHistoryItem> _history = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _refreshCancellation;
    private bool _allowClose;
    private bool _isBusy;

    public QuickNotebookWindow()
        : this(new QuickNotebookService())
    {
    }

    public QuickNotebookWindow(IQuickNotebookService notebookService)
    {
        _notebookService = notebookService ?? throw new ArgumentNullException(nameof(notebookService));
        InitializeComponent();
        HistoryListBox.ItemsSource = _history;
        StatusTextBlock.ToolTip = _notebookService.StorageDirectory;
        _notebookService.AlarmsChanged += NotebookService_OnAlarmsChanged;
    }

    public string StorageDirectory => _notebookService.StorageDirectory;

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
        EditorTextBox.Focus();
        _ = RefreshHistoryAsync();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _refreshCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
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

    protected override void OnClosed(EventArgs e)
    {
        _notebookService.AlarmsChanged -= NotebookService_OnAlarmsChanged;
        _refreshCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
        base.OnClosed(e);
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshHistoryAsync();
    }

    private async void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SaveEditorTextAsync();
            return;
        }

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Keep TextBox's native text paste semantics. Only take over Ctrl+V
            // when the clipboard actually contains an image.
            QuickNotebookClipboardContent? content = TryReadCurrentClipboardContent();
            if (content?.Image is not null)
            {
                e.Handled = true;
                await SaveClipboardContentAsync(content, savePastedText: false);
            }
        }
    }

    private void EditorTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (EditorPlaceholderTextBlock is null)
        {
            return;
        }

        EditorPlaceholderTextBlock.Visibility = string.IsNullOrEmpty(EditorTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Window_OnPreviewDragOver(object sender, WpfDragEventArgs e)
    {
        e.Effects = QuickNotebookClipboardReader.ContainsSupportedData(e.Data)
            ? WpfDragDropEffects.Copy
            : WpfDragDropEffects.None;
        e.Handled = true;
        DropHintBorder.Visibility = e.Effects == WpfDragDropEffects.Copy
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Window_OnPreviewDragLeave(object sender, WpfDragEventArgs e)
    {
        DropHintBorder.Visibility = Visibility.Collapsed;
    }

    private async void Window_OnPreviewDrop(object sender, WpfDragEventArgs e)
    {
        e.Handled = true;
        DropHintBorder.Visibility = Visibility.Collapsed;
        await SaveDroppedDataAsync(e.Data);
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

    private void HideButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshHistoryAsync();
    }

    private void OpenFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _notebookService.OpenStorageFolder();
            SetStatus("已打开 save 文件夹");
        }
        catch (Exception exception)
        {
            SetStatus($"无法打开文件夹：{exception.Message}", true);
        }
    }

    private async void SaveTextButton_OnClick(object sender, RoutedEventArgs e)
    {
        await SaveEditorTextAsync();
    }

    private async void PasteSaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        await PasteFromClipboardAsync(savePastedText: true);
    }

    private async void AlarmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        string initialMessage = EditorTextBox.Text.Trim();
        bool messageCameFromEditor = initialMessage.Length > 0;
        if (initialMessage.Length == 0
            && HistoryListBox.SelectedItem is QuickNotebookHistoryItem selected)
        {
            if (selected.Alarm is { } selectedAlarm)
            {
                initialMessage = selectedAlarm.Message;
            }
            else if (selected.Entry is { IsText: true } selectedEntry)
            {
                try
                {
                    SetBusy(true);
                    initialMessage = (await _notebookService.ReadTextAsync(
                        selectedEntry,
                        _lifetimeCancellation.Token)).Trim();
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SetStatus($"读取提醒文字失败：{exception.Message}", true);
                    return;
                }
                finally
                {
                    SetBusy(false);
                }
            }
        }

        if (initialMessage.Length == 0)
        {
            SetStatus(
                HistoryListBox.SelectedItem is QuickNotebookHistoryItem { Entry.IsImage: true }
                    ? "图片笔记没有文字，请先在编辑框输入提醒内容"
                    : "请先输入提醒文字，或选择一条文字笔记",
                true);
            EditorTextBox.Focus();
            return;
        }

        var alarmWindow = new QuickNotebookAlarmWindow(_notebookService, initialMessage)
        {
            Owner = this
        };
        bool alarmScheduled = alarmWindow.ShowDialog() == true;
        if (alarmScheduled && messageCameFromEditor)
        {
            EditorTextBox.Clear();
            EditorTextBox.Focus();
            SetStatus("闹铃已设置，输入框已清空");
        }

        await RefreshHistoryAsync(preserveStatus: true);
    }

    private async void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        await CopySelectedEntryAsync();
    }

    private async void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || HistoryListBox.SelectedItem is not QuickNotebookHistoryItem selected)
        {
            return;
        }

        bool isAlarm = selected.Alarm is not null;
        MessageBoxResult result = WpfMessageBox.Show(
            this,
            isAlarm ? "确定取消这个闹铃吗？" : "确定删除这条本地笔记吗？",
            isAlarm ? "取消闹铃" : "快速笔记",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true);
            if (selected.Alarm is { } alarm)
            {
                await _notebookService.DeleteAlarmAsync(
                    alarm.Id,
                    _lifetimeCancellation.Token);
                SetStatus("闹铃已取消");
            }
            else if (selected.Entry is { } entry)
            {
                await _notebookService.DeleteAsync(
                    entry,
                    _lifetimeCancellation.Token);
                SetStatus("笔记已删除");
            }

            await RefreshHistoryAsync(preserveStatus: true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"删除失败：{exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void HistoryListBox_OnSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        bool hasSelection = HistoryListBox.SelectedItem is QuickNotebookHistoryItem;
        CopyButton.IsEnabled = hasSelection && !_isBusy;
        DeleteButton.IsEnabled = hasSelection && !_isBusy;
    }

    private async void HistoryListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_isBusy || HistoryListBox.SelectedItem is not QuickNotebookHistoryItem selected)
        {
            return;
        }

        if (selected.Alarm is { } alarm)
        {
            ShowAlarmDetails(alarm);
            return;
        }

        if (selected.Entry is not { } entry)
        {
            return;
        }

        if (entry.IsImage)
        {
            await CopySelectedEntryAsync();
            return;
        }

        try
        {
            SetBusy(true);
            EditorTextBox.Text = await _notebookService.ReadTextAsync(
                entry,
                _lifetimeCancellation.Token);
            EditorTextBox.CaretIndex = EditorTextBox.Text.Length;
            EditorTextBox.Focus();
            SetStatus("历史文字已载入编辑框");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"读取失败：{exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task PasteFromClipboardAsync(bool savePastedText)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            SetBusy(true);
            QuickNotebookClipboardContent? clipboard = await ReadClipboardAsync(_lifetimeCancellation.Token);
            if (clipboard is null)
            {
                SetStatus("剪贴板中没有可用的文字或图片", true);
                return;
            }

            await ApplyClipboardSnapshotAsync(
                clipboard,
                savePastedText,
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"粘贴失败：{exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveClipboardContentAsync(
        QuickNotebookClipboardContent content,
        bool savePastedText)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            SetBusy(true);
            await ApplyClipboardSnapshotAsync(
                content,
                savePastedText,
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"粘贴失败：{exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static QuickNotebookClipboardContent? TryReadCurrentClipboardContent()
    {
        try
        {
            WpfDataObject? dataObject = WpfClipboard.GetDataObject();
            QuickNotebookClipboardContent? content = dataObject is null
                ? null
                : QuickNotebookClipboardReader.Read(dataObject);
            if (content?.Image is not null)
            {
                return content;
            }

            return TryReadLegacyClipboardImage() ?? content;
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task SaveDroppedDataAsync(WpfDataObject dataObject)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            SetBusy(true);
            QuickNotebookClipboardContent? dropped = QuickNotebookClipboardReader.Read(dataObject);
            if (dropped is null)
            {
                SetStatus("拖入的内容不是可识别的文字或图片", true);
                return;
            }

            await ApplyClipboardSnapshotAsync(
                dropped,
                saveText: false,
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"拖入失败：{exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyClipboardSnapshotAsync(
        QuickNotebookClipboardContent clipboard,
        bool saveText,
        CancellationToken cancellationToken)
    {
        if (clipboard.Image is not null)
        {
            await _notebookService.SaveImageAsync(clipboard.Image, cancellationToken);
            SetStatus($"{clipboard.SourceLabel}已保存为 PNG");
            await RefreshHistoryAsync(preserveStatus: true);
            return;
        }

        if (string.IsNullOrEmpty(clipboard.Text))
        {
            SetStatus("没有可用的文字内容", true);
            return;
        }

        InsertTextAtCaret(clipboard.Text);
        if (!saveText)
        {
            SetStatus("文字已粘贴，按 Ctrl+Enter 保存");
            return;
        }

        if (string.IsNullOrWhiteSpace(EditorTextBox.Text))
        {
            SetStatus("剪贴板文字为空", true);
            return;
        }

        await _notebookService.SaveTextAsync(EditorTextBox.Text, cancellationToken);
        EditorTextBox.Clear();
        SetStatus("剪贴板文字已保存");
        await RefreshHistoryAsync(preserveStatus: true);
    }

    private void InsertTextAtCaret(string text)
    {
        EditorTextBox.Focus();
        int insertionStart = EditorTextBox.SelectionStart;
        EditorTextBox.SelectedText = text;
        EditorTextBox.CaretIndex = insertionStart + text.Length;
        EditorTextBox.SelectionLength = 0;
    }

    private async Task SaveEditorTextAsync()
    {
        if (_isBusy)
        {
            return;
        }

        string text = EditorTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("请先输入需要保存的文字", true);
            EditorTextBox.Focus();
            return;
        }

        try
        {
            SetBusy(true);
            await _notebookService.SaveTextAsync(text, _lifetimeCancellation.Token);
            EditorTextBox.Clear();
            SetStatus("文字已保存到本地");
            await RefreshHistoryAsync(preserveStatus: true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"保存失败：{exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CopySelectedEntryAsync()
    {
        if (_isBusy || HistoryListBox.SelectedItem is not QuickNotebookHistoryItem selected)
        {
            return;
        }

        try
        {
            SetBusy(true);
            if (selected.Alarm is { } alarm)
            {
                await WriteClipboardAsync(
                    () => WpfClipboard.SetText(alarm.Message),
                    _lifetimeCancellation.Token);
                SetStatus("闹铃文字已复制");
            }
            else if (selected.Entry is { IsText: true } textEntry)
            {
                string text = await _notebookService.ReadTextAsync(
                    textEntry,
                    _lifetimeCancellation.Token);
                await WriteClipboardAsync(
                    () => WpfClipboard.SetText(text),
                    _lifetimeCancellation.Token);
                SetStatus("文字已复制");
            }
            else if (selected.Entry is { IsImage: true } imageEntry)
            {
                byte[] bytes = await _notebookService.ReadImageAsync(
                    imageEntry,
                    _lifetimeCancellation.Token);
                BitmapSource image = DecodeImage(bytes);
                await WriteClipboardAsync(
                    () => WpfClipboard.SetImage(image),
                    _lifetimeCancellation.Token);
                SetStatus("图片已复制");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"复制失败：{exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshHistoryAsync(bool preserveStatus = false)
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        CancellationToken cancellationToken = _refreshCancellation.Token;

        try
        {
            IReadOnlyList<QuickNotebookEntry> entries =
                await _notebookService.GetHistoryAsync(cancellationToken);
            IReadOnlyList<QuickNotebookAlarm> alarms =
                await _notebookService.GetPendingAlarmsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            QuickNotebookHistoryItem[] combined = entries
                .Select(entry => new QuickNotebookHistoryItem(entry))
                .Concat(alarms.Select(alarm => new QuickNotebookHistoryItem(alarm)))
                .OrderByDescending(item => item.SortAt)
                .ToArray();
            await Dispatcher.InvokeAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _history.Clear();
                    foreach (QuickNotebookHistoryItem item in combined)
                    {
                        _history.Add(item);
                    }

                    HistoryCountTextBlock.Text = $"{_history.Count} 条";
                    EmptyStatePanel.Visibility = _history.Count == 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    if (!preserveStatus)
                    {
                        SetStatus(
                            _history.Count == 0
                                ? "内容保存在本地 save 文件夹"
                                : "历史记录已刷新");
                    }
                },
                DispatcherPriority.DataBind,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"读取历史失败：{exception.Message}", true);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        AlarmButton.IsEnabled = !busy;
        SaveTextButton.IsEnabled = !busy;
        PasteSaveButton.IsEnabled = !busy;
        bool hasSelection = HistoryListBox.SelectedItem is QuickNotebookHistoryItem;
        CopyButton.IsEnabled = !busy && hasSelection;
        DeleteButton.IsEnabled = !busy && hasSelection;
    }

    private void SetStatus(string text, bool isError = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                _ = Dispatcher.BeginInvoke(
                    () => SetStatus(text, isError),
                    DispatcherPriority.DataBind);
            }

            return;
        }

        StatusTextBlock.Text = text;
        StatusTextBlock.Foreground = (MediaBrush)FindResource(
            isError ? "ErrorBrush" : "TextSecondaryBrush");
    }

    private static async Task<QuickNotebookClipboardContent?> ReadClipboardAsync(
        CancellationToken cancellationToken)
    {
        ExternalException? lastException = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                WpfDataObject? dataObject = WpfClipboard.GetDataObject();
                QuickNotebookClipboardContent? snapshot = dataObject is null
                    ? null
                    : QuickNotebookClipboardReader.Read(dataObject);
                if (snapshot?.Image is not null)
                {
                    return snapshot;
                }

                // Some applications publish only a legacy CF_DIB bitmap that the WPF
                // data object cannot materialize. WinForms handles that conversion.
                QuickNotebookClipboardContent? legacyImage = TryReadLegacyClipboardImage();
                if (legacyImage is not null)
                {
                    return legacyImage;
                }

                return snapshot;
            }
            catch (ExternalException exception)
            {
                lastException = exception;
                await Task.Delay(45, cancellationToken);
            }
        }

        throw new InvalidOperationException("剪贴板暂时被其他程序占用。", lastException);
    }

    private static QuickNotebookClipboardContent? TryReadLegacyClipboardImage()
    {
        if (!System.Windows.Forms.Clipboard.ContainsImage())
        {
            return null;
        }

        using DrawingImage? drawingImage = System.Windows.Forms.Clipboard.GetImage();
        BitmapSource? image = QuickNotebookClipboardReader.TryConvertToBitmapSource(drawingImage);
        return image is null
            ? null
            : new QuickNotebookClipboardContent(null, image, "剪贴板图片");
    }

    private static async Task WriteClipboardAsync(Action writeAction, CancellationToken cancellationToken)
    {
        ExternalException? lastException = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                writeAction();
                return;
            }
            catch (ExternalException exception)
            {
                lastException = exception;
                await Task.Delay(45, cancellationToken);
            }
        }

        throw new InvalidOperationException("剪贴板暂时被其他程序占用。", lastException);
    }

    private static BitmapSource DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void AlarmBadgeButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isBusy
            || sender is not System.Windows.Controls.Button
            {
                Tag: QuickNotebookAlarm alarm
            })
        {
            return;
        }

        ShowAlarmDetails(alarm);
    }

    private void ShowAlarmDetails(QuickNotebookAlarm alarm)
    {
        var detailWindow = new QuickNotebookAlarmDetailWindow(_notebookService, alarm)
        {
            Owner = this
        };
        detailWindow.ShowDialog();
    }

    private void NotebookService_OnAlarmsChanged(object? sender, EventArgs e)
    {
        if (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            _ = RefreshHistoryAsync(preserveStatus: true);
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () => _ = RefreshHistoryAsync(preserveStatus: true));
    }

}

public sealed class QuickNotebookHistoryItem
{
    public QuickNotebookHistoryItem(QuickNotebookEntry entry)
    {
        Entry = entry;
        SortAt = entry.CreatedAt;
        if (entry.IsImage)
        {
            Thumbnail = TryLoadThumbnail(entry.FilePath);
        }
    }

    public QuickNotebookHistoryItem(QuickNotebookAlarm alarm)
    {
        Alarm = alarm;
        SortAt = alarm.CreatedAt;
    }

    public QuickNotebookEntry? Entry { get; }

    public QuickNotebookAlarm? Alarm { get; }

    public DateTimeOffset SortAt { get; }

    public string IconGlyph => Alarm is not null
        ? "\uE823"
        : Entry?.IsText == true
            ? "\uE8A5"
            : "\uEB9F";

    public Visibility IconVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ThumbnailVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;

    public ImageSource? Thumbnail { get; }

    public string Preview => Alarm is { } alarm
        ? NormalizePreview(alarm.Message)
        : string.IsNullOrWhiteSpace(Entry?.Preview)
            ? (Entry?.IsText == true ? "空白文字" : "PNG 图片")
            : Entry.Preview;

    public string Detail => Alarm is { } alarm
        ? $"闹铃 · {FormatDueTime(alarm.DueAt)}"
        : $"{(Entry?.IsText == true ? "文字" : "图片")} · {FormatFileSize(Entry?.SizeBytes ?? 0)}";

    public string TimeText => Alarm is { } alarm
        ? alarm.DueAt.ToLocalTime().ToString("MM-dd HH:mm")
        : Entry?.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm") ?? string.Empty;

    public System.Windows.Media.Brush TimeBrush => Alarm is null
        ? System.Windows.Media.Brushes.Gray
        : new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(241, 184, 75));

    public Visibility AlarmBadgeVisibility => Alarm is null
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string AlarmToolTip => Alarm is { } alarm
        ? $"查看闹铃：{alarm.DueAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        : string.Empty;

    private static ImageSource? TryLoadThumbnail(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 76;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException
                                           or InvalidOperationException
                                           or ArgumentException
                                           or FormatException)
        {
            return null;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes / (1024d * 1024d):0.#} MB";
    }

    private static string FormatDueTime(DateTimeOffset dueAt)
    {
        DateTimeOffset local = dueAt.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? $"今天 {local:HH:mm}"
            : local.Date == DateTimeOffset.Now.Date.AddDays(1)
                ? $"明天 {local:HH:mm}"
                : local.ToString("yyyy-MM-dd HH:mm");
    }

    private static string NormalizePreview(string message) =>
        string.Join(
            " ",
            message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
