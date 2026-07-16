using Huaci.App.Models;

namespace Huaci.App.Services.Translation;

public sealed class TranslationService : ITranslationService
{
    private readonly IOfflineTranslationProvider _offlineProvider;
    private readonly ITranslationProvider _onlineProvider;
    private readonly Func<string?> _readApiKey;

    public TranslationService(
        IOfflineTranslationProvider offlineProvider,
        ITranslationProvider onlineProvider,
        Func<string?> readApiKey)
    {
        _offlineProvider = offlineProvider;
        _onlineProvider = onlineProvider;
        _readApiKey = readApiKey;
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new TranslationProviderException(
                TranslationErrorKind.Configuration,
                "没有可翻译的文本。");
        }

        return settings.TranslationMode switch
        {
            TranslationRouteMode.OfflineOnly => await TranslateOfflineAsync(
                request,
                cancellationToken).ConfigureAwait(false),
            TranslationRouteMode.OnlineOnly => await TranslateOnlineAsync(
                request,
                settings,
                usedFallback: false,
                cancellationToken).ConfigureAwait(false),
            _ => await TranslateOfflineFirstAsync(
                request,
                settings,
                cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<TranslationResult> TranslateOfflineFirstAsync(
        TranslationRequest request,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        TranslationProviderException offlineFailure;

        if (!_offlineProvider.IsAvailable)
        {
            offlineFailure = new TranslationProviderException(
                TranslationErrorKind.Configuration,
                _offlineProvider.AvailabilityMessage);
        }
        else if (!_offlineProvider.Supports(request))
        {
            offlineFailure = CreateUnsupportedLanguageException();
        }
        else
        {
            try
            {
                TranslationResult result = await _offlineProvider
                    .TranslateAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return result with { Origin = TranslationOrigin.Offline, UsedFallback = false };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TranslationProviderException exception)
            {
                offlineFailure = exception;
            }
            catch (Exception exception)
            {
                offlineFailure = new TranslationProviderException(
                    TranslationErrorKind.ProviderUnavailable,
                    "内置离线翻译引擎暂时不可用。",
                    innerException: exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await TranslateOnlineAsync(
                request,
                settings,
                usedFallback: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TranslationProviderException onlineFailure)
        {
            throw new TranslationProviderException(
                offlineFailure.Kind,
                $"{offlineFailure.Message} 在线回退也不可用：{onlineFailure.Message}",
                innerException: new AggregateException(offlineFailure, onlineFailure));
        }
    }

    private async Task<TranslationResult> TranslateOfflineAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        if (!_offlineProvider.IsAvailable)
        {
            throw new TranslationProviderException(
                TranslationErrorKind.Configuration,
                _offlineProvider.AvailabilityMessage);
        }

        if (!_offlineProvider.Supports(request))
        {
            throw CreateUnsupportedLanguageException();
        }

        TranslationResult result = await _offlineProvider
            .TranslateAsync(request, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result with { Origin = TranslationOrigin.Offline, UsedFallback = false };
    }

    private async Task<TranslationResult> TranslateOnlineAsync(
        TranslationRequest request,
        AppSettings settings,
        bool usedFallback,
        CancellationToken cancellationToken)
    {
        string? apiKey;
        try
        {
            apiKey = _readApiKey();
        }
        catch (Exception exception)
        {
            throw new TranslationProviderException(
                TranslationErrorKind.Configuration,
                "无法读取 Windows 中保存的 API 密钥。",
                innerException: exception);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranslationProviderException(
                TranslationErrorKind.Configuration,
                "未配置在线翻译 API Key。可在设置中配置，或使用内置中英互译模型。");
        }

        TranslationResult result = await _onlineProvider.TranslateAsync(
            request,
            new TranslationProviderOptions(settings.ApiBaseUrl, settings.Model, apiKey),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result with { Origin = TranslationOrigin.Online, UsedFallback = usedFallback };
    }

    private static TranslationProviderException CreateUnsupportedLanguageException() => new(
        TranslationErrorKind.Configuration,
        "当前内置离线模型仅支持英语与简体中文互译；其他语言请使用在线模式。");
}
