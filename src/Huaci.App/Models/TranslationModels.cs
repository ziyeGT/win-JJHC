using System.Net;

namespace Huaci.App.Models;

public sealed record TranslationRequest(
    string Text,
    string SourceLanguage = "auto",
    string TargetLanguage = "zh-CN");

public enum TranslationRouteMode
{
    OfflineFirst = 0,
    OfflineOnly = 1,
    OnlineOnly = 2
}

public enum TranslationOrigin
{
    Online,
    Offline
}

public sealed record TranslationResult(
    string SourceText,
    string TranslatedText,
    string? DetectedSourceLanguage = null,
    TranslationOrigin Origin = TranslationOrigin.Online,
    bool UsedFallback = false);

/// <summary>
/// Per-request provider configuration. ToString is intentionally redacted so an API key is
/// not exposed by common diagnostic logging.
/// </summary>
public sealed class TranslationProviderOptions
{
    public TranslationProviderOptions(string apiBaseUrl, string model, string apiKey)
    {
        ApiBaseUrl = apiBaseUrl;
        Model = model;
        ApiKey = apiKey;
    }

    public string ApiBaseUrl { get; }

    public string Model { get; }

    public string ApiKey { get; }

    public override string ToString() => $"{nameof(TranslationProviderOptions)} {{ ApiBaseUrl = {ApiBaseUrl}, Model = {Model}, ApiKey = [REDACTED] }}";
}

public enum TranslationErrorKind
{
    Configuration,
    Authentication,
    RateLimited,
    ProviderUnavailable,
    InvalidResponse,
    Timeout,
    Network,
    Unknown
}

public sealed class TranslationProviderException : Exception
{
    public TranslationProviderException(
        TranslationErrorKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public TranslationErrorKind Kind { get; }

    public HttpStatusCode? StatusCode { get; }
}
