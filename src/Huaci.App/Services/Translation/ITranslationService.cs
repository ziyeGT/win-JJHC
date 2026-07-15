using Huaci.App.Models;

namespace Huaci.App.Services.Translation;

public interface IOfflineTranslationProvider
{
    bool IsAvailable { get; }

    string AvailabilityMessage { get; }

    bool Supports(TranslationRequest request);

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default);
}
