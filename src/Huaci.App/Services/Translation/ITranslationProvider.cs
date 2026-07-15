using Huaci.App.Models;

namespace Huaci.App.Services.Translation;

public interface ITranslationProvider
{
    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        TranslationProviderOptions options,
        CancellationToken cancellationToken = default);
}
