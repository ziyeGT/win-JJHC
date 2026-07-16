using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Huaci.App.Models;
using Huaci.App.Services;
using Huaci.App.Services.Capture;
using Huaci.App.Services.Notebook;
using Huaci.App.Services.Ocr;
using Huaci.App.Services.Settings;
using Huaci.App.Services.Translation;
using Huaci.App.Views;

var failures = new List<string>();

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        Console.WriteLine($"PASS  {name}");
        return;
    }

    failures.Add(detail is null ? name : $"{name}: {detail}");
    Console.WriteLine($"FAIL  {name}{(detail is null ? string.Empty : $" — {detail}")}");
}

Check("英文文本可翻译", TextHeuristics.IsLikelyTranslatable("Hello, world!"));
Check("纯中文自动跳过", !TextHeuristics.IsLikelyTranslatable("你好，世界！"));
Check("纯标点自动跳过", !TextHeuristics.IsLikelyTranslatable("...?!"));
Check("空白规范化", TextHeuristics.Normalize("  hello\u00a0  world  ") == "hello world");

var structuredOcrText = OcrTextLayout.ReconstructParagraphs(
[
    new OcrTextBlock("First line of a paragraph", 0.99, new OcrRectangle(10, 10, 220, 20)),
    new OcrTextBlock("continues on the next line.", 0.98, new OcrRectangle(10, 36, 230, 20)),
    new OcrTextBlock("A new paragraph starts here.", 0.97, new OcrRectangle(10, 82, 240, 20))
]);
Check(
    "OCR 行坐标重建段落",
    structuredOcrText == "First line of a paragraph continues on the next line.\n\nA new paragraph starts here.");

var defaults = new AppSettings();
Check("默认启动自动划词", defaults.AutoCaptureEnabled);
Check("默认即时取词参数", defaults.CaptureDelayMs == 60 && defaults.PopupDurationSeconds == 5);
Check("默认离线优先", defaults.TranslationMode == TranslationRouteMode.OfflineFirst);
Check("默认开启截图翻译", defaults.ScreenshotTranslationEnabled);
Check("默认关闭开机自动启动", !defaults.StartWithWindowsEnabled);
Check(
    "快速召唤主窗口快捷键为 Ctrl+F1",
    GlobalHotKeyService.ToggleMainWindowKey == Key.F1
    && GlobalHotKeyService.ToggleMainWindowModifiers == ModifierKeys.Control
    && GlobalHotKeyService.ToggleMainWindowDisplayText == "Ctrl+F1");
Check("默认启用 Ctrl+Alt+左键直接拖动截图手势", new GlobalMouseHookOptions().ScreenshotGestureEnabled);
Check(
    "截图框选过滤点击抖动",
    new ScreenRegionCaptureOptions() is { MinimumWidth: >= 16, MinimumHeight: >= 10 });
Check(
    "截图松手后保留可见选框",
    new ScreenRegionCaptureOptions().SelectionConfirmationDelay >= TimeSpan.FromMilliseconds(120));
Check(
    "开机启动参数大小写不敏感",
    WindowsStartupService.IsStartupLaunch(["--STARTUP"])
    && !WindowsStartupService.IsStartupLaunch(["--other"]));
Check(
    "开机启动命令正确引用空格路径",
    WindowsStartupService.BuildRunCommand(@"C:\Program Files\Huaci\Huaci.exe")
        == "\"C:\\Program Files\\Huaci\\Huaci.exe\" --startup");
Check(
    "旧取词延迟自动迁移",
    SettingsService.ValidateAndNormalize(new AppSettings { CaptureDelayMs = 250 }).CaptureDelayMs == 100);

var toastStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
var toastLifetime = TimeSpan.FromSeconds(5);

var initialOutsidePolicy = new ToastDismissPolicy();
initialOutsidePolicy.BeginPresentation(toastStart, pointerIsOver: false);
Check(
    "Toast 初次出现不因鼠标在窗外秒退",
    !initialOutsidePolicy.ShouldDismiss(
        toastStart.AddMilliseconds(300),
        pointerIsOver: false,
        cursorDistance: 500,
        toastLifetime));

var leavePolicy = new ToastDismissPolicy();
leavePolicy.BeginPresentation(toastStart, pointerIsOver: false);
_ = leavePolicy.ShouldDismiss(toastStart, pointerIsOver: false, cursorDistance: 10, toastLifetime);
Check(
    "Toast 移开立即消失",
    leavePolicy.ShouldDismiss(
        toastStart.AddMilliseconds(1),
        pointerIsOver: false,
        cursorDistance: 100,
        toastLifetime));

var edgePolicy = new ToastDismissPolicy();
edgePolicy.BeginPresentation(toastStart, pointerIsOver: true);
edgePolicy.PointerLeft(toastStart.AddMilliseconds(100));
Check(
    "Toast MouseLeave 立即消失",
    edgePolicy.ShouldDismiss(
        toastStart.AddMilliseconds(100),
        pointerIsOver: false,
        cursorDistance: null,
        toastLifetime));

var pinPolicy = new ToastDismissPolicy();
pinPolicy.BeginPresentation(toastStart, pointerIsOver: true);
pinPolicy.SetPinned(true, toastStart, pointerIsOver: true);
Check(
    "Toast 固定后忽略移开和超时",
    !pinPolicy.ShouldDismiss(
        toastStart.AddSeconds(30),
        pointerIsOver: false,
        cursorDistance: 500,
        fallbackLifetime: TimeSpan.FromSeconds(1)));
pinPolicy.SetPinned(false, toastStart.AddSeconds(30), pointerIsOver: true);
Check(
    "Toast 取消固定后等待下一次移开",
    !pinPolicy.ShouldDismiss(
        toastStart.AddSeconds(31),
        pointerIsOver: true,
        cursorDistance: 0,
        fallbackLifetime: TimeSpan.FromSeconds(1)));
pinPolicy.PointerLeft(toastStart.AddSeconds(31));
Check(
    "Toast 取消固定后可正常消失",
    pinPolicy.ShouldDismiss(
        toastStart.AddSeconds(31),
        pointerIsOver: false,
        cursorDistance: null,
        fallbackLifetime: toastLifetime));

Check(
    "基础地址补全接口路径",
    OpenAiCompatibleTranslationProvider.ResolveEndpoint("https://api.deepseek.com").AbsoluteUri
        == "https://api.deepseek.com/chat/completions");
Check(
    "完整接口路径保持不变",
    OpenAiCompatibleTranslationProvider.ResolveEndpoint("https://example.test/v1/chat/completions").AbsoluteUri
        == "https://example.test/v1/chat/completions");

var rejectedInvalidEndpoint = false;
try
{
    _ = OpenAiCompatibleTranslationProvider.ResolveEndpoint("file:///tmp/api");
}
catch (TranslationProviderException)
{
    rejectedInvalidEndpoint = true;
}
Check("拒绝非 HTTP 接口", rejectedInvalidEndpoint);

var temporaryRoot = Path.Combine(Path.GetTempPath(), $"Huaci-Smoke-{Guid.NewGuid():N}");
var settingsPath = Path.Combine(temporaryRoot, "settings.json");
try
{
    using var settingsService = new SettingsService(settingsPath);
    settingsService.Save(new AppSettings
    {
        AutoCaptureEnabled = false,
        ClipboardFallbackEnabled = true,
        ScreenshotTranslationEnabled = false,
        StartWithWindowsEnabled = true,
        CaptureDelayMs = 1,
        PopupDurationSeconds = 999,
        ApiBaseUrl = "https://example.test/v1/",
        Model = " model-x "
    });

    var loaded = settingsService.Load();
    Check(
        "设置往返保存",
        !loaded.AutoCaptureEnabled
        && loaded.ClipboardFallbackEnabled
        && !loaded.ScreenshotTranslationEnabled
        && loaded.StartWithWindowsEnabled);
    Check("设置范围归一化", loaded.CaptureDelayMs == 50 && loaded.PopupDurationSeconds == 60);
    Check("接口与模型归一化", loaded.ApiBaseUrl == "https://example.test/v1" && loaded.Model == "model-x");
    Check("设置文件不含密钥字段", !File.ReadAllText(settingsPath).Contains("apiKey", StringComparison.OrdinalIgnoreCase));

    var notebookDirectory = Path.Combine(temporaryRoot, "save");
    var notebookService = new QuickNotebookService(notebookDirectory);
    Check(
        "快速笔记使用本地 save 文件夹",
        Path.GetFileName(Path.TrimEndingDirectorySeparator(notebookService.StorageDirectory))
            .Equals("save", StringComparison.OrdinalIgnoreCase));

    const string notebookText = "Huaci 快速笔记：本地文字记录。";
    QuickNotebookEntry textEntry = await notebookService.SaveTextAsync(notebookText);
    Check(
        "快速笔记保存并读取文字",
        textEntry.IsText
        && File.Exists(textEntry.FilePath)
        && Path.GetExtension(textEntry.FilePath).Equals(".txt", StringComparison.OrdinalIgnoreCase)
        && await notebookService.ReadTextAsync(textEntry) == notebookText);

    byte[] pixels =
    [
        0x20, 0x70, 0xE0, 0xFF,
        0x50, 0xC0, 0x60, 0xFF,
        0xE0, 0x80, 0x30, 0xFF,
        0xF0, 0xF0, 0xF0, 0xFF,
    ];
    BitmapSource notebookImage = BitmapSource.Create(
        2,
        2,
        96,
        96,
        PixelFormats.Bgra32,
        palette: null,
        pixels,
        stride: 8);
    notebookImage.Freeze();

    QuickNotebookEntry imageEntry = await notebookService.SaveImageAsync(notebookImage);
    byte[] savedImage = await notebookService.ReadImageAsync(imageEntry);
    Check(
        "快速笔记保存并读取 PNG 图片",
        imageEntry.IsImage
        && File.Exists(imageEntry.FilePath)
        && Path.GetExtension(imageEntry.FilePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
        && savedImage.Length > 8
        && savedImage.AsSpan(0, 8).SequenceEqual(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));

    var namedPngData = new System.Windows.DataObject();
    namedPngData.SetData("image/png", savedImage);
    QuickNotebookClipboardContent? namedPngContent = QuickNotebookClipboardReader.Read(namedPngData);
    Check(
        "快速笔记识别命名 PNG 剪贴板格式",
        namedPngContent?.Image is { PixelWidth: 2, PixelHeight: 2 });

    var bitmapData = new System.Windows.DataObject();
    bitmapData.SetData(System.Windows.DataFormats.Bitmap, notebookImage);
    QuickNotebookClipboardContent? bitmapContent = QuickNotebookClipboardReader.Read(bitmapData);
    Check(
        "快速笔记识别 Bitmap 剪贴板回退",
        bitmapContent?.Image is { PixelWidth: 2, PixelHeight: 2 });

    var fileDropData = new System.Windows.DataObject();
    fileDropData.SetData(
        System.Windows.DataFormats.FileDrop,
        new[] { imageEntry.FilePath });
    QuickNotebookClipboardContent? fileDropContent = QuickNotebookClipboardReader.Read(fileDropData);
    Check(
        "快速笔记识别资源管理器图片 FileDrop",
        fileDropContent?.Image is { PixelWidth: 2, PixelHeight: 2 }
        && fileDropContent.SourceLabel == "图片文件");

    var textClipboardData = new System.Windows.DataObject();
    textClipboardData.SetData(System.Windows.DataFormats.UnicodeText, notebookText);
    QuickNotebookClipboardContent? textClipboardContent =
        QuickNotebookClipboardReader.Read(textClipboardData);
    Check(
        "快速笔记保留普通文字剪贴板内容",
        textClipboardContent?.Text == notebookText && textClipboardContent.Image is null);

    IReadOnlyList<QuickNotebookEntry> notebookHistory = await notebookService.GetHistoryAsync();
    Check(
        "快速笔记历史包含文字和图片",
        notebookHistory.Count == 2
        && notebookHistory.Any(entry => entry.FilePath == textEntry.FilePath && entry.IsText)
        && notebookHistory.Any(entry => entry.FilePath == imageEntry.FilePath && entry.IsImage));

    var alarmChangeCount = 0;
    notebookService.AlarmsChanged += (_, _) => alarmChangeCount++;
    DateTimeOffset alarmDueAt = DateTimeOffset.Now.AddMinutes(20);
    QuickNotebookAlarm alarm = await notebookService.ScheduleAlarmAsync(
        "十分钟后检查翻译结果",
        alarmDueAt);
    Check(
        "快速笔记闹铃保存到本地 alarms.json",
        File.Exists(notebookService.AlarmStoragePath)
        && Path.GetFileName(notebookService.AlarmStoragePath)
            .Equals("alarms.json", StringComparison.OrdinalIgnoreCase)
        && alarmChangeCount == 1);

    var reloadedNotebookService = new QuickNotebookService(notebookDirectory);
    IReadOnlyList<QuickNotebookAlarm> pendingAlarms =
        await reloadedNotebookService.GetPendingAlarmsAsync();
    Check(
        "快速笔记闹铃可持久化读取",
        pendingAlarms.Count == 1
        && pendingAlarms[0].Id == alarm.Id
        && pendingAlarms[0].Message == "十分钟后检查翻译结果"
        && Math.Abs((pendingAlarms[0].DueAt - alarmDueAt).TotalSeconds) < 1);
    Check(
        "闹铃 JSON 不混入笔记历史",
        (await notebookService.GetHistoryAsync()).Count == 2);
    Check(
        "快速笔记闹铃可取消",
        await notebookService.DeleteAlarmAsync(alarm.Id)
        && alarmChangeCount == 2
        && (await notebookService.GetPendingAlarmsAsync()).Count == 0);

    await notebookService.DeleteAsync(textEntry);
    await notebookService.DeleteAsync(imageEntry);
    Check(
        "快速笔记删除同步更新本地历史",
        !File.Exists(textEntry.FilePath)
        && !File.Exists(imageEntry.FilePath)
        && (await notebookService.GetHistoryAsync()).Count == 0);
}
finally
{
    if (Directory.Exists(temporaryRoot))
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
}

var offlineOnlyProvider = new FakeOfflineProvider();
var offlineOnlyOnlineProvider = new FakeOnlineProvider();
var offlineOnlyKeyReads = 0;
var offlineOnlyService = new TranslationService(
    offlineOnlyProvider,
    offlineOnlyOnlineProvider,
    () =>
    {
        offlineOnlyKeyReads += 1;
        throw new InvalidOperationException("仅离线模式不应读取密钥。");
    });
var offlineOnlyResult = await offlineOnlyService.TranslateAsync(
    new TranslationRequest("hello"),
    new AppSettings { TranslationMode = TranslationRouteMode.OfflineOnly });
Check(
    "仅离线不读取密钥且不调用网络",
    offlineOnlyResult.Origin == TranslationOrigin.Offline
    && offlineOnlyProvider.CallCount == 1
    && offlineOnlyOnlineProvider.CallCount == 0
    && offlineOnlyKeyReads == 0);

var offlineFirstProvider = new FakeOfflineProvider();
var offlineFirstKeyReads = 0;
var offlineFirstService = new TranslationService(
    offlineFirstProvider,
    new FakeOnlineProvider(),
    () =>
    {
        offlineFirstKeyReads += 1;
        return "unused";
    });
var offlineFirstResult = await offlineFirstService.TranslateAsync(
    new TranslationRequest("hello"),
    new AppSettings { TranslationMode = TranslationRouteMode.OfflineFirst });
Check(
    "离线优先命中本地时不读取密钥",
    offlineFirstResult.Origin == TranslationOrigin.Offline && offlineFirstKeyReads == 0);

var fallbackOfflineProvider = new FakeOfflineProvider
{
    Failure = new TranslationProviderException(
        TranslationErrorKind.ProviderUnavailable,
        "模拟离线引擎不可用")
};
var fallbackOnlineProvider = new FakeOnlineProvider();
var fallbackKeyReads = 0;
var fallbackService = new TranslationService(
    fallbackOfflineProvider,
    fallbackOnlineProvider,
    () =>
    {
        fallbackKeyReads += 1;
        return "test-secret";
    });
var fallbackResult = await fallbackService.TranslateAsync(
    new TranslationRequest("hello"),
    new AppSettings { TranslationMode = TranslationRouteMode.OfflineFirst });
Check(
    "离线失败后可在线回退",
    fallbackResult.Origin == TranslationOrigin.Online
    && fallbackResult.UsedFallback
    && fallbackOnlineProvider.CallCount == 1
    && fallbackKeyReads == 1);

var onlineOnlyOfflineProvider = new FakeOfflineProvider();
var onlineOnlyService = new TranslationService(
    onlineOnlyOfflineProvider,
    new FakeOnlineProvider(),
    () => null);
var onlineOnlyRejectedMissingKey = false;
try
{
    _ = await onlineOnlyService.TranslateAsync(
        new TranslationRequest("hello"),
        new AppSettings { TranslationMode = TranslationRouteMode.OnlineOnly });
}
catch (TranslationProviderException exception)
{
    onlineOnlyRejectedMissingKey = exception.Kind == TranslationErrorKind.Configuration;
}

Check(
    "仅在线缺少密钥且不调用离线",
    onlineOnlyRejectedMissingKey && onlineOnlyOfflineProvider.CallCount == 0);

using (var httpClient = new HttpClient(new StubHandler()))
using (var provider = new OpenAiCompatibleTranslationProvider(httpClient))
{
    var result = await provider.TranslateAsync(
        new TranslationRequest("hello"),
        new TranslationProviderOptions("https://example.test/v1", "model-x", "test-secret"));

    Check("兼容接口解析译文", result.TranslatedText == "你好");
    var reverseResult = await provider.TranslateAsync(
        new TranslationRequest("你好", "zh-CN", "en"),
        new TranslationProviderOptions("https://example.test/v1", "model-x", "test-secret"));

    Check("兼容接口解析中译英", reverseResult.TranslatedText == "Hello");
    // Request details and both language directions are asserted inside StubHandler.
    Check("兼容接口双向请求结构", true);
}

if (failures.Count == 0)
{
    Console.WriteLine("\n全部烟测通过。");
    return 0;
}

Console.Error.WriteLine($"\n{failures.Count} 项烟测失败：");
foreach (var failure in failures)
{
    Console.Error.WriteLine($"- {failure}");
}
return 1;

internal sealed class StubHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsoluteUri != "https://example.test/v1/chat/completions")
        {
            throw new InvalidOperationException($"请求路径不正确：{request.RequestUri}");
        }

        if (request.Headers.Authorization?.Scheme != "Bearer"
            || request.Headers.Authorization.Parameter != "test-secret")
        {
            throw new InvalidOperationException("Authorization 请求头不正确。");
        }

        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string model = root.GetProperty("model").GetString() ?? string.Empty;
        string prompt = root.GetProperty("messages")[1].GetProperty("content").GetString() ?? string.Empty;
        if (model != "model-x")
        {
            throw new InvalidOperationException("翻译请求模型不正确。");
        }

        string translatedText;
        if (prompt.Contains("hello", StringComparison.Ordinal))
        {
            if (!prompt.Contains("目标语言：zh-CN（简体中文）", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("英译中请求没有携带简体中文目标语言。");
            }

            translatedText = "你好";
        }
        else if (prompt.Contains("你好", StringComparison.Ordinal))
        {
            if (!prompt.Contains("源语言：zh-CN（简体中文）", StringComparison.Ordinal)
                || !prompt.Contains("目标语言：en（英语）", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("中译英请求没有携带正确语言方向。");
            }

            translatedText = "Hello";
        }
        else
        {
            throw new InvalidOperationException("翻译请求正文不完整。");
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new { message = new { content = translatedText } }
                    }
                }),
                Encoding.UTF8,
                "application/json")
        };
    }
}

internal sealed class FakeOfflineProvider : IOfflineTranslationProvider
{
    public bool IsAvailable { get; init; } = true;

    public string AvailabilityMessage => IsAvailable ? "ready" : "offline unavailable";

    public int CallCount { get; private set; }

    public Exception? Failure { get; init; }

    public bool Supports(TranslationRequest request) => true;

    public Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        CallCount += 1;
        if (Failure is not null)
        {
            return Task.FromException<TranslationResult>(Failure);
        }

        return Task.FromResult(new TranslationResult(
            request.Text,
            "你好",
            "en",
            TranslationOrigin.Offline));
    }
}

internal sealed class FakeOnlineProvider : ITranslationProvider
{
    public int CallCount { get; private set; }

    public Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        TranslationProviderOptions options,
        CancellationToken cancellationToken = default)
    {
        CallCount += 1;
        return Task.FromResult(new TranslationResult(
            request.Text,
            "你好（在线）",
            "en",
            TranslationOrigin.Online));
    }
}
