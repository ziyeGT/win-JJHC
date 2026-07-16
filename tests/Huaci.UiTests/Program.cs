using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Huaci.App.Models;
using Huaci.App.Services.Notebook;
using Huaci.App.Services.Capture;
using Huaci.App.Services.Ocr;
using Huaci.App.Services.Translation;
using Huaci.App.Views;

namespace Huaci.UiTests;

internal static class Program
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;

    [STAThread]
    private static int Main(string[] args)
    {
        bool skipOfflineModels = args.Any(
            argument => argument.Equals("--skip-offline-models", StringComparison.OrdinalIgnoreCase));
        string[] positionalArguments = args
            .Where(argument => !argument.StartsWith("--", StringComparison.Ordinal))
            .ToArray();
        var outputDirectory = Path.GetFullPath(
            positionalArguments.Length > 0
                ? positionalArguments[0]
                : Path.Combine(AppContext.BaseDirectory, "ui-artifacts"));
        var offlineAssetsPath = positionalArguments.Length > 1
            ? Path.GetFullPath(positionalArguments[1])
            : null;

        TranslationPopupWindow? popup = null;
        ScreenshotTranslationOverlayWindow? screenshotOverlay = null;
        BergamotOfflineTranslationProvider? offlineProvider = null;
        RapidOcrService? ocrService = null;
        System.Windows.Application? application = null;
        Window? testHostWindow = null;

        try
        {
            Directory.CreateDirectory(outputDirectory);
            application = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            testHostWindow = new Window
            {
                Width = 1,
                Height = 1,
                Left = SystemParameters.VirtualScreenLeft - 10_000,
                Top = SystemParameters.VirtualScreenTop - 10_000,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Opacity = 0
            };
            application.MainWindow = testHostWindow;
            testHostWindow.Show();

            TranslationResult offlineResult;
            TranslationResult reverseOfflineResult;
            if (skipOfflineModels)
            {
                offlineResult = new TranslationResult(
                    "offline fixture",
                    "跳过运行中的 WebView2 模型加载",
                    "en",
                    TranslationOrigin.Offline);
                reverseOfflineResult = new TranslationResult(
                    "今天天气很好。",
                    "The weather is lovely today.",
                    "zh",
                    TranslationOrigin.Offline);
                Console.WriteLine("SKIP  内置双向模型真实加载（--skip-offline-models）");
            }
            else
            {
                offlineProvider = new BergamotOfflineTranslationProvider(
                    application.Dispatcher,
                    offlineAssetsPath,
                    Path.Combine(
                        outputDirectory,
                        $"webview2-offline-{Environment.ProcessId}"));
                Ensure(offlineProvider.IsAvailable, offlineProvider.AvailabilityMessage);
                Ensure(
                    offlineProvider.Supports(new TranslationRequest("hello", "en", "zh-CN"))
                    && offlineProvider.Supports(new TranslationRequest("你好", "zh-CN", "en"))
                    && !offlineProvider.Supports(new TranslationRequest("bonjour", "fr", "zh-CN")),
                    "离线模型支持的语言方向不正确。");
                Console.WriteLine("RUN   内置模型真实离线英译中");
                offlineResult = WaitForTask(
                    () => offlineProvider.TranslateAsync(new TranslationRequest(
                        "The weather is beautiful today, and this translation runs entirely offline.",
                        "en",
                        "zh-CN")),
                    TimeSpan.FromSeconds(90));
                Ensure(
                    offlineResult.Origin == TranslationOrigin.Offline
                    && offlineResult.TranslatedText.Contains("天气", StringComparison.Ordinal)
                    && offlineResult.TranslatedText.Contains("离线", StringComparison.Ordinal),
                    $"内置英中模型未返回预期译文：{offlineResult.TranslatedText}");
                Console.WriteLine("RUN   内置模型真实离线中译英");
                reverseOfflineResult = WaitForTask(
                    () => offlineProvider.TranslateAsync(new TranslationRequest(
                        "今天天气很好。",
                        "zh-CN",
                        "en")),
                    TimeSpan.FromSeconds(90));
                Ensure(
                    reverseOfflineResult.Origin == TranslationOrigin.Offline
                    && reverseOfflineResult.DetectedSourceLanguage == "zh"
                    && reverseOfflineResult.TranslatedText.Any(char.IsAsciiLetter),
                    $"内置中英模型未返回预期译文：{reverseOfflineResult.TranslatedText}");
            }

            var ocrRoot = positionalArguments.Length > 2
                ? Path.GetFullPath(positionalArguments[2])
                : AppContext.BaseDirectory;
            var ocrModelPaths = new OcrModelPaths(
                Path.Combine(ocrRoot, "models", "v5", "ch_PP-OCRv5_mobile_det.onnx"),
                Path.Combine(ocrRoot, "models", "v5", "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
                Path.Combine(ocrRoot, "Ocr", "models", "v5", "ch_PP-OCRv5_rec_mobile.onnx"),
                Path.Combine(ocrRoot, "Ocr", "models", "v5", "ppocrv5_dict.txt"));
            ocrService = new RapidOcrService(ocrModelPaths);
            Ensure(ocrService.IsAvailable, ocrService.AvailabilityMessage);

            var ocrFixturePath = Path.Combine(outputDirectory, "ocr-fixture.png");
            var ocrFixture = CreateOcrFixturePng(ocrFixturePath);
            var ocrStarted = DateTime.UtcNow;
            Console.WriteLine("RUN   PP-OCRv5 真实离线中英文识别");
            var ocrResult = WaitForTask(
                () => ocrService.RecognizeAsync(ocrFixture),
                TimeSpan.FromSeconds(60));
            var ocrElapsed = DateTime.UtcNow - ocrStarted;
            Ensure(
                ocrResult.IsSuccess
                && ocrResult.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase)
                && ocrResult.Text.Contains("screenshot", StringComparison.OrdinalIgnoreCase)
                && ocrResult.Text.Contains("translation", StringComparison.OrdinalIgnoreCase),
                $"PP-OCRv5 did not recognize the English fixture: {ocrResult.Status} / {ocrResult.Text} / {ocrResult.Diagnostic}");
            Ensure(
                ocrResult.Text.Any(character => character is >= '\u3400' and <= '\u9fff'),
                $"PP-OCRv5 did not recognize the Chinese fixture: {ocrResult.Text}");

            popup = new TranslationPopupWindow();
            var firstAnchor = new Rect(180, 160, 1, 1);

            popup.ShowLoading(
                "正在识别截图文字…",
                firstAnchor,
                autoHideSeconds: 2,
                keepVisibleUntilUpdated: true,
                dismissMode: TranslationPopupDismissMode.ManualCloseOnly);
            FlushLayout(popup);
            VerifyWindowContract(popup);
            Render(popup, Path.Combine(outputDirectory, "toast-loading.png"));
            RaiseMouseEvent(popup, Mouse.MouseEnterEvent);
            RaiseMouseEvent(popup, Mouse.MouseLeaveEvent);
            Ensure(popup.IsVisible, "截图 OCR 加载 Toast 被 MouseLeave 提前关闭。");
            WaitForDispatcherDelay(TimeSpan.FromMilliseconds(2200));
            Ensure(popup.IsVisible, "截图 OCR 加载 Toast 被普通弹窗超时提前关闭。");

            popup.ShowResult(
                "Screenshot text",
                "截图翻译结果只能手动关闭。",
                firstAnchor,
                autoHideSeconds: 2,
                dismissMode: TranslationPopupDismissMode.ManualCloseOnly);
            FlushLayout(popup);
            var manualModePinButton = popup.FindName("PinButton") as ToggleButton
                ?? throw new InvalidOperationException("未找到 Toast 钉子按钮。");
            Ensure(manualModePinButton.Visibility == Visibility.Collapsed,
                "截图结果仍显示了无意义的固定按钮。");
            RaiseMouseEvent(popup, Mouse.MouseEnterEvent);
            RaiseMouseEvent(popup, Mouse.MouseLeaveEvent);
            WaitForDispatcherDelay(TimeSpan.FromMilliseconds(2200));
            Ensure(popup.IsVisible, "截图 OCR 结果被移开鼠标或超时自动关闭。");
            Render(popup, Path.Combine(outputDirectory, "toast-screenshot-manual-close.png"));
            popup.HidePopup();

            screenshotOverlay = new ScreenshotTranslationOverlayWindow();
            var screenshotBounds = new ScreenRect(180, 160, 480, 260);
            var overlayDismissedCount = 0;
            screenshotOverlay.Dismissed += () => overlayDismissedCount++;
            screenshotOverlay.ShowLoading(
                string.Empty,
                screenshotBounds,
                "正在识别…",
                ocrFixture);
            FlushLayout(screenshotOverlay);
            VerifyScreenshotOverlayContract(screenshotOverlay, screenshotBounds);
            var overlayViewToggle = screenshotOverlay.FindName("ViewToggleButton") as Button
                ?? throw new InvalidOperationException("未找到截图原文/译文切换按钮。");
            Ensure(
                !overlayViewToggle.IsEnabled || overlayViewToggle.Visibility != Visibility.Visible,
                "截图识别期间仍可切换原文/译文。");
            Render(screenshotOverlay, Path.Combine(outputDirectory, "screenshot-overlay-loading.png"));
            RaiseMouseEvent(screenshotOverlay, Mouse.MouseEnterEvent);
            RaiseMouseEvent(screenshotOverlay, Mouse.MouseLeaveEvent);
            WaitForDispatcherDelay(TimeSpan.FromMilliseconds(300));
            Ensure(screenshotOverlay.IsVisible, "截图选区覆盖层被 MouseLeave 或超时提前关闭。");

            const string screenshotSource =
                "Hello screenshot translation. The selected paragraph remains available as the original screenshot.";
            const string screenshotTranslation =
                "译文直接在截图选区内重新排版，使用覆盖整个选区的纯色阅读画布。" +
                "这一段特意保持足够长度，用来确认结果会根据选区宽度自动换行，" +
                "不会继续挤在旧版底部的小卡片中，同时仍然保留常驻选框和原图切换能力。";
            screenshotOverlay.ShowResult(
                screenshotSource,
                screenshotTranslation,
                screenshotBounds,
                TranslationOrigin.Offline,
                usedFallback: false,
                ocrResult.Blocks);
            FlushLayout(screenshotOverlay);
            VerifyScreenshotOverlayContract(screenshotOverlay, screenshotBounds);
            var replacementSurface = screenshotOverlay.FindName("ReplacementSurface") as Border
                ?? throw new InvalidOperationException("未找到截图译文替换画布。");
            var frozenScreenshot = screenshotOverlay.FindName("FrozenBackgroundImage") as Image
                ?? throw new InvalidOperationException("未找到截图冻结原图。");
            var overlayRoot = screenshotOverlay.FindName("RootGrid") as Grid
                ?? throw new InvalidOperationException("未找到截图覆盖层根容器。");
            var overlayResultScroller = screenshotOverlay.FindName("ResultScrollViewer") as ScrollViewer
                ?? throw new InvalidOperationException("未找到截图译文滚动区域。");
            var overlayResultText = screenshotOverlay.FindName("ResultTextBlock") as TextBlock
                ?? throw new InvalidOperationException("未找到截图译文文本。");
            Ensure(
                screenshotOverlay.CurrentOrigin == TranslationOrigin.Offline
                && screenshotOverlay.CurrentSource == screenshotSource
                && screenshotOverlay.CurrentResult == screenshotTranslation,
                "截图选区覆盖层没有更新离线翻译结果。");
            Ensure(
                overlayViewToggle.IsEnabled
                && overlayViewToggle.Visibility == Visibility.Visible
                && overlayViewToggle.Content as string == "原文"
                && replacementSurface.Visibility == Visibility.Visible
                && overlayResultScroller.Visibility == Visibility.Visible,
                "截图结果没有默认进入译文替换视图。");
            Ensure(
                replacementSurface.Background is SolidColorBrush { Color.A: 255 }
                && replacementSurface.ActualWidth >= screenshotOverlay.ActualWidth - 10
                && replacementSurface.ActualHeight >= screenshotOverlay.ActualHeight - 10
                && overlayRoot.Children.IndexOf(replacementSurface)
                    > overlayRoot.Children.IndexOf(frozenScreenshot),
                "译文纯色画布没有完整覆盖原截图选区。");
            Ensure(
                overlayResultText.Text == screenshotTranslation
                && overlayResultText.TextWrapping == TextWrapping.Wrap
                && ScrollViewer.GetHorizontalScrollBarVisibility(overlayResultScroller)
                    == ScrollBarVisibility.Disabled
                && overlayResultText.ActualHeight >= overlayResultText.LineHeight * 2,
                "截图译文没有在选区宽度内重新排版换行。");
            Render(
                screenshotOverlay,
                Path.Combine(outputDirectory, "screenshot-overlay-result-translation.png"));

            overlayViewToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FlushLayout(screenshotOverlay);
            Ensure(
                overlayViewToggle.Content as string == "译文"
                && replacementSurface.Visibility == Visibility.Collapsed
                && overlayResultScroller.Visibility == Visibility.Collapsed
                && frozenScreenshot.Visibility == Visibility.Visible
                && frozenScreenshot.Source is not null,
                "点击原文后没有恢复冻结的原截图。");
            Render(
                screenshotOverlay,
                Path.Combine(outputDirectory, "screenshot-overlay-result-original.png"));

            overlayViewToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FlushLayout(screenshotOverlay);
            Ensure(
                overlayViewToggle.Content as string == "原文"
                && replacementSurface.Visibility == Visibility.Visible
                && overlayResultScroller.Visibility == Visibility.Visible
                && overlayResultText.Text == screenshotTranslation,
                "从原文切回译文后没有恢复重排结果。");
            RaiseMouseEvent(screenshotOverlay, Mouse.MouseLeaveEvent);
            Ensure(screenshotOverlay.IsVisible, "截图翻译结果在鼠标移开后消失。");

            var overlayCloseButton = screenshotOverlay.FindName("CloseButton") as Button
                ?? throw new InvalidOperationException("未找到截图选区覆盖层关闭按钮。");
            overlayCloseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Ensure(!screenshotOverlay.IsVisible && overlayDismissedCount == 1,
                "截图选区覆盖层没有通过 X 手动关闭并发出取消信号。");
            Ensure(
                screenshotOverlay.PhysicalBounds is null
                && screenshotOverlay.CurrentSource.Length == 0
                && screenshotOverlay.CurrentResult.Length == 0
                && screenshotOverlay.CurrentOrigin is null
                && frozenScreenshot.Source is null
                && overlayResultText.Text.Length == 0
                && replacementSurface.Visibility == Visibility.Collapsed
                && overlayResultScroller.Visibility == Visibility.Collapsed
                && (!overlayViewToggle.IsEnabled
                    || overlayViewToggle.Visibility != Visibility.Visible),
                "截图选区覆盖层关闭后仍持有敏感截图或文字。");

            screenshotOverlay.ShowLoading(string.Empty, screenshotBounds, "正在识别…", ocrFixture);
            screenshotOverlay.ShowError("截图翻译", "未识别到可翻译的文字。", screenshotBounds);
            FlushLayout(screenshotOverlay);
            Ensure(screenshotOverlay.IsVisible, "截图 OCR 错误没有保留在原选区内。");
            Ensure(
                !overlayViewToggle.IsEnabled || overlayViewToggle.Visibility != Visibility.Visible,
                "截图 OCR 错误状态仍可切换原文/译文。");
            Render(screenshotOverlay, Path.Combine(outputDirectory, "screenshot-overlay-error.png"));
            overlayCloseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Ensure(
                !screenshotOverlay.IsVisible
                && overlayDismissedCount == 2
                && screenshotOverlay.PhysicalBounds is null
                && screenshotOverlay.CurrentSource.Length == 0
                && screenshotOverlay.CurrentResult.Length == 0
                && screenshotOverlay.CurrentOrigin is null
                && frozenScreenshot.Source is null
                && overlayResultText.Text.Length == 0,
                "截图 OCR 错误覆盖层无法手动关闭。");

            popup.ShowResult(
                "A lightweight selection translator",
                "一款轻量的划词翻译工具。",
                firstAnchor,
                autoHideSeconds: 60);
            FlushLayout(popup);
            Render(popup, Path.Combine(outputDirectory, "toast-result.png"));

            RaiseMouseEvent(popup, Mouse.MouseEnterEvent);
            RaiseMouseEvent(popup, Mouse.MouseLeaveEvent);
            Ensure(!popup.IsVisible, "Toast 收到 MouseLeave 后没有立即消失。");
            popup.ShowResult(
                "A lightweight selection translator",
                "一款轻量的划词翻译工具。",
                firstAnchor,
                autoHideSeconds: 60);
            var pinButton = popup.FindName("PinButton") as ToggleButton
                ?? throw new InvalidOperationException("未找到 Toast 钉子按钮。");
            pinButton.IsChecked = true;
            FlushLayout(popup);

            Ensure(popup.IsVisible, "固定前 Toast 已被真实鼠标位置干扰而关闭。");
            Ensure(GetWindowRect(new WindowInteropHelper(popup).Handle, out var beforePin), "无法读取固定前窗口位置。");
            Ensure(popup.IsPinned, "点击钉子后 Toast 未进入固定状态。");
            RaiseMouseEvent(popup, Mouse.MouseLeaveEvent);
            Ensure(popup.IsVisible, "固定后的 Toast 被 MouseLeave 关闭。");

            var longResult =
                "极简工具平时应当安静地待在后台，只在需要时出现；完成任务后自然消失，不打断用户正在进行的工作。" +
                "固定状态下，新划词会在同一个窗口中更新，而不会让浮窗在屏幕上跳动。" +
                "当译文较长时，内容仍然保留在这个轻量窗口中，并允许继续向下阅读。" +
                "这样既不会让普通短句变得臃肿，也不会因为追求小尺寸而丢失真正需要的信息。" +
                "翻译窗口保持有限高度，鼠标滚轮可以继续阅读后面的段落。" +
                "复制、固定和关闭仍然保留在同一位置，不会随着内容长度改变操作习惯。" +
                "最后一段用于确认滚动区域确实包含完整译文，而不是把超出窗口的文字直接裁掉。";
            popup.ShowResult(
                "Minimal tools should stay out of the way until they are needed, then disappear without interrupting the current task.",
                longResult,
                new Rect(960, 700, 1, 1),
                autoHideSeconds: 60);
            FlushLayout(popup);

            Ensure(GetWindowRect(new WindowInteropHelper(popup).Handle, out var afterPin), "无法读取固定后窗口位置。");
            Ensure(beforePin.Left == afterPin.Left && beforePin.Top == afterPin.Top, "固定后新内容改变了 Toast 位置。");
            Ensure(popup.ActualHeight <= popup.MaxHeight + 0.5, "长文本 Toast 超出最大高度。");
            var resultScroller = popup.FindName("ResultScrollViewer") as ScrollViewer
                ?? throw new InvalidOperationException("未找到译文滚动容器。");
            Ensure(resultScroller.ScrollableHeight > 0, "超长译文没有提供滚动阅读能力。");
            Render(popup, Path.Combine(outputDirectory, "toast-pinned-long.png"));

            pinButton.IsChecked = false;
            Ensure(!popup.IsPinned, "取消钉子后 Toast 仍处于固定状态。");
            popup.HidePopup();
            VerifyAndRenderDesktopWindows(outputDirectory);
            VerifyAlarmBannerAndCoordinator(outputDirectory);

            Console.WriteLine("PASS  Toast 真实 WPF 窗口契约");
            Console.WriteLine("PASS  固定后新内容原地更新");
            Console.WriteLine("PASS  加载、结果、固定长文本三态渲染");
            Console.WriteLine("PASS  截图 OCR 加载期间忽略移开和普通弹窗超时");
            Console.WriteLine("PASS  截图 OCR 结果仅允许手动关闭");
            Console.WriteLine("PASS  截图译文重排覆盖选区并可切换原图");
            Console.WriteLine("PASS  截图覆盖层加载、结果、错误三态仅手动关闭");
            Console.WriteLine("PASS  Toast 灰色主体无外层留白");
            Console.WriteLine("PASS  真实 MouseLeave 立即关闭且固定后保持");
            Console.WriteLine("PASS  五图标启动器按四列紧凑换行");
            Console.WriteLine("PASS  设置独立小窗可滚动且集中全部配置");
            Console.WriteLine("PASS  手动翻译独立窗口与核心交互");
            Console.WriteLine("PASS  快速笔记独立窗口与本地 save 历史壳体");
            Console.WriteLine("PASS  笔记闹铃到点后循环飞机横幅并仅手动关闭");
            Console.WriteLine("PASS  四个窗口紧凑尺寸与基础壳体契约");
            Console.WriteLine("PASS  最小化与 X 共用托盘隐藏逻辑并可重新打开");
            Console.WriteLine("PASS  外部划词状态同步不覆盖设置草稿");
            if (!skipOfflineModels)
            {
                Console.WriteLine($"PASS  内置模型真实离线英译中：{offlineResult.TranslatedText}");
                Console.WriteLine($"PASS  内置模型真实离线中译英：{reverseOfflineResult.TranslatedText}");
            }
            Console.WriteLine($"PASS  PP-OCRv5 真实离线中英文识别（{ocrElapsed.TotalMilliseconds:F0} ms）：{ocrResult.Text.Replace(Environment.NewLine, " / ")}");
            Console.WriteLine($"ARTIFACTS  {outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL  Toast UI 验证：{exception}");
            return 1;
        }
        finally
        {
            if (popup is not null)
            {
                popup.CloseForExit();
            }

            screenshotOverlay?.CloseForExit();

            offlineProvider?.Dispose();
            ocrService?.Dispose();
            testHostWindow?.Close();
            application?.Shutdown();
        }
    }

    private static void VerifyWindowContract(TranslationPopupWindow popup)
    {
        Ensure(popup.AllowsTransparency, "Toast 未启用透明窗口。");
        Ensure(!popup.ShowActivated && !popup.IsActive, "Toast 抢占了当前窗口焦点。");
        Ensure(!popup.ShowInTaskbar, "Toast 出现在任务栏中。");
        Ensure(popup.Topmost, "Toast 未保持在前端。");

        var root = popup.FindName("RootBorder") as Border
            ?? throw new InvalidOperationException("未找到 Toast 根容器。");
        Ensure(root.Background is SolidColorBrush { Color.A: > 0 and < 255 }, "Toast 根背景不是半透明色。");
        Ensure(root.Margin == default, "Toast 仍有外层透明留白。");
        Ensure(root.BorderThickness == default, "Toast 仍有外层描边。");
        Ensure(root.Effect is null, "Toast 仍有外层阴影。");
        Ensure(
            Math.Abs(root.ActualWidth - popup.ActualWidth) < 0.5
            && Math.Abs(root.ActualHeight - popup.ActualHeight) < 0.5,
            "Toast 灰色主体未铺满整个窗口。");

        var hwnd = new WindowInteropHelper(popup).Handle;
        var extendedStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        Ensure((extendedStyle & WsExNoActivate) != 0, "Toast 缺少 WS_EX_NOACTIVATE。");
        Ensure((extendedStyle & WsExToolWindow) != 0, "Toast 缺少 WS_EX_TOOLWINDOW。");
    }

    private static void VerifyScreenshotOverlayContract(
        ScreenshotTranslationOverlayWindow overlay,
        ScreenRect expectedBounds)
    {
        Ensure(overlay.AllowsTransparency, "截图选区覆盖层未启用透明窗口。");
        Ensure(!overlay.ShowActivated && !overlay.IsActive, "截图选区覆盖层抢占了当前窗口焦点。");
        Ensure(!overlay.ShowInTaskbar, "截图选区覆盖层出现在任务栏中。");
        Ensure(overlay.Topmost, "截图选区覆盖层未保持在前端。");
        Ensure(overlay.WindowStyle == WindowStyle.None, "截图选区覆盖层仍有系统白框。");

        var frozenBackground = overlay.FindName("FrozenBackgroundImage") as Image
            ?? throw new InvalidOperationException("未找到截图冻结背景。");
        Ensure(frozenBackground.Source is not null, "截图选区覆盖层未保留原截图背景。");
        var selectionBorder = overlay.FindName("SelectionBorder") as Border
            ?? throw new InvalidOperationException("未找到截图常驻选框。");
        Ensure(selectionBorder.BorderThickness.Left >= 2,
            "截图常驻选框不够清晰。");
        var contentCard = overlay.FindName("ContentCard") as Border
            ?? throw new InvalidOperationException("未找到选区内翻译结果卡。");
        Ensure(contentCard.Background is SolidColorBrush { Color.A: > 0 and < 255 },
            "选区内翻译结果卡不是半透明背景。");

        var hwnd = new WindowInteropHelper(overlay).Handle;
        Ensure(GetWindowRect(hwnd, out var windowRect), "无法读取截图选区覆盖层位置。");
        Ensure(
            windowRect.Left == (int)expectedBounds.X
            && windowRect.Top == (int)expectedBounds.Y
            && windowRect.Right - windowRect.Left == (int)expectedBounds.Width
            && windowRect.Bottom - windowRect.Top == (int)expectedBounds.Height,
            $"截图选区覆盖层未精确覆盖物理选区：{windowRect.Left},{windowRect.Top}," +
            $"{windowRect.Right - windowRect.Left}x{windowRect.Bottom - windowRect.Top}");

        var extendedStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        Ensure((extendedStyle & WsExNoActivate) != 0, "截图选区覆盖层缺少 WS_EX_NOACTIVATE。");
        Ensure((extendedStyle & WsExToolWindow) != 0, "截图选区覆盖层缺少 WS_EX_TOOLWINDOW。");
    }

    private static void VerifyAndRenderDesktopWindows(string outputDirectory)
    {
        var launcher = new LauncherWindow();
        var settingsWindow = new SettingsWindow();
        var manualWindow = new ManualTranslationWindow();
        var quickNotebookService = new QuickNotebookService(
            Path.Combine(outputDirectory, "save"));
        var quickNotebookWindow = new QuickNotebookWindow(quickNotebookService);
        var quickNotebookAlarmWindow = new QuickNotebookAlarmWindow(
            quickNotebookService,
            "下午三点检查离线翻译结果");

        try
        {
            var settings = new AppSettings
            {
                AutoCaptureEnabled = true,
                ClipboardFallbackEnabled = false,
                CaptureDelayMs = 60,
                PopupDurationSeconds = 5,
                MainWindowLeft = 120,
                MainWindowTop = 140
            };

            launcher.LoadSettings(settings, hasApiKey: false);
            launcher.SetStatus("自动划词已开启");
            launcher.Show();
            FlushLayout(launcher);
            VerifyLauncherContract(launcher);
            Render(launcher, Path.Combine(outputDirectory, "launcher.png"));

            var openManualCount = 0;
            var openScreenshotCount = 0;
            var openQuickNotebookCount = 0;
            var openSettingsCount = 0;
            bool? requestedCaptureState = null;
            launcher.OpenManualTranslationRequested += () => openManualCount++;
            launcher.OpenScreenshotTranslationRequested += () => openScreenshotCount++;
            launcher.OpenQuickNotebookRequested += () => openQuickNotebookCount++;
            launcher.OpenSettingsRequested += () => openSettingsCount++;
            launcher.AutoCaptureChanged += value => requestedCaptureState = value;

            var hideRequestedCount = 0;
            launcher.HideRequested += () => hideRequestedCount++;
            var minimizeButton = launcher.FindName("MinimizeButton") as Button
                ?? throw new InvalidOperationException("未找到最小化到托盘按钮。");
            var hideButton = launcher.FindName("HideButton") as Button
                ?? throw new InvalidOperationException("未找到隐藏到托盘按钮。");

            minimizeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Ensure(!launcher.IsVisible && hideRequestedCount == 1,
                "最小化按钮没有通过统一逻辑隐藏到托盘。");
            launcher.ShowAndActivate();
            FlushLayout(launcher);

            hideButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Ensure(!launcher.IsVisible && hideRequestedCount == 2,
                "X 按钮没有通过统一逻辑隐藏到托盘。");
            launcher.ShowAndActivate();
            FlushLayout(launcher);

            launcher.Topmost = false;
            launcher.KeepAboveWithoutActivation();
            FlushLayout(launcher);
            Ensure(
                launcher.IsVisible && launcher.Topmost,
                "Reasserting launcher z-order hid the window or failed to restore topmost.");

            var manualModule = launcher.FindName("ManualTranslationModuleButton") as Button
                ?? throw new InvalidOperationException("未找到手动翻译入口。");
            var settingsModule = launcher.FindName("SettingsModuleButton") as Button
                ?? throw new InvalidOperationException("未找到设置入口。");
            var screenshotModule = launcher.FindName("ScreenshotTranslationModuleButton") as Button
                ?? throw new InvalidOperationException("未找到截图翻译入口。");
            var quickNotebookModule = launcher.FindName("QuickNotebookModuleButton") as Button
                ?? throw new InvalidOperationException("未找到快速笔记入口。");
            var captureModule = launcher.FindName("AutoCaptureModuleButton") as ToggleButton
                ?? throw new InvalidOperationException("未找到自动划词入口。");
            manualModule.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var launcherStateBeforeScreenshot = (
                IsVisible: launcher.IsVisible,
                WindowState: launcher.WindowState,
                Width: launcher.Width,
                Height: launcher.Height,
                Left: launcher.Left,
                Top: launcher.Top,
                Topmost: launcher.Topmost);
            screenshotModule.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Ensure(
                launcher.IsVisible == launcherStateBeforeScreenshot.IsVisible
                && launcher.WindowState == launcherStateBeforeScreenshot.WindowState
                && Math.Abs(launcher.Width - launcherStateBeforeScreenshot.Width) < 0.5
                && Math.Abs(launcher.Height - launcherStateBeforeScreenshot.Height) < 0.5
                && Math.Abs(launcher.Left - launcherStateBeforeScreenshot.Left) < 0.5
                && Math.Abs(launcher.Top - launcherStateBeforeScreenshot.Top) < 0.5
                && launcher.Topmost == launcherStateBeforeScreenshot.Topmost,
                "点击截图入口后，启动器的可见性、尺寸、位置或窗口状态发生了变化。");
            quickNotebookModule.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            settingsModule.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            captureModule.IsChecked = false;
            captureModule.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Ensure(
                openManualCount == 1
                && openScreenshotCount == 1
                && openQuickNotebookCount == 1
                && openSettingsCount == 1,
                "启动器五个模块没有发出对应请求。");
            Ensure(
                !manualModule.IsKeyboardFocusWithin
                && !screenshotModule.IsKeyboardFocusWithin
                && !quickNotebookModule.IsKeyboardFocusWithin
                && !settingsModule.IsKeyboardFocusWithin
                && !captureModule.IsKeyboardFocusWithin,
                "鼠标点击模块后仍残留蓝色键盘焦点框。");
            Ensure(requestedCaptureState == false, "划词图标没有发出暂停请求。");
            launcher.SetAutoCaptureState(true);

            settingsWindow.LoadSettings(settings, hasApiKey: false);
            settingsWindow.Show();
            FlushLayout(settingsWindow);
            VerifySettingsWindowContract(settingsWindow);
            Render(settingsWindow, Path.Combine(outputDirectory, "settings.png"));

            var delay = settingsWindow.FindName("CaptureDelayTextBox") as TextBox
                ?? throw new InvalidOperationException("未找到响应时间设置。");
            var apiBaseUrl = settingsWindow.FindName("ApiBaseUrlTextBox") as TextBox
                ?? throw new InvalidOperationException("未找到 API 地址设置。");
            var model = settingsWindow.FindName("ModelTextBox") as TextBox
                ?? throw new InvalidOperationException("未找到模型设置。");
            var apiKey = settingsWindow.FindName("ApiKeyPasswordBox") as PasswordBox
                ?? throw new InvalidOperationException("未找到 API Key 设置。");
            var translationMode = settingsWindow.FindName("TranslationModeComboBox") as ComboBox
                ?? throw new InvalidOperationException("未找到翻译模式设置。");
            var startWithWindows = settingsWindow.FindName("StartWithWindowsCheckBox") as CheckBox
                ?? throw new InvalidOperationException("未找到开机自动启动设置。");
            var screenshotTranslation = settingsWindow.FindName("ScreenshotTranslationCheckBox") as CheckBox
                ?? throw new InvalidOperationException("未找到截图翻译设置。");

            delay.Text = "77";
            apiBaseUrl.Text = "https://draft.example/v1";
            model.Text = "draft-model";
            apiKey.Password = "draft-secret";
            translationMode.SelectedIndex = 1;
            screenshotTranslation.IsChecked = false;
            startWithWindows.IsChecked = true;
            settingsWindow.SetAutoCaptureState(enabled: false);

            Ensure(settingsWindow.FindName("AutoCaptureCheckBox") is null,
                "设置页仍保留了重复的自动划词开关。");
            Ensure((settingsWindow.FindName("CaptureStateTextBlock") as TextBlock)?.Text == "已暂停",
                "设置页没有以只读状态徽标同步主页划词开关。");
            Ensure(delay.Text == "77", "划词状态同步覆盖了响应时间草稿。");
            Ensure(apiBaseUrl.Text == "https://draft.example/v1", "划词状态同步覆盖了 API 地址草稿。");
            Ensure(model.Text == "draft-model", "划词状态同步覆盖了模型草稿。");
            Ensure(apiKey.Password == "draft-secret", "划词状态同步清空了未保存的 API Key。");

            SettingsWindowInput? savedInput = null;
            settingsWindow.SaveSettingsRequested += input => savedInput = input;
            var saveButton = settingsWindow.FindName("SaveSettingsButton") as Button
                ?? throw new InvalidOperationException("未找到保存设置按钮。");
            saveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var emittedInput = savedInput
                ?? throw new InvalidOperationException("保存设置没有发出完整配置。");
            Ensure(
                emittedInput.CaptureDelayMs == 77
                && emittedInput.ApiKey == "draft-secret"
                && emittedInput.TranslationMode == TranslationRouteMode.OfflineOnly
                && emittedInput.StartWithWindowsEnabled
                && !emittedInput.ScreenshotTranslationEnabled,
                "保存设置发出的配置不完整。");
            Ensure(apiKey.Password.Length == 0, "保存设置后 API Key 输入框未清空。");

            settingsWindow.ShowSection(SettingsSection.Service);
            FlushLayout(settingsWindow);
            Render(settingsWindow, Path.Combine(outputDirectory, "settings-service.png"));

            manualWindow.SetTranslation(
                "Minimal translation should stay focused on the selected text.",
                "极简划词翻译只专注于当前选中的内容。");
            manualWindow.Show();
            FlushLayout(manualWindow);
            VerifyManualWindowContract(manualWindow);

            string? translateRequest = null;
            manualWindow.TranslateRequested += text => translateRequest = text;
            var source = manualWindow.FindName("SourceTextBox") as TextBox
                ?? throw new InvalidOperationException("未找到手动翻译原文框。");
            var translateButton = manualWindow.FindName("TranslateButton") as Button
                ?? throw new InvalidOperationException("未找到手动翻译按钮。");
            source.Text = "  hello world  ";
            translateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Ensure(translateRequest == "hello world", "手动翻译没有提交去除首尾空格后的原文。");
            manualWindow.SetTranslation("hello world", "你好，世界。");
            FlushLayout(manualWindow);
            Render(manualWindow, Path.Combine(outputDirectory, "manual-translate-en-zh.png"));

            var directionButton = manualWindow.FindName("DirectionButton") as Button
                ?? throw new InvalidOperationException("未找到手动翻译方向按钮。");
            directionButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Ensure(
                manualWindow.Direction == TranslationDirection.SimplifiedChineseToEnglish
                && manualWindow.SourceLanguage == "zh-CN"
                && manualWindow.TargetLanguage == "en",
                "手动翻译没有切换为中译英。");
            Ensure(
                source.Text == "你好，世界。"
                && (manualWindow.FindName("ResultTextBox") as TextBox)?.Text == "hello world",
                "切换方向后没有交换已有原文和译文。");

            source.Text = "  今天天气很好。  ";
            translateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Ensure(translateRequest == "今天天气很好。", "中译英没有提交去除首尾空格后的原文。");
            TranslationRequest directionalRequest = manualWindow.CreateTranslationRequest(
                translateRequest ?? string.Empty);
            Ensure(
                directionalRequest.SourceLanguage == "zh-CN"
                && directionalRequest.TargetLanguage == "en",
                "手动翻译请求没有携带中译英方向。");
            manualWindow.SetTranslation("今天天气很好。", "The weather is lovely today.");
            FlushLayout(manualWindow);
            Render(manualWindow, Path.Combine(outputDirectory, "manual-translate-zh-en.png"));

            _ = WaitForTask(
                () => quickNotebookService.SaveTextAsync("统一历史列表中的普通文字笔记"),
                TimeSpan.FromSeconds(5));
            QuickNotebookAlarm historyAlarm = WaitForTask(
                () => quickNotebookService.ScheduleAlarmAsync(
                    "统一历史列表中的闹铃提醒",
                    DateTimeOffset.Now.AddHours(2)),
                TimeSpan.FromSeconds(5));
            quickNotebookWindow.Show();
            WaitForDispatcherDelay(TimeSpan.FromMilliseconds(150));
            FlushLayout(quickNotebookWindow);
            VerifyQuickNotebookWindowContract(quickNotebookWindow, historyAlarm);
            Render(quickNotebookWindow, Path.Combine(outputDirectory, "quick-notebook.png"));

            quickNotebookAlarmWindow.Owner = quickNotebookWindow;
            quickNotebookAlarmWindow.Show();
            FlushLayout(quickNotebookAlarmWindow);
            VerifyQuickNotebookAlarmWindowContract(quickNotebookAlarmWindow);
            Render(
                quickNotebookAlarmWindow,
                Path.Combine(outputDirectory, "quick-notebook-alarm.png"));
        }
        finally
        {
            quickNotebookAlarmWindow.Close();
            quickNotebookWindow.CloseForExit();
            manualWindow.CloseForExit();
            settingsWindow.CloseForExit();
            launcher.CloseForExit();
        }
    }

    private static void VerifyAlarmBannerAndCoordinator(string outputDirectory)
    {
        string alarmDirectory = Path.Combine(
            outputDirectory,
            $"alarm-test-{Guid.NewGuid():N}");
        var alarmService = new QuickNotebookService(alarmDirectory);
        var flybySound = new RecordingAlarmFlybySound();
        var banner = new AlarmBannerWindow(flybySound);
        var coordinator = new QuickNotebookAlarmCoordinator(
            alarmService,
            banner,
            Dispatcher.CurrentDispatcher);
        var dismissedCount = 0;
        banner.Dismissed += () => dismissedCount++;

        try
        {
            const string reminder =
                "这是一个用于验证超长闹铃文字自动省略的提醒横幅，飞机会在屏幕上方持续循环飞行，直到用户点击关闭按钮。";
            _ = WaitForTask(
                () => alarmService.ScheduleAlarmAsync(
                    reminder,
                    DateTimeOffset.Now.AddSeconds(1.2)),
                TimeSpan.FromSeconds(5));
            coordinator.Start();
            WaitForDispatcherDelay(TimeSpan.FromSeconds(2.5));
            FlushLayout(banner);

            Ensure(banner.IsReminderActive && banner.CurrentText == reminder,
                "笔记闹铃到点后没有显示飞机横幅。");
            Ensure(
                banner.AllowsTransparency
                && !banner.ShowActivated
                && !banner.ShowInTaskbar
                && banner.Topmost
                && banner.WindowStyle == WindowStyle.None,
                "飞机横幅窗口壳体契约不正确。");

            var message = banner.FindName("MessageTextBlock") as TextBlock
                ?? throw new InvalidOperationException("飞机横幅缺少提醒文字。");
            var flight = banner.FindName("FlightAssembly") as Grid
                ?? throw new InvalidOperationException("飞机横幅缺少飞行动画主体。");
            var translate = banner.FindName("FlightTranslate") as TranslateTransform
                ?? throw new InvalidOperationException("飞机横幅缺少循环位移动画。");
            var bannerCard = banner.FindName("BannerCard") as Border
                ?? throw new InvalidOperationException("Alarm banner is missing its trailing message card.");
            var planeVisual = banner.FindName("PlaneVisual") as Grid
                ?? throw new InvalidOperationException("Alarm banner is missing its vector aircraft.");
            var planeBob = banner.FindName("PlaneBobTranslate") as TranslateTransform
                ?? throw new InvalidOperationException("Alarm aircraft is missing vertical motion.");
            var planePitch = banner.FindName("PlanePitchRotate") as RotateTransform
                ?? throw new InvalidOperationException("Alarm aircraft is missing pitch motion.");
            var wingFlex = banner.FindName("WingFlexScale") as ScaleTransform
                ?? throw new InvalidOperationException("Alarm aircraft is missing wing motion.");
            var engineFan = banner.FindName("EngineFanRotate") as RotateTransform
                ?? throw new InvalidOperationException("Alarm aircraft is missing engine motion.");
            var navigationLight =
                banner.FindName("NavigationLight") as System.Windows.Shapes.Ellipse
                ?? throw new InvalidOperationException("Alarm aircraft is missing its navigation light.");
            var closeButton = banner.FindName("CloseReminderButton") as Button
                ?? throw new InvalidOperationException("飞机横幅缺少手动关闭按钮。");
            Ensure(
                message.Text == reminder
                && message.TextTrimming == TextTrimming.CharacterEllipsis
                && message.TextWrapping == TextWrapping.NoWrap
                && flight.ActualWidth > 300,
                "飞机横幅没有保留完整文案或长文字省略能力。");

            Point bannerPosition = bannerCard.TranslatePoint(default, flight);
            Point planePosition = planeVisual.TranslatePoint(default, flight);
            Ensure(
                planePosition.X >= bannerPosition.X + bannerCard.ActualWidth,
                "The aircraft must lead on the right with the reminder banner trailing behind it.");
            Ensure(
                CountVisualDescendants<System.Windows.Shapes.Path>(planeVisual) >= 5
                && CountVisualDescendants<Image>(planeVisual) == 0,
                "The alarm aircraft must be a layered vector drawing rather than a static image.");
            Ensure(
                flybySound.PlayCount == 1,
                "The fly-by sound did not play when the alarm first appeared.");

            Guid activeAlarmId = coordinator.ActiveAlarm?.Id
                ?? throw new InvalidOperationException("The alarm coordinator lost the active alarm.");
            banner.ShowReminder(activeAlarmId, reminder);
            WaitForDispatcherDelay(TimeSpan.FromMilliseconds(80));
            Ensure(
                flybySound.PlayCount == 1,
                "Restarting the same alarm animation replayed the fly-by sound.");

            var hwnd = new WindowInteropHelper(banner).Handle;
            long extendedStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            Ensure(
                (extendedStyle & WsExNoActivate) != 0
                && (extendedStyle & WsExToolWindow) != 0,
                "飞机横幅缺少 NOACTIVATE/TOOLWINDOW 样式。");

            double firstFlightPosition = translate.X;
            WaitForDispatcherDelay(TimeSpan.FromMilliseconds(450));
            double secondFlightPosition = translate.X;
            Ensure(
                translate.HasAnimatedProperties
                && Math.Abs(secondFlightPosition - firstFlightPosition) > 5
                && planeBob.HasAnimatedProperties
                && planePitch.HasAnimatedProperties
                && wingFlex.HasAnimatedProperties
                && engineFan.HasAnimatedProperties
                && navigationLight.HasAnimatedProperties,
                "飞机横幅的循环位移动画没有实际运行。");

            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = 24;
            FlushLayout(banner);
            Render(banner, Path.Combine(outputDirectory, "alarm-flight-banner.png"));

            closeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            coordinator.Dispose();
            Ensure(
                !banner.IsReminderActive
                && !banner.IsVisible
                && banner.CurrentText.Length == 0
                && dismissedCount == 1
                && flybySound.StopCount >= 1,
                "飞机横幅没有通过关闭按钮停止并清理提醒内容。");
            IReadOnlyList<QuickNotebookAlarm> remaining = WaitForTask(
                () => alarmService.GetPendingAlarmsAsync(),
                TimeSpan.FromSeconds(5));
            Ensure(
                remaining.Count == 0,
                "关闭横幅后立即退出时，已确认的闹铃仍留在本地待提醒列表。");
            banner.CloseForExit();
            Ensure(
                flybySound.DisposeCount == 1,
                "Closing the app did not release the alarm audio resources exactly once.");
        }
        finally
        {
            coordinator.Dispose();
            banner.CloseForExit();
            try
            {
                if (Directory.Exists(alarmDirectory))
                {
                    Directory.Delete(alarmDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void VerifyLauncherContract(LauncherWindow launcher)
    {
        var shell = launcher.FindName("WindowShell") as Border
            ?? throw new InvalidOperationException("未找到启动器外壳。");
        var headerDragArea = launcher.FindName("HeaderDragArea") as Border
            ?? throw new InvalidOperationException("未找到启动器拖动区域。");
        var moduleGrid = launcher.FindName("ModuleGrid") as UniformGrid
            ?? throw new InvalidOperationException("未找到启动器图标区。");
        var minimizeButton = launcher.FindName("MinimizeButton") as Button
            ?? throw new InvalidOperationException("未找到启动器最小化按钮。");
        var hideButton = launcher.FindName("HideButton") as Button
            ?? throw new InvalidOperationException("未找到启动器隐藏按钮。");
        var screenshotButton = launcher.FindName("ScreenshotTranslationModuleButton") as Button
            ?? throw new InvalidOperationException("未找到截图翻译模块。");
        var pinButton = launcher.FindName("PinButton") as ToggleButton
            ?? throw new InvalidOperationException("Launcher is missing its always-on-top indicator.");
        var captureIcon = launcher.FindName("AutoCaptureIcon") as TextBlock
            ?? throw new InvalidOperationException("未找到划词模块图标。");

        Ensure(launcher.AllowsTransparency, "启动器未启用透明窗口。");
        Ensure(launcher.Background is SolidColorBrush { Color.A: 0 }, "启动器外层背景不透明。");
        Ensure(shell.Background is SolidColorBrush { Color.A: >= 0xF0 and < 255 },
            "启动器背景透明度不符合灰黑半透明要求。");
        Ensure(shell.BorderThickness == default, "启动器仍有外层白色描边。");
        Ensure(launcher.ResizeMode == ResizeMode.NoResize, "启动器仍可调整尺寸。");
        Ensure(
            launcher.Topmost
            && pinButton.IsChecked == true
            && !pinButton.IsHitTestVisible
            && !pinButton.Focusable,
            "Launcher must stay topmost until the user explicitly minimizes or closes it.");
        Ensure(Math.Abs(launcher.ActualWidth - 256) < 0.5 && Math.Abs(launcher.ActualHeight - 162) < 0.5,
            "启动器不是预期的 256×162 四列紧凑尺寸。");
        Ensure(Math.Abs(launcher.Left - 120) < 0.5 && Math.Abs(launcher.Top - 140) < 0.5,
            "已保存的启动器坐标被强制改回屏幕中央。");
        VerifyDragSurface(headerDragArea, "启动器");
        Ensure(headerDragArea.ActualWidth >= 160, "加入最小化按钮后启动器拖动区域过小。");
        Ensure(Grid.GetColumn(minimizeButton) == 2 && Grid.GetColumn(hideButton) == 3,
            "启动器标题按钮顺序不是置顶、最小化、隐藏。");
        Ensure(minimizeButton.Content is TextBlock { Text: "\uE921" },
            "启动器最小化按钮没有使用标准 Windows 图标。");
        Ensure(moduleGrid.Rows == 0 && moduleGrid.Columns == 4 && moduleGrid.Children.Count == 5,
            "启动器没有按每行最多四个模块自动换行。");
        var firstModule = (FrameworkElement)moduleGrid.Children[0];
        var fourthModule = (FrameworkElement)moduleGrid.Children[3];
        var fifthModule = (FrameworkElement)moduleGrid.Children[4];
        Point firstPosition = firstModule.TranslatePoint(default, moduleGrid);
        Point fourthPosition = fourthModule.TranslatePoint(default, moduleGrid);
        Point fifthPosition = fifthModule.TranslatePoint(default, moduleGrid);
        Ensure(
            Math.Abs(firstPosition.Y - fourthPosition.Y) < 0.5
            && Math.Abs(firstPosition.X - fifthPosition.X) < 0.5
            && fifthPosition.Y > firstPosition.Y + 1,
            "第五个模块没有从第二行左侧与第一列对齐。");
        Ensure(
            moduleGrid.Children.OfType<Control>().All(module => !module.Focusable && module.FocusVisualStyle is null),
            "启动器模块仍会在鼠标点击后残留蓝色键盘焦点框。");
        Ensure(captureIcon.Text == "译" && captureIcon.FontFamily.Source.Contains("Microsoft YaHei", StringComparison.Ordinal),
            "划词模块没有使用清晰的中文‘译’字图标。");
        Ensure(
            screenshotButton.ToolTip as string == "按住 Ctrl+Alt+左键直接拖动，松开完成",
            "截图模块没有说明默认手势可直接拖动框选。");
        Ensure(launcher.FindName("MainTabs") is null, "启动器仍残留旧页签和详情面板。");
    }

    private static void VerifySettingsWindowContract(SettingsWindow settingsWindow)
    {
        var shell = settingsWindow.FindName("WindowShell") as Border
            ?? throw new InvalidOperationException("未找到设置窗口外壳。");
        var headerDragArea = settingsWindow.FindName("HeaderDragArea") as Border
            ?? throw new InvalidOperationException("未找到设置窗口拖动区域。");
        var scroller = settingsWindow.FindName("SettingsScrollViewer") as ScrollViewer
            ?? throw new InvalidOperationException("未找到设置滚动容器。");
        var saveButton = settingsWindow.FindName("SaveSettingsButton") as Button
            ?? throw new InvalidOperationException("未找到固定保存按钮。");
        var screenshotSection = settingsWindow.FindName("ScreenshotSettingsSection") as Border
            ?? throw new InvalidOperationException("未找到截图翻译设置分区。");
        var screenshotToggle = settingsWindow.FindName("ScreenshotTranslationCheckBox") as CheckBox
            ?? throw new InvalidOperationException("未找到截图翻译开关。");
        var captureSection = settingsWindow.FindName("CaptureSettingsSection") as Border
            ?? throw new InvalidOperationException("未找到划词行为设置分区。");
        var fallbackToggle = settingsWindow.FindName("ClipboardFallbackCheckBox") as CheckBox
            ?? throw new InvalidOperationException("未找到兼容模式设置。");
        var startupSection = settingsWindow.FindName("StartupSettingsSection") as Border
            ?? throw new InvalidOperationException("未找到启动与后台设置分区。");
        var startWithWindowsToggle = settingsWindow.FindName("StartWithWindowsCheckBox") as CheckBox
            ?? throw new InvalidOperationException("未找到开机自动启动设置。");

        Ensure(settingsWindow.AllowsTransparency && settingsWindow.ResizeMode == ResizeMode.NoResize,
            "设置窗口壳体契约不正确。");
        Ensure(shell.Background is SolidColorBrush { Color.A: >= 0xF0 and < 255 },
            "设置窗口背景穿透过强。");
        Ensure(shell.BorderThickness == default, "设置窗口仍有外层白色描边。");
        Ensure(Math.Abs(settingsWindow.ActualWidth - 316) < 0.5 && Math.Abs(settingsWindow.ActualHeight - 410) < 0.5,
            "设置窗口不是预期的 316×410 小尺寸。");
        VerifyDragSurface(headerDragArea, "设置窗口");
        Ensure(scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "设置窗口未启用自动垂直滚动。");
        Ensure(scroller.ScrollableHeight > 0, "全部设置没有形成可滚动内容。");
        Ensure(saveButton.IsVisible && saveButton.ActualHeight >= 27,
            "保存按钮没有固定在可视底栏。");
        Ensure(screenshotToggle.IsChecked == true, "截图翻译手势没有默认启用。");
        Ensure(settingsWindow.FindName("AutoCaptureCheckBox") is null,
            "划词行为设置仍包含主页已有的自动翻译开关。");
        Ensure(fallbackToggle.IsVisible && ContainsText(captureSection, "兼容模式"),
            "划词行为设置没有只保留兼容模式入口。");
        Ensure(startWithWindowsToggle.IsChecked == false && ContainsText(startupSection, "默认关闭"),
            "开机自动启动没有以默认关闭状态呈现。");
        Ensure(ContainsText(screenshotSection, "Ctrl + Alt + 左键"),
            "截图翻译设置没有显示默认快捷键文案。");
        Ensure(ContainsText(screenshotSection, "直接拖动，松开完成"),
            "截图翻译设置仍在提示松开后进行第二次拖动。");
    }

    private static void VerifyManualWindowContract(ManualTranslationWindow manualWindow)
    {
        var headerDragArea = manualWindow.FindName("HeaderDragArea") as Border
            ?? throw new InvalidOperationException("未找到手动翻译拖动区域。");
        var result = manualWindow.FindName("ResultTextBox") as TextBox
            ?? throw new InvalidOperationException("未找到手动翻译结果框。");
        var directionButton = manualWindow.FindName("DirectionButton") as Button
            ?? throw new InvalidOperationException("未找到手动翻译方向按钮。");

        Ensure(manualWindow.AllowsTransparency && manualWindow.ResizeMode == ResizeMode.NoResize,
            "手动翻译窗口壳体契约不正确。");
        Ensure(Math.Abs(manualWindow.ActualWidth - 336) < 0.5 && Math.Abs(manualWindow.ActualHeight - 376) < 0.5,
            "手动翻译窗口不是预期的 336×376 小尺寸。");
        Ensure(result.IsReadOnly, "手动翻译结果框不是只读状态。");
        Ensure(directionButton.IsEnabled, "手动翻译方向按钮不可用。");
        Ensure(
            manualWindow.SourceLanguage == "en" && manualWindow.TargetLanguage == "zh-CN",
            "手动翻译默认方向不是英译中。");
        VerifyDragSurface(headerDragArea, "手动翻译窗口");
    }

    private static void VerifyQuickNotebookWindowContract(
        QuickNotebookWindow notebookWindow,
        QuickNotebookAlarm expectedAlarm)
    {
        var shell = notebookWindow.Content as Border
            ?? throw new InvalidOperationException("快速笔记窗口缺少外壳。");
        var editor = notebookWindow.FindName("EditorTextBox") as TextBox
            ?? throw new InvalidOperationException("快速笔记窗口缺少文字编辑框。");
        var history = notebookWindow.FindName("HistoryListBox") as ListBox
            ?? throw new InvalidOperationException("快速笔记窗口缺少历史列表。");
        var pasteSaveButton = notebookWindow.FindName("PasteSaveButton") as Button
            ?? throw new InvalidOperationException("快速笔记窗口缺少粘贴保存入口。");
        var saveTextButton = notebookWindow.FindName("SaveTextButton") as Button
            ?? throw new InvalidOperationException("快速笔记窗口缺少保存文字入口。");
        var alarmButton = notebookWindow.FindName("AlarmButton") as Button
            ?? throw new InvalidOperationException("快速笔记窗口缺少闹铃入口。");
        var newNoteIcon = notebookWindow.FindName("NewNoteIconBorder") as Border
            ?? throw new InvalidOperationException("快速笔记窗口缺少新建笔记图标。");
        var status = notebookWindow.FindName("StatusTextBlock") as TextBlock
            ?? throw new InvalidOperationException("快速笔记窗口缺少本地保存状态栏。");
        var dropHint = notebookWindow.FindName("DropHintBorder") as Border
            ?? throw new InvalidOperationException("快速笔记窗口缺少图片拖放提示层。");
        var editorPlaceholder = notebookWindow.FindName("EditorPlaceholderTextBlock") as TextBlock
            ?? throw new InvalidOperationException("快速笔记窗口缺少编辑区空状态提示。");

        Ensure(
            notebookWindow.AllowsTransparency
            && notebookWindow.ResizeMode == ResizeMode.NoResize
            && !notebookWindow.ShowInTaskbar,
            "快速笔记窗口壳体契约不正确。");
        Ensure(
            Math.Abs(notebookWindow.ActualWidth - 326) < 0.5
            && Math.Abs(notebookWindow.ActualHeight - 410) < 0.5,
            "快速笔记窗口不是预期的 326×410 小尺寸。");
        Ensure(shell.Background is SolidColorBrush { Color.A: >= 0xF0 and < 255 },
            "快速笔记窗口背景不符合灰黑半透明要求。");
        Ensure(shell.BorderThickness == default,
            "快速笔记窗口仍有多余的外层描边。");
        Ensure(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(notebookWindow.StorageDirectory))
                .Equals("save", StringComparison.OrdinalIgnoreCase),
            "快速笔记窗口没有指向本地 save 文件夹。");
        Ensure(
            editor.AcceptsReturn
            && pasteSaveButton.IsVisible
            && saveTextButton.IsVisible
            && alarmButton.IsVisible
            && alarmButton.Content as string == "设置闹铃"
            && Grid.GetColumn(alarmButton) == 1
            && Grid.GetColumn(newNoteIcon) == 0,
            "快速笔记文字、剪贴板或闹铃入口不完整。");
        const string alarmDraft = "Clear this editor after scheduling the reminder";
        editor.Text = alarmDraft;
        Exception? alarmAutomationError = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(
            new Action(() =>
            {
                try
                {
                    QuickNotebookAlarmWindow alarmDialog =
                        System.Windows.Application.Current.Windows
                            .OfType<QuickNotebookAlarmWindow>()
                            .FirstOrDefault(window =>
                                window.IsVisible
                                && ReferenceEquals(window.Owner, notebookWindow))
                        ?? throw new InvalidOperationException(
                            "The note alarm dialog did not open.");
                    var scheduleAlarmButton =
                        alarmDialog.FindName("ScheduleAlarmButton") as Button
                        ?? throw new InvalidOperationException(
                            "The note alarm dialog is missing its schedule button.");
                    scheduleAlarmButton.RaiseEvent(
                        new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                catch (Exception exception)
                {
                    alarmAutomationError = exception;
                    foreach (QuickNotebookAlarmWindow openDialog in
                             System.Windows.Application.Current.Windows
                                 .OfType<QuickNotebookAlarmWindow>()
                                 .Where(window =>
                                     window.IsVisible
                                     && ReferenceEquals(window.Owner, notebookWindow))
                                 .ToArray())
                    {
                        openDialog.Close();
                    }
                }
            }),
            DispatcherPriority.ApplicationIdle);
        alarmButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (alarmAutomationError is not null)
        {
            throw alarmAutomationError;
        }

        Ensure(
            editor.Text.Length == 0,
            "Scheduling an alarm from the note editor did not clear the submitted text.");
        WaitForCondition(
            () => history.Items
                .Cast<QuickNotebookHistoryItem>()
                .Any(item => item.Alarm?.Message == alarmDraft),
            TimeSpan.FromSeconds(5),
            "The scheduled note alarm did not appear in the shared history.");

        QuickNotebookHistoryItem[] historyItems =
            history.Items.Cast<QuickNotebookHistoryItem>().ToArray();
        Ensure(
            historyItems.Any(item => item.Entry is not null)
            && historyItems.Any(item =>
                item.Alarm?.Id == expectedAlarm.Id
                && item.AlarmBadgeVisibility == Visibility.Visible
                && item.AlarmToolTip.Contains(
                    expectedAlarm.DueAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    StringComparison.Ordinal)),
            "笔记与闹铃没有共用历史列表，或闹铃标识无法查看具体时间。");
        Ensure(
            notebookWindow.AllowDrop
            && dropHint.Visibility == Visibility.Collapsed
            && editorPlaceholder.Text.Contains("粘贴", StringComparison.Ordinal),
            "快速笔记缺少 Ctrl+V 或拖入图片的交互提示。");
        Ensure(
            ScrollViewer.GetVerticalScrollBarVisibility(history) == ScrollBarVisibility.Auto,
            "快速笔记历史列表没有自动垂直滚动能力。");
        Ensure(
            status.ToolTip?.ToString()?.Contains("save", StringComparison.OrdinalIgnoreCase) == true,
            "快速笔记状态栏没有指向本地 save 保存位置。");
    }

    private static void VerifyQuickNotebookAlarmWindowContract(
        QuickNotebookAlarmWindow alarmWindow)
    {
        var message = alarmWindow.FindName("AlarmMessageTextBox") as TextBox
            ?? throw new InvalidOperationException("闹铃窗口缺少可编辑提醒内容。");
        var date = alarmWindow.FindName("AlarmDatePicker") as DatePicker
            ?? throw new InvalidOperationException("闹铃窗口缺少日期选择。");
        var hour = alarmWindow.FindName("AlarmHourComboBox") as ComboBox
            ?? throw new InvalidOperationException("闹铃窗口缺少小时选择。");
        var minute = alarmWindow.FindName("AlarmMinuteComboBox") as ComboBox
            ?? throw new InvalidOperationException("闹铃窗口缺少分钟选择。");
        var scheduleButton = alarmWindow.FindName("ScheduleAlarmButton") as Button
            ?? throw new InvalidOperationException("闹铃窗口缺少保存按钮。");
        var status = alarmWindow.FindName("AlarmStatusTextBlock") as TextBlock
            ?? throw new InvalidOperationException("闹铃窗口缺少本地保存状态。");

        Ensure(
            alarmWindow.AllowsTransparency
            && alarmWindow.ResizeMode == ResizeMode.NoResize
            && !alarmWindow.ShowInTaskbar,
            "闹铃设置窗口壳体契约不正确。");
        Ensure(
            Math.Abs(alarmWindow.ActualWidth - 306) < 0.5
            && Math.Abs(alarmWindow.ActualHeight - 272) < 0.5,
            "闹铃设置窗口不是预期的 306×272 紧凑尺寸。");
        Ensure(
            message.AcceptsReturn
            && message.Text == "下午三点检查离线翻译结果"
            && date.SelectedDate is not null
            && hour.SelectedItem is int
            && minute.SelectedItem is int,
            "闹铃窗口没有预填可编辑提醒文案或默认时间。");
        Ensure(scheduleButton.IsVisible, "闹铃保存入口不可用。");
        Ensure(
            alarmWindow.FindName("AlarmListBox") is null
            && !ContainsText(alarmWindow, "待提醒"),
            "闹铃设置窗口仍残留单独的待提醒面板。");
        Ensure(
            Path.GetFileName(alarmWindow.AlarmStoragePath)
                .Equals("alarms.json", StringComparison.OrdinalIgnoreCase)
            && status.Text.Contains("alarms.json", StringComparison.OrdinalIgnoreCase),
            "闹铃窗口没有指向本地 save\\alarms.json。");
    }

    private static void VerifyDragSurface(Border headerDragArea, string windowName)
    {
        Ensure(headerDragArea.Background is SolidColorBrush { Color.A: 0 },
            $"{windowName}标题空白区没有透明命中面。");
        Ensure(headerDragArea.ActualWidth > 150 && headerDragArea.ActualHeight >= 37,
            $"{windowName}可拖动区域过小。");
        Ensure(headerDragArea.InputHitTest(new Point(headerDragArea.ActualWidth - 4, headerDragArea.ActualHeight / 2)) is not null,
            $"{windowName}标题空白位置无法接收鼠标输入。");
    }

    private static bool ContainsText(DependencyObject root, string expectedText)
    {
        if (root is TextBlock textBlock
            && textBlock.Text.Contains(expectedText, StringComparison.Ordinal))
        {
            return true;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            if (ContainsText(VisualTreeHelper.GetChild(root, index), expectedText))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = root is T ? 1 : 0;
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            count += CountVisualDescendants<T>(
                VisualTreeHelper.GetChild(root, index));
        }

        return count;
    }

    private static void VerifyAndRenderMainWindow(string outputDirectory)
    {
        var mainWindow = new MainWindow();
        try
        {
            var settings = new AppSettings
            {
                AutoCaptureEnabled = true,
                CaptureDelayMs = 60,
                PopupDurationSeconds = 5,
                MainWindowLeft = 120,
                MainWindowTop = 140
            };
            mainWindow.LoadSettings(settings, hasApiKey: false);
            mainWindow.SetTranslation(
                "Minimal translation should stay focused on the selected text.",
                "极简划词翻译只专注于当前选中的内容。");
            mainWindow.SetStatus("自动划词已开启");
            mainWindow.Show();
            FlushLayout(mainWindow);
            VerifyMainWindowContract(mainWindow);

            var mainTabs = mainWindow.FindName("MainTabs") as TabControl
                ?? throw new InvalidOperationException("未找到管理窗口页签。");
            var homeTab = mainWindow.FindName("HomeTab") as TabItem
                ?? throw new InvalidOperationException("未找到划词模块。");
            var translateTab = mainWindow.FindName("TranslateTab") as TabItem
                ?? throw new InvalidOperationException("未找到翻译模块。");
            var captureTab = mainWindow.FindName("CaptureTab") as TabItem
                ?? throw new InvalidOperationException("未找到取词模块。");
            var serviceTab = mainWindow.FindName("ServiceTab") as TabItem
                ?? throw new InvalidOperationException("未找到服务模块。");
            Ensure(mainTabs.Items.Count == 4 && ReferenceEquals(mainTabs.SelectedItem, homeTab),
                "管理窗口没有默认显示四模块划词首页。");
            Render(mainWindow, Path.Combine(outputDirectory, "main-home.png"));

            mainTabs.SelectedItem = translateTab;
            FlushLayout(mainWindow);
            Render(mainWindow, Path.Combine(outputDirectory, "main-translate.png"));

            mainWindow.ShowSettingsSection();
            Ensure(ReferenceEquals(mainTabs.SelectedItem, captureTab), "设置入口没有打开取词模块。");
            var delay = mainWindow.FindName("CaptureDelayTextBox") as TextBox
                ?? throw new InvalidOperationException("未找到取词响应设置。");
            var duration = mainWindow.FindName("PopupDurationTextBox") as TextBox
                ?? throw new InvalidOperationException("未找到弹窗停留设置。");
            var fallback = mainWindow.FindName("ClipboardFallbackCheckBox") as CheckBox
                ?? throw new InvalidOperationException("未找到兼容取词设置。");
            delay.Text = "77";
            duration.Text = "8";
            fallback.IsChecked = true;
            FlushLayout(mainWindow);
            Render(mainWindow, Path.Combine(outputDirectory, "main-capture.png"));

            mainWindow.ShowServiceSettingsSection();
            Ensure(ReferenceEquals(mainTabs.SelectedItem, serviceTab), "服务入口没有打开翻译服务模块。");
            var apiBaseUrl = mainWindow.FindName("ApiBaseUrlTextBox") as TextBox
                ?? throw new InvalidOperationException("未找到 API 地址设置。");
            var model = mainWindow.FindName("ModelTextBox") as TextBox
                ?? throw new InvalidOperationException("未找到模型设置。");
            var apiKey = mainWindow.FindName("ApiKeyPasswordBox") as PasswordBox
                ?? throw new InvalidOperationException("未找到 API Key 设置。");
            var autoCapture = mainWindow.FindName("AutoCaptureCheckBox") as CheckBox
                ?? throw new InvalidOperationException("未找到自动划词设置。");

            apiBaseUrl.Text = "https://draft.example/v1";
            model.Text = "draft-model";
            apiKey.Password = "draft-secret";
            mainWindow.SetAutoCaptureState(enabled: false);

            Ensure(autoCapture.IsChecked == false, "主窗自动划词状态没有同步。");
            Ensure(delay.Text == "77", "托盘状态同步覆盖了取词响应草稿。");
            Ensure(apiBaseUrl.Text == "https://draft.example/v1", "托盘状态同步覆盖了 API 地址草稿。");
            Ensure(model.Text == "draft-model", "托盘状态同步覆盖了模型草稿。");
            Ensure(apiKey.Password == "draft-secret", "托盘状态同步清空了未保存的 API Key。");
            FlushLayout(mainWindow);
            VerifyMainWindowContract(mainWindow);
            Render(mainWindow, Path.Combine(outputDirectory, "main-service.png"));
        }
        finally
        {
            mainWindow.CloseForExit();
        }
    }

    private static void VerifyMainWindowContract(MainWindow mainWindow)
    {
        var shell = mainWindow.FindName("WindowShell") as Border
            ?? throw new InvalidOperationException("未找到管理窗口外壳。");
        var headerDragArea = mainWindow.FindName("HeaderDragArea") as Border
            ?? throw new InvalidOperationException("未找到管理窗口拖动区域。");
        var mainTabs = mainWindow.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("未找到模块导航。");
        Ensure(mainWindow.AllowsTransparency, "管理窗口未启用透明背景。");
        Ensure(mainWindow.Background is SolidColorBrush { Color.A: 0 }, "管理窗口外层背景不透明。");
        Ensure(shell.Background is SolidColorBrush { Color.A: >= 0xF0 and < 255 },
            "管理窗口背景穿透过强，后方文字会干扰阅读。");
        Ensure(mainWindow.ResizeMode == ResizeMode.NoResize, "管理窗口仍可被拉回旧的大尺寸。");
        Ensure(Math.Abs(mainWindow.ActualWidth - 344) < 0.5 && Math.Abs(mainWindow.ActualHeight - 438) < 0.5,
            "管理窗口不是预期的 344×438 紧凑尺寸。");
        Ensure(Math.Abs(mainWindow.Left - 120) < 0.5 && Math.Abs(mainWindow.Top - 140) < 0.5,
            "已保存的窗口坐标被强制改回屏幕中央。");
        Ensure(headerDragArea.Background is SolidColorBrush { Color.A: 0 },
            "标题栏空白区域没有透明命中面，窗口会表现为无法拖动。");
        Ensure(headerDragArea.ActualWidth > 100 && headerDragArea.ActualHeight >= 39,
            "标题栏可拖动区域过小。");
        Ensure(headerDragArea.InputHitTest(new Point(headerDragArea.ActualWidth - 4, headerDragArea.ActualHeight / 2)) is not null,
            "标题栏空白位置无法接收鼠标输入。");
        Ensure(mainTabs.Items.Count == 4, "管理窗口没有形成四模块导航。");
    }

    private static void FlushLayout(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        window.UpdateLayout();
    }

    private static void RaiseMouseEvent(Window window, RoutedEvent routedEvent)
    {
        window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = routedEvent
        });
    }

    private static void WaitForDispatcherDelay(TimeSpan delay)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = delay
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void WaitForCondition(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(failureMessage);
            }

            WaitForDispatcherDelay(TimeSpan.FromMilliseconds(40));
        }
    }

    private static void Render(Window window, string outputPath)
    {
        var root = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("Toast 缺少可渲染内容。");
        InvalidateVisualTree(root);
        WaitForCompositionFrames();
        root.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(root);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static byte[] CreateOcrFixturePng(string outputPath)
    {
        const int width = 920;
        const int height = 210;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var typeface = new Typeface(
                new FontFamily("Microsoft YaHei UI"),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal);
            var english = new FormattedText(
                "Hello screenshot translation",
                CultureInfo.GetCultureInfo("en-US"),
                FlowDirection.LeftToRight,
                typeface,
                48,
                Brushes.Black,
                1.0);
            var chinese = new FormattedText(
                "截图翻译 中英互译",
                CultureInfo.GetCultureInfo("zh-CN"),
                FlowDirection.LeftToRight,
                typeface,
                46,
                Brushes.Black,
                1.0);
            drawing.DrawText(english, new Point(30, 24));
            drawing.DrawText(chinese, new Point(30, 112));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var bytes = stream.ToArray();
        File.WriteAllBytes(outputPath, bytes);
        return bytes;
    }

    private static void InvalidateVisualTree(DependencyObject visual)
    {
        if (visual is UIElement element)
        {
            element.InvalidateVisual();
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
        {
            InvalidateVisualTree(VisualTreeHelper.GetChild(visual, index));
        }
    }

    private static void WaitForCompositionFrames(int frameCount = 2)
    {
        var frame = new DispatcherFrame();
        var remainingFrames = frameCount;
        var timeout = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        EventHandler? renderingHandler = null;
        renderingHandler = (_, _) =>
        {
            if (--remainingFrames > 0)
            {
                return;
            }

            CompositionTarget.Rendering -= renderingHandler;
            timeout.Stop();
            frame.Continue = false;
        };

        timeout.Tick += (_, _) =>
        {
            CompositionTarget.Rendering -= renderingHandler;
            timeout.Stop();
            frame.Continue = false;
        };

        CompositionTarget.Rendering += renderingHandler;
        timeout.Start();
        Dispatcher.PushFrame(frame);
    }

    private static T WaitForTask<T>(Func<Task<T>> operation, TimeSpan timeout)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = timeout
        };
        Task<T>? task = null;
        Exception? startException = null;
        var timedOut = false;

        timer.Tick += (_, _) =>
        {
            timedOut = true;
            timer.Stop();
            frame.Continue = false;
        };

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                task = operation();
                _ = task.ContinueWith(
                    _ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception exception)
            {
                startException = exception;
                frame.Continue = false;
            }
        }), DispatcherPriority.ApplicationIdle);

        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();

        if (startException is not null)
        {
            throw startException;
        }

        if (task is null || (timedOut && !task.IsCompleted))
        {
            throw new TimeoutException($"等待异步验证超过 {timeout.TotalSeconds:0} 秒。");
        }

        return task.GetAwaiter().GetResult();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingAlarmFlybySound : IAlarmFlybySound
    {
        public int PlayCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void Play() => PlayCount++;

        public void Stop() => StopCount++;

        public void Dispose() => DisposeCount++;
    }

    private static nint GetWindowLongPtr(nint hwnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hwnd, index)
        : new nint(GetWindowLong32(hwnd, index));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
