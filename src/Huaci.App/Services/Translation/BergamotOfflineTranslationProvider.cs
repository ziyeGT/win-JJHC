using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows.Threading;
using Huaci.App.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Huaci.App.Services.Translation;

public sealed class BergamotOfflineTranslationProvider : IOfflineTranslationProvider, IDisposable
{
    private const string EngineHost = "huaci-engine.example";
    private const string EngineOrigin = "https://huaci-engine.example";
    private const int MaximumTextLength = 8_000;

    private static readonly string[] RequiredRelativePaths =
    [
        "index.html",
        "bridge.js",
        "engine/translator.js",
        "engine/worker/translator-worker.js",
        "engine/worker/bergamot-translator-worker.js",
        "engine/worker/bergamot-translator-worker.wasm",
        "models/index.json",
        "models/en-zh/lex.50.50.enzh.s2t.bin",
        "models/en-zh/model.enzh.intgemm.alphas.bin",
        "models/en-zh/srcvocab.enzh.spm",
        "models/en-zh/trgvocab.enzh.spm",
        "models/zh-en/lex.50.50.zhen.s2t.bin",
        "models/zh-en/model.zhen.intgemm.alphas.bin",
        "models/zh-en/vocab.zhen.spm"
    ];

    private readonly Dispatcher _dispatcher;
    private readonly string _assetsPath;
    private readonly string _userDataFolder;
    private readonly bool _assetsAvailable;
    private readonly bool _runtimeAvailable;
    private readonly object _initializationGate = new();
    private readonly object _warmUpGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();

    private Task? _initializationTask;
    private Task? _warmUpTask;
    private TaskCompletionSource<bool>? _readySignal;
    private CoreWebView2Environment? _environment;
    private OfflineTranslationHostWindow? _hostWindow;
    private WebView2? _webView;
    private bool _disposed;

    public BergamotOfflineTranslationProvider(
        Dispatcher dispatcher,
        string? assetsPath = null,
        string? userDataFolder = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _assetsPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(assetsPath)
                ? Path.Combine(AppContext.BaseDirectory, "Offline")
                : assetsPath);
        _userDataFolder = Path.GetFullPath(
            string.IsNullOrWhiteSpace(userDataFolder)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Huaci",
                    "WebView2",
                    "Offline")
                : userDataFolder);

        _assetsAvailable = _assetsPath.Length < 240
            && RequiredRelativePaths.All(path => File.Exists(Path.Combine(_assetsPath, path)));
        _runtimeAvailable = DetectWebView2Runtime();
    }

    public bool IsAvailable => _assetsAvailable && _runtimeAvailable && !_disposed;

    public string AvailabilityMessage => !_assetsAvailable
        ? "内置离线模型文件不完整，请重新解压完整的 Huaci ZIP。"
        : !_runtimeAvailable
            ? "本地模型已内置，但系统缺少 Microsoft Edge WebView2 Runtime，请安装后重启 Huaci。"
            : _disposed
                ? "内置离线翻译引擎已关闭，请重启 Huaci。"
                : "内置英语↔简体中文模型已就绪";

    public bool Supports(TranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return TryResolveLanguagePair(request, out _, out _)
            && request.Text.Length is > 0 and <= MaximumTextLength;
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Task warmUp;
        lock (_warmUpGate)
        {
            _warmUpTask ??= WarmUpCoreAsync();
            warmUp = _warmUpTask;
        }

        return warmUp.WaitAsync(cancellationToken);
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        string sourceText = request.Text.Trim();
        if (sourceText.Length == 0)
        {
            throw new TranslationProviderException(
                TranslationErrorKind.Configuration,
                "没有可翻译的文本。");
        }

        if (!IsAvailable)
        {
            throw new TranslationProviderException(
                TranslationErrorKind.Configuration,
                AvailabilityMessage);
        }

        if (!Supports(request))
        {
            throw new TranslationProviderException(
                TranslationErrorKind.Configuration,
                "当前内置离线模型仅支持英语与简体中文互译。" );
        }

        _ = TryResolveLanguagePair(request, out string sourceLanguage, out string targetLanguage);

        Task initialization = GetInitializationTask();
        await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        string requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new TranslationProviderException(
                TranslationErrorKind.ProviderUnavailable,
                "无法创建离线翻译请求，请稍后重试。");
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => CancelPendingRequest(requestId, cancellationToken));

        try
        {
            string json = JsonSerializer.Serialize(new
            {
                v = 1,
                type = "translate",
                id = requestId,
                text = sourceText,
                from = sourceLanguage,
                to = targetLanguage
            });

            await InvokeOnDispatcherAsync(
                () =>
                {
                    CoreWebView2 core = _webView?.CoreWebView2
                        ?? throw new TranslationProviderException(
                            TranslationErrorKind.ProviderUnavailable,
                            "离线翻译引擎尚未就绪。");
                    core.PostWebMessageAsJson(json);
                },
                cancellationToken).ConfigureAwait(false);

            string translatedText = await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(translatedText))
            {
                throw new TranslationProviderException(
                    TranslationErrorKind.InvalidResponse,
                    "离线翻译引擎返回了空结果。");
            }

            return new TranslationResult(
                sourceText,
                translatedText.Trim(),
                sourceLanguage,
                TranslationOrigin.Offline);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        FailAllPending(new ObjectDisposedException(nameof(BergamotOfflineTranslationProvider)));

        try
        {
            if (_dispatcher.CheckAccess())
            {
                DisposeHostOnUiThread();
            }
            else
            {
                _dispatcher.Invoke(DisposeHostOnUiThread);
            }
        }
        catch (TaskCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            // The application dispatcher may already be shutting down.
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task WarmUpCoreAsync()
    {
        if (!IsAvailable)
        {
            throw new TranslationProviderException(
                TranslationErrorKind.Configuration,
                AvailabilityMessage);
        }

        _ = await TranslateAsync(
            new TranslationRequest("Hello.", "en", "zh-CN"),
            _lifetime.Token).ConfigureAwait(false);
    }

    private Task GetInitializationTask()
    {
        lock (_initializationGate)
        {
            _initializationTask ??= StartInitializationOnDispatcherAsync();
            return _initializationTask;
        }
    }

    private Task StartInitializationOnDispatcherAsync()
    {
        if (_dispatcher.CheckAccess())
        {
            return InitializeOnUiThreadAsync();
        }

        return _dispatcher
            .InvokeAsync(InitializeOnUiThreadAsync, DispatcherPriority.Normal)
            .Task
            .Unwrap();
    }

    private async Task InitializeOnUiThreadAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(_userDataFolder);

        _readySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: _userDataFolder).ConfigureAwait(true);

        _webView = new WebView2
        {
            Width = 1,
            Height = 1,
            Focusable = false
        };
        _hostWindow = new OfflineTranslationHostWindow(_webView);
        _hostWindow.Show();

        await _webView.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);
        CoreWebView2 core = _webView.CoreWebView2;
        ConfigureSecurity(core);

        core.WebMessageReceived += Core_OnWebMessageReceived;
        core.NavigationStarting += Core_OnNavigationStarting;
        core.NavigationCompleted += Core_OnNavigationCompleted;
        core.NewWindowRequested += Core_OnNewWindowRequested;
        core.DownloadStarting += Core_OnDownloadStarting;
        core.PermissionRequested += Core_OnPermissionRequested;
        core.ProcessFailed += Core_OnProcessFailed;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Core_OnWebResourceRequested;
        core.SetVirtualHostNameToFolderMapping(
            EngineHost,
            _assetsPath,
            CoreWebView2HostResourceAccessKind.Deny);
        core.Navigate($"{EngineOrigin}/index.html");

        await _readySignal.Task
            .WaitAsync(TimeSpan.FromSeconds(60), _lifetime.Token)
            .ConfigureAwait(true);
    }

    private static void ConfigureSecurity(CoreWebView2 core)
    {
        CoreWebView2Settings settings = core.Settings;
        settings.IsWebMessageEnabled = true;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsZoomControlEnabled = false;
    }

    private void Core_OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrustedEngineSource(e.Source))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = document.RootElement;
            string type = root.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            if (type == "ready")
            {
                _readySignal?.TrySetResult(true);
                return;
            }

            if (type == "engine-error")
            {
                string engineError = ReadString(root, "error", "离线引擎发生未知错误。");
                FailAllPending(new TranslationProviderException(
                    TranslationErrorKind.ProviderUnavailable,
                    $"离线翻译引擎错误：{engineError}"));
                return;
            }

            if (type != "translation-result")
            {
                return;
            }

            string id = ReadString(root, "id", string.Empty);
            if (id.Length == 0 || !_pending.TryRemove(id, out TaskCompletionSource<string>? completion))
            {
                return;
            }

            bool succeeded = root.TryGetProperty("ok", out JsonElement okElement)
                && okElement.ValueKind is JsonValueKind.True;
            if (succeeded)
            {
                completion.TrySetResult(ReadString(root, "text", string.Empty));
            }
            else
            {
                string error = ReadString(root, "error", "未知错误");
                completion.TrySetException(new TranslationProviderException(
                    TranslationErrorKind.ProviderUnavailable,
                    $"离线翻译失败：{error}"));
            }
        }
        catch (JsonException exception)
        {
            FailAllPending(new TranslationProviderException(
                TranslationErrorKind.InvalidResponse,
                "离线翻译引擎返回了无法识别的数据。",
                innerException: exception));
        }
    }

    private static void Core_OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        e.Cancel = !IsTrustedEngineSource(e.Uri);
    }

    private void Core_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            _readySignal?.TrySetException(new TranslationProviderException(
                TranslationErrorKind.ProviderUnavailable,
                $"无法加载离线翻译引擎页面：{e.WebErrorStatus}"));
        }
    }

    private static void Core_OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
    }

    private static void Core_OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
    }

    private static void Core_OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
    }

    private void Core_OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var failure = new TranslationProviderException(
            TranslationErrorKind.ProviderUnavailable,
            $"离线翻译进程异常（{e.ProcessFailedKind}），请重启 Huaci。");
        _readySignal?.TrySetException(failure);
        FailAllPending(failure);

        lock (_initializationGate)
        {
            _initializationTask = Task.FromException(failure);
        }
    }

    private void Core_OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_environment is null)
        {
            return;
        }

        e.Response = _environment.CreateWebResourceResponse(
            new MemoryStream(),
            403,
            "Forbidden",
            "Content-Type: text/plain");
    }

    private void DisposeHostOnUiThread()
    {
        if (_webView?.CoreWebView2 is CoreWebView2 core)
        {
            core.WebMessageReceived -= Core_OnWebMessageReceived;
            core.NavigationStarting -= Core_OnNavigationStarting;
            core.NavigationCompleted -= Core_OnNavigationCompleted;
            core.NewWindowRequested -= Core_OnNewWindowRequested;
            core.DownloadStarting -= Core_OnDownloadStarting;
            core.PermissionRequested -= Core_OnPermissionRequested;
            core.ProcessFailed -= Core_OnProcessFailed;
            core.WebResourceRequested -= Core_OnWebResourceRequested;
        }

        _webView?.Dispose();
        _webView = null;

        if (_hostWindow is not null)
        {
            _hostWindow.Content = null;
            _hostWindow.Close();
            _hostWindow = null;
        }

        _environment = null;
    }

    private Task InvokeOnDispatcherAsync(Action action, CancellationToken cancellationToken)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }

    private void CancelPendingRequest(string id, CancellationToken cancellationToken)
    {
        if (_pending.TryRemove(id, out TaskCompletionSource<string>? completion))
        {
            completion.TrySetCanceled(cancellationToken);
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach ((string id, TaskCompletionSource<string> completion) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static string NormalizeLanguage(string? value) =>
        (value ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();

    private static bool TryResolveLanguagePair(
        TranslationRequest request,
        out string sourceLanguage,
        out string targetLanguage)
    {
        string source = NormalizeLanguage(request.SourceLanguage);
        string target = NormalizeLanguage(request.TargetLanguage);

        bool targetIsEnglish = IsEnglish(target);
        bool targetIsChinese = IsSimplifiedChinese(target);
        bool sourceIsAuto = source is "" or "auto";
        bool sourceIsEnglish = IsEnglish(source);
        bool sourceIsChinese = IsSimplifiedChinese(source);

        if ((sourceIsEnglish || sourceIsAuto) && targetIsChinese)
        {
            sourceLanguage = "en";
            targetLanguage = "zh";
            return true;
        }

        if ((sourceIsChinese || sourceIsAuto) && targetIsEnglish)
        {
            sourceLanguage = "zh";
            targetLanguage = "en";
            return true;
        }

        sourceLanguage = string.Empty;
        targetLanguage = string.Empty;
        return false;
    }

    private static bool IsEnglish(string language) =>
        language == "en" || language.StartsWith("en-", StringComparison.Ordinal);

    private static bool IsSimplifiedChinese(string language) =>
        language is "zh" or "zh-cn" or "zh-hans"
        || language.StartsWith("zh-hans-", StringComparison.Ordinal);

    private static bool IsTrustedEngineSource(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? source)
        && source.Scheme == Uri.UriSchemeHttps
        && source.Host.Equals(EngineHost, StringComparison.OrdinalIgnoreCase)
        && source.IsDefaultPort;

    private static string ReadString(JsonElement root, string propertyName, string fallback) =>
        root.TryGetProperty(propertyName, out JsonElement element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : fallback;

    private static bool DetectWebView2Runtime()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
