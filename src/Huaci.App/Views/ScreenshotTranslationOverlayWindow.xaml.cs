using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Huaci.App.Models;
using Huaci.App.Services.Capture;
using Huaci.App.Services.Ocr;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;
using MediaColor = System.Windows.Media.Color;

namespace Huaci.App.Views;

/// <summary>
/// A persistent translation surface occupying the exact physical screenshot
/// selection. Results are reconstructed inside the selected area and remain
/// until the user explicitly closes them.
/// </summary>
public partial class ScreenshotTranslationOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExToolWindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;
    private static readonly nint HwndTopmost = new(-1);

    private static readonly MediaColor DefaultReadingBackground = MediaColor.FromRgb(247, 247, 247);
    private static readonly MediaColor DefaultReadingForeground = MediaColor.FromRgb(52, 54, 59);
    private static readonly SolidColorBrush LoadingForeground = CreateFrozenBrush(MediaColor.FromRgb(239, 241, 245));
    private static readonly SolidColorBrush ErrorForeground = CreateFrozenBrush(MediaColor.FromRgb(255, 178, 178));

    private ScreenRect? _physicalBounds;
    private HwndSource? _windowSource;
    private IReadOnlyList<OcrTextBlock> _ocrBlocks = Array.Empty<OcrTextBlock>();
    private bool _allowClose;
    private bool _isUserDismissalInProgress;
    private bool _hasResult;
    private bool _isShowingOriginal;

    public ScreenshotTranslationOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => ApplyAdaptiveLayout();
        PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Raised after X, right-click or Escape explicitly dismisses the overlay.</summary>
    public event Action? Dismissed;

    public ScreenRect? PhysicalBounds => _physicalBounds;

    public string CurrentSource { get; private set; } = string.Empty;

    public string CurrentResult { get; private set; } = string.Empty;

    public TranslationOrigin? CurrentOrigin { get; private set; }

    public bool IsShowingOriginal => _isShowingOriginal;

    public void ShowLoading(
        string source,
        ScreenRect physicalBounds,
        string loadingText,
        ReadOnlyMemory<byte>? frozenPng = null)
    {
        SetCommonState(source, physicalBounds);
        if (frozenPng is { Length: > 0 } imageBytes)
        {
            FrozenBackgroundImage.Source = DecodeFrozenImage(imageBytes.Span);
            ApplyReadingPalette();
        }

        CurrentResult = string.Empty;
        CurrentOrigin = null;
        _ocrBlocks = Array.Empty<OcrTextBlock>();
        _hasResult = false;
        _isShowingOriginal = false;

        ResultTextBlock.Text = string.Empty;
        ResultTextBlock.Foreground = CreateFrozenBrush(DefaultReadingForeground);
        ResultScrollViewer.Visibility = Visibility.Collapsed;
        ReplacementSurface.Visibility = Visibility.Collapsed;

        LoadingTextBlock.Text = loadingText;
        LoadingTextBlock.Foreground = LoadingForeground;
        LoadingProgressBar.Visibility = Visibility.Visible;
        LoadingCard.Visibility = Visibility.Visible;

        OriginBadge.Visibility = Visibility.Collapsed;
        OriginBadge.ToolTip = null;
        ViewToggleButton.Content = "原文";
        ViewToggleButton.ToolTip = "查看原截图";
        ViewToggleButton.IsEnabled = false;
        ViewToggleButton.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
        StatusTextBlock.Text = loadingText.Contains("翻译", StringComparison.Ordinal)
            ? "翻译中"
            : "识别中";

        ShowAtPhysicalBounds();
    }

    public void ShowResult(
        string source,
        string result,
        ScreenRect physicalBounds,
        TranslationOrigin origin,
        bool usedFallback,
        IReadOnlyList<OcrTextBlock>? blocks = null)
    {
        SetCommonState(source, physicalBounds);
        CurrentResult = result;
        CurrentOrigin = origin;
        _ocrBlocks = blocks is null
            ? Array.Empty<OcrTextBlock>()
            : blocks
                .Where(block => !string.IsNullOrWhiteSpace(block.Text)
                    && block.Bounds.Width > 0
                    && block.Bounds.Height > 0)
                .ToArray();
        _hasResult = true;

        ApplyReadingPalette();
        ResultTextBlock.Text = result;
        ResultScrollViewer.ScrollToHome();
        LoadingCard.Visibility = Visibility.Collapsed;
        LoadingProgressBar.Visibility = Visibility.Collapsed;

        OriginTextBlock.Text = origin == TranslationOrigin.Offline ? "离线" : "在线";
        OriginBadge.ToolTip = usedFallback
            ? "离线不可用，本次使用在线服务"
            : origin == TranslationOrigin.Offline
                ? "译文由内置模型在本机生成"
                : "译文由在线服务生成";
        CopyButton.IsEnabled = result.Length > 0;
        ViewToggleButton.IsEnabled = true;
        StatusTextBlock.Text = string.Empty;
        SetOriginalView(showOriginal: false);

        ShowAtPhysicalBounds();
    }

    public void ShowError(string source, string error, ScreenRect physicalBounds)
    {
        SetCommonState(source, physicalBounds);
        CurrentResult = string.Empty;
        CurrentOrigin = null;
        _ocrBlocks = Array.Empty<OcrTextBlock>();
        _hasResult = false;
        _isShowingOriginal = false;

        ResultTextBlock.Text = string.Empty;
        ResultScrollViewer.Visibility = Visibility.Collapsed;
        ReplacementSurface.Visibility = Visibility.Collapsed;

        LoadingTextBlock.Text = error;
        LoadingTextBlock.Foreground = ErrorForeground;
        LoadingProgressBar.Visibility = Visibility.Collapsed;
        LoadingCard.Visibility = Visibility.Visible;

        OriginBadge.Visibility = Visibility.Collapsed;
        OriginBadge.ToolTip = null;
        ViewToggleButton.IsEnabled = false;
        ViewToggleButton.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
        StatusTextBlock.Text = "失败";

        ShowAtPhysicalBounds();
    }

    /// <summary>Hides for a new capture or settings change without reporting a user dismissal.</summary>
    public void HideOverlay()
    {
        if (IsVisible)
        {
            Hide();
        }

        ClearSensitiveState();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        ClearSensitiveState();
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

    private void SetCommonState(string source, ScreenRect physicalBounds)
    {
        if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalBounds),
                "Screenshot overlay bounds must be positive physical pixels.");
        }

        _physicalBounds = physicalBounds;
        CurrentSource = source;
    }

    private static BitmapSource DecodeFrozenImage(ReadOnlySpan<byte> pngBytes)
    {
        using var stream = new MemoryStream(pngBytes.ToArray(), writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ApplyReadingPalette()
    {
        MediaColor background = DefaultReadingBackground;
        if (FrozenBackgroundImage.Source is BitmapSource bitmap)
        {
            background = EstimateDominantColor(bitmap);
        }

        double luminance = RelativeLuminance(background);
        MediaColor foreground = luminance >= 0.48
            ? DefaultReadingForeground
            : MediaColor.FromRgb(240, 242, 246);

        ReplacementSurface.Background = CreateFrozenBrush(background);
        ResultTextBlock.Foreground = CreateFrozenBrush(foreground);
    }

    private static MediaColor EstimateDominantColor(BitmapSource source)
    {
        try
        {
            const int maximumThumbnailEdge = 72;
            double scale = Math.Min(
                1d,
                Math.Min(
                    maximumThumbnailEdge / (double)Math.Max(1, source.PixelWidth),
                    maximumThumbnailEdge / (double)Math.Max(1, source.PixelHeight)));
            BitmapSource thumbnail = scale < 0.999d
                ? new TransformedBitmap(source, new ScaleTransform(scale, scale))
                : source;
            BitmapSource formatted = thumbnail.Format == PixelFormats.Bgra32
                ? thumbnail
                : new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0);

            int width = Math.Max(1, formatted.PixelWidth);
            int height = Math.Max(1, formatted.PixelHeight);
            int stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            var counts = new int[4096];
            var redSums = new int[4096];
            var greenSums = new int[4096];
            var blueSums = new int[4096];

            try
            {
                formatted.CopyPixels(pixels, stride, 0);
                for (int offset = 0; offset < pixels.Length; offset += 4)
                {
                    byte alpha = pixels[offset + 3];
                    if (alpha < 16)
                    {
                        continue;
                    }

                    byte blue = pixels[offset];
                    byte green = pixels[offset + 1];
                    byte red = pixels[offset + 2];
                    int bucket = ((red >> 4) << 8) | ((green >> 4) << 4) | (blue >> 4);
                    counts[bucket]++;
                    redSums[bucket] += red;
                    greenSums[bucket] += green;
                    blueSums[bucket] += blue;
                }

                int dominantBucket = 0;
                for (int index = 1; index < counts.Length; index++)
                {
                    if (counts[index] > counts[dominantBucket])
                    {
                        dominantBucket = index;
                    }
                }

                int count = counts[dominantBucket];
                if (count == 0)
                {
                    return DefaultReadingBackground;
                }

                return MediaColor.FromRgb(
                    (byte)(redSums[dominantBucket] / count),
                    (byte)(greenSums[dominantBucket] / count),
                    (byte)(blueSums[dominantBucket] / count));
            }
            finally
            {
                Array.Clear(pixels);
            }
        }
        catch (Exception)
        {
            return DefaultReadingBackground;
        }
    }

    private static double RelativeLuminance(MediaColor color) =>
        ((0.2126d * color.R) + (0.7152d * color.G) + (0.0722d * color.B)) / 255d;

    private static SolidColorBrush CreateFrozenBrush(MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void ClearSensitiveState()
    {
        _physicalBounds = null;
        CurrentSource = string.Empty;
        CurrentResult = string.Empty;
        CurrentOrigin = null;
        _ocrBlocks = Array.Empty<OcrTextBlock>();
        _hasResult = false;
        _isShowingOriginal = false;

        FrozenBackgroundImage.Source = null;
        ReplacementSurface.Background = CreateFrozenBrush(DefaultReadingBackground);
        ReplacementSurface.Visibility = Visibility.Collapsed;
        ResultTextBlock.Text = string.Empty;
        ResultTextBlock.Foreground = CreateFrozenBrush(DefaultReadingForeground);
        ResultScrollViewer.Visibility = Visibility.Collapsed;
        LoadingTextBlock.Text = string.Empty;
        LoadingCard.Visibility = Visibility.Collapsed;
        OriginBadge.Visibility = Visibility.Collapsed;
        OriginBadge.ToolTip = null;
        StatusTextBlock.Text = string.Empty;
        ViewToggleButton.Content = "原文";
        ViewToggleButton.IsEnabled = false;
        ViewToggleButton.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
    }

    private void ShowAtPhysicalBounds()
    {
        if (_physicalBounds is null)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        PositionWindow();
        UpdateLayout();
        ApplyAdaptiveLayout();
    }

    private void PositionWindow()
    {
        if (_physicalBounds is not { } bounds)
        {
            return;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            HwndTopmost,
            checked((int)Math.Round(bounds.X)),
            checked((int)Math.Round(bounds.Y)),
            Math.Max(1, checked((int)Math.Round(bounds.Width))),
            Math.Max(1, checked((int)Math.Round(bounds.Height))),
            SwpNoActivate | SwpShowWindow);
    }

    private void ApplyAdaptiveLayout()
    {
        double width = Math.Max(1, ActualWidth);
        double height = Math.Max(1, ActualHeight);
        bool compact = width < 230 || height < 115;
        bool tiny = width < 100 || height < 48;

        ContentCard.Margin = tiny ? new Thickness(3) : new Thickness(6);
        ContentCard.Padding = tiny ? new Thickness(1) : new Thickness(3);
        ContentCard.Visibility = width >= 24 && height >= 18
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentCard.Opacity = compact ? 0.9 : 0.82;

        // Origin and progress details remain available through button tooltips.
        // Keeping them out of the visible toolbar preserves the reconstructed page.
        StatusTextBlock.Visibility = Visibility.Collapsed;
        OriginBadge.Visibility = Visibility.Collapsed;
        ViewToggleButton.Visibility = _hasResult && !tiny
            ? Visibility.Visible
            : Visibility.Collapsed;
        CopyButton.Visibility = _hasResult && !tiny
            ? Visibility.Visible
            : Visibility.Collapsed;

        double closeSize = tiny
            ? Math.Clamp(Math.Min(width - 6, height - 6), 12, 22)
            : 25;
        CloseButton.Width = closeSize;
        CloseButton.Height = closeSize;
        ViewToggleButton.Height = compact ? 22 : 25;
        CopyButton.Width = compact ? 22 : 25;
        CopyButton.Height = compact ? 22 : 25;

        LoadingCard.MaxWidth = Math.Max(20, width - 16);
        LoadingCard.Padding = compact
            ? new Thickness(7, 5, 7, 5)
            : new Thickness(10, 8, 10, 8);
        LoadingProgressBar.Visibility = LoadingProgressBar.Visibility == Visibility.Collapsed
            ? Visibility.Collapsed
            : tiny
                ? Visibility.Collapsed
                : Visibility.Visible;

        ApplyReadingLayout(width, height);
    }

    private void ApplyReadingLayout(double width, double height)
    {
        double left = 12;
        double top = 12;
        double right = 12;
        double bottom = 12;
        double estimatedFontSize = Math.Clamp(Math.Min(width / 28d, height / 14d), 12d, 22d);

        if (FrozenBackgroundImage.Source is BitmapSource bitmap && _ocrBlocks.Count > 0)
        {
            double scaleX = width / Math.Max(1d, bitmap.PixelWidth);
            double scaleY = height / Math.Max(1d, bitmap.PixelHeight);
            float minimumX = _ocrBlocks.Min(block => block.Bounds.X);
            float minimumY = _ocrBlocks.Min(block => block.Bounds.Y);
            float maximumX = _ocrBlocks.Max(block => block.Bounds.Right);
            float maximumY = _ocrBlocks.Max(block => block.Bounds.Bottom);

            left = Math.Clamp(minimumX * scaleX, 8d, Math.Max(8d, width * 0.18d));
            top = Math.Clamp(minimumY * scaleY, 8d, Math.Max(8d, height * 0.18d));
            right = Math.Clamp((bitmap.PixelWidth - maximumX) * scaleX, 8d, Math.Max(8d, width * 0.18d));
            bottom = Math.Clamp((bitmap.PixelHeight - maximumY) * scaleY, 8d, Math.Max(8d, height * 0.18d));

            double[] lineHeights = _ocrBlocks
                .Select(block => block.Bounds.Height * scaleY)
                .Where(value => value > 1d)
                .OrderBy(value => value)
                .ToArray();
            if (lineHeights.Length > 0)
            {
                double medianLineHeight = lineHeights[lineHeights.Length / 2];
                double maximumFontSize = height < 80
                    ? Math.Max(10.5d, height * 0.34d)
                    : 34d;
                estimatedFontSize = Math.Clamp(medianLineHeight * 0.82d, 10.5d, maximumFontSize);
            }
        }

        if (width < 120 || height < 60)
        {
            left = right = Math.Min(6d, width * 0.08d);
            top = bottom = Math.Min(6d, height * 0.08d);
            estimatedFontSize = Math.Min(estimatedFontSize, Math.Max(9d, height * 0.3d));
        }

        if (_hasResult && height >= 60)
        {
            // Reserve a slim footer for the floating controls so translated
            // glyphs are never painted underneath them.
            bottom = Math.Max(bottom, 43d);
        }

        ResultScrollViewer.Margin = new Thickness(left, top, right, bottom);
        ResultTextBlock.FontSize = estimatedFontSize;
        ResultTextBlock.LineHeight = Math.Max(estimatedFontSize + 4d, estimatedFontSize * 1.52d);
    }

    private void SetOriginalView(bool showOriginal)
    {
        if (!_hasResult)
        {
            return;
        }

        _isShowingOriginal = showOriginal;
        ReplacementSurface.Visibility = showOriginal ? Visibility.Collapsed : Visibility.Visible;
        ResultScrollViewer.Visibility = showOriginal ? Visibility.Collapsed : Visibility.Visible;
        ViewToggleButton.Content = showOriginal ? "译文" : "原文";
        ViewToggleButton.ToolTip = showOriginal ? "查看重排译文" : "查看原截图";
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        nint styles = GetWindowLongPtr(handle, GwlExStyle);
        _ = SetWindowLongPtr(handle, GwlExStyle, styles | WsExNoActivate | WsExToolWindow);
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        PositionWindow();
    }

    private nint WindowMessageHook(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message is WmDpiChanged or WmDisplayChange)
        {
            _ = Dispatcher.BeginInvoke(
                () =>
                {
                    PositionWindow();
                    UpdateLayout();
                    ApplyAdaptiveLayout();
                },
                DispatcherPriority.Loaded);
        }

        return nint.Zero;
    }

    private void ViewToggleButton_OnClick(object sender, RoutedEventArgs e) =>
        SetOriginalView(!_isShowingOriginal);

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
            // Clipboard contention is transient; leave the translation visible.
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => DismissFromUser();

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DismissFromUser();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, InputKeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        DismissFromUser();
        e.Handled = true;
    }

    private void DismissFromUser()
    {
        if (_isUserDismissalInProgress)
        {
            return;
        }

        _isUserDismissalInProgress = true;
        try
        {
            HideOverlay();
            Dismissed?.Invoke();
        }
        finally
        {
            _isUserDismissalInProgress = false;
        }
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
}
