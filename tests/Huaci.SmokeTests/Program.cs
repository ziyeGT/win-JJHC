using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Huaci.App.Models;
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

var defaults = new AppSettings();
Check("默认即时取词参数", defaults.CaptureDelayMs == 60 && defaults.PopupDurationSeconds == 5);
Check("默认离线优先", defaults.TranslationMode == TranslationRouteMode.OfflineFirst);
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
        CaptureDelayMs = 1,
        PopupDurationSeconds = 999,
        ApiBaseUrl = "https://example.test/v1/",
        Model = " model-x "
    });

    var loaded = settingsService.Load();
    Check("设置往返保存", !loaded.AutoCaptureEnabled && loaded.ClipboardFallbackEnabled);
    Check("设置范围归一化", loaded.CaptureDelayMs == 50 && loaded.PopupDurationSeconds == 60);
    Check("接口与模型归一化", loaded.ApiBaseUrl == "https://example.test/v1" && loaded.Model == "model-x");
    Check("设置文件不含密钥字段", !File.ReadAllText(settingsPath).Contains("apiKey", StringComparison.OrdinalIgnoreCase));
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
    // Request details are asserted inside StubHandler; reaching this point confirms them.
    Check("兼容接口请求结构", true);
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
        if (!body.Contains("model-x", StringComparison.Ordinal)
            || !body.Contains("hello", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("翻译请求正文不完整。");
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}",
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
