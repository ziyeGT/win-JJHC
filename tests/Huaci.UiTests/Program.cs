using System.IO;
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
        var outputDirectory = Path.GetFullPath(
            args.Length > 0
                ? args[0]
                : Path.Combine(AppContext.BaseDirectory, "ui-artifacts"));
        var offlineAssetsPath = args.Length > 1
            ? Path.GetFullPath(args[1])
            : null;

        TranslationPopupWindow? popup = null;
        BergamotOfflineTranslationProvider? offlineProvider = null;
        System.Windows.Application? application = null;

        try
        {
            Directory.CreateDirectory(outputDirectory);
            var huaciApplication = new Huaci.App.App();
            huaciApplication.InitializeComponent();
            huaciApplication.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            application = huaciApplication;

            offlineProvider = new BergamotOfflineTranslationProvider(
                application.Dispatcher,
                offlineAssetsPath);
            Ensure(offlineProvider.IsAvailable, offlineProvider.AvailabilityMessage);
            var offlineResult = WaitForTask(
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

            popup = new TranslationPopupWindow();
            var firstAnchor = new Rect(180, 160, 1, 1);

            popup.ShowLoading("A lightweight selection translator", firstAnchor, autoHideSeconds: 60);
            FlushLayout(popup);
            VerifyWindowContract(popup);
            Render(popup, Path.Combine(outputDirectory, "toast-loading.png"));

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

            Console.WriteLine("PASS  Toast 真实 WPF 窗口契约");
            Console.WriteLine("PASS  固定后新内容原地更新");
            Console.WriteLine("PASS  加载、结果、固定长文本三态渲染");
            Console.WriteLine("PASS  Toast 灰色主体无外层留白");
            Console.WriteLine("PASS  真实 MouseLeave 立即关闭且固定后保持");
            Console.WriteLine("PASS  三图标极简启动器渲染");
            Console.WriteLine("PASS  设置独立小窗可滚动且集中全部配置");
            Console.WriteLine("PASS  手动翻译独立窗口与核心交互");
            Console.WriteLine("PASS  三个窗口标题栏均可命中拖动");
            Console.WriteLine("PASS  最小化与 X 共用托盘隐藏逻辑并可重新打开");
            Console.WriteLine("PASS  外部划词状态同步不覆盖设置草稿");
            Console.WriteLine($"PASS  内置模型真实离线英译中：{offlineResult.TranslatedText}");
            Console.WriteLine($"ARTIFACTS  {outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL  Toast UI 验证：{exception.Message}");
            return 1;
        }
        finally
        {
            if (popup is not null)
            {
                popup.CloseForExit();
            }

            offlineProvider?.Dispose();
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

    private static void VerifyAndRenderDesktopWindows(string outputDirectory)
    {
        var launcher = new LauncherWindow();
        var settingsWindow = new SettingsWindow();
        var manualWindow = new ManualTranslationWindow();

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
            var openSettingsCount = 0;
            bool? requestedCaptureState = null;
            launcher.OpenManualTranslationRequested += () => openManualCount++;
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

            var manualModule = launcher.FindName("ManualTranslationModuleButton") as Button
                ?? throw new InvalidOperationException("未找到手动翻译入口。");
            var settingsModule = launcher.FindName("SettingsModuleButton") as Button
                ?? throw new InvalidOperationException("未找到设置入口。");
            var captureModule = launcher.FindName("AutoCaptureModuleButton") as ToggleButton
                ?? throw new InvalidOperationException("未找到自动划词入口。");
            manualModule.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            settingsModule.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            captureModule.IsChecked = false;
            captureModule.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Ensure(openManualCount == 1 && openSettingsCount == 1, "启动器图标没有发出对应窗口请求。");
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
            var autoCapture = settingsWindow.FindName("AutoCaptureCheckBox") as CheckBox
                ?? throw new InvalidOperationException("未找到自动划词设置。");

            delay.Text = "77";
            apiBaseUrl.Text = "https://draft.example/v1";
            model.Text = "draft-model";
            apiKey.Password = "draft-secret";
            translationMode.SelectedIndex = 1;
            settingsWindow.SetAutoCaptureState(enabled: false);

            Ensure(autoCapture.IsChecked == false, "设置窗口没有同步划词状态。");
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
                && emittedInput.TranslationMode == TranslationRouteMode.OfflineOnly,
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
            Render(manualWindow, Path.Combine(outputDirectory, "manual-translate.png"));
        }
        finally
        {
            manualWindow.CloseForExit();
            settingsWindow.CloseForExit();
            launcher.CloseForExit();
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

        Ensure(launcher.AllowsTransparency, "启动器未启用透明窗口。");
        Ensure(launcher.Background is SolidColorBrush { Color.A: 0 }, "启动器外层背景不透明。");
        Ensure(shell.Background is SolidColorBrush { Color.A: >= 0xF0 and < 255 },
            "启动器背景透明度不符合灰黑半透明要求。");
        Ensure(shell.BorderThickness == default, "启动器仍有外层白色描边。");
        Ensure(launcher.ResizeMode == ResizeMode.NoResize, "启动器仍可调整尺寸。");
        Ensure(Math.Abs(launcher.ActualWidth - 292) < 0.5 && Math.Abs(launcher.ActualHeight - 156) < 0.5,
            "启动器不是预期的 292×156 小尺寸。");
        Ensure(Math.Abs(launcher.Left - 120) < 0.5 && Math.Abs(launcher.Top - 140) < 0.5,
            "已保存的启动器坐标被强制改回屏幕中央。");
        VerifyDragSurface(headerDragArea, "启动器");
        Ensure(headerDragArea.ActualWidth >= 190, "加入最小化按钮后启动器拖动区域过小。");
        Ensure(Grid.GetColumn(minimizeButton) == 2 && Grid.GetColumn(hideButton) == 3,
            "启动器标题按钮顺序不是置顶、最小化、隐藏。");
        Ensure(minimizeButton.Content is TextBlock { Text: "\uE921" },
            "启动器最小化按钮没有使用标准 Windows 图标。");
        Ensure(moduleGrid.Children.Count == 3, "启动器不是划词、翻译、设置三个图标。");
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

        Ensure(settingsWindow.AllowsTransparency && settingsWindow.ResizeMode == ResizeMode.NoResize,
            "设置窗口壳体契约不正确。");
        Ensure(shell.Background is SolidColorBrush { Color.A: >= 0xF0 and < 255 },
            "设置窗口背景穿透过强。");
        Ensure(shell.BorderThickness == default, "设置窗口仍有外层白色描边。");
        Ensure(Math.Abs(settingsWindow.ActualWidth - 336) < 0.5 && Math.Abs(settingsWindow.ActualHeight - 438) < 0.5,
            "设置窗口不是预期的小尺寸。");
        VerifyDragSurface(headerDragArea, "设置窗口");
        Ensure(scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "设置窗口未启用自动垂直滚动。");
        Ensure(scroller.ScrollableHeight > 0, "全部设置没有形成可滚动内容。");
        Ensure(saveButton.IsVisible && saveButton.ActualHeight >= 29,
            "保存按钮没有固定在可视底栏。");
    }

    private static void VerifyManualWindowContract(ManualTranslationWindow manualWindow)
    {
        var headerDragArea = manualWindow.FindName("HeaderDragArea") as Border
            ?? throw new InvalidOperationException("未找到手动翻译拖动区域。");
        var result = manualWindow.FindName("ResultTextBox") as TextBox
            ?? throw new InvalidOperationException("未找到手动翻译结果框。");

        Ensure(manualWindow.AllowsTransparency && manualWindow.ResizeMode == ResizeMode.NoResize,
            "手动翻译窗口壳体契约不正确。");
        Ensure(Math.Abs(manualWindow.ActualWidth - 360) < 0.5 && Math.Abs(manualWindow.ActualHeight - 402) < 0.5,
            "手动翻译窗口尺寸不正确。");
        Ensure(result.IsReadOnly, "手动翻译结果框不是只读状态。");
        VerifyDragSurface(headerDragArea, "手动翻译窗口");
    }

    private static void VerifyDragSurface(Border headerDragArea, string windowName)
    {
        Ensure(headerDragArea.Background is SolidColorBrush { Color.A: 0 },
            $"{windowName}标题空白区没有透明命中面。");
        Ensure(headerDragArea.ActualWidth > 150 && headerDragArea.ActualHeight >= 39,
            $"{windowName}可拖动区域过小。");
        Ensure(headerDragArea.InputHitTest(new Point(headerDragArea.ActualWidth - 4, headerDragArea.ActualHeight / 2)) is not null,
            $"{windowName}标题空白位置无法接收鼠标输入。");
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
