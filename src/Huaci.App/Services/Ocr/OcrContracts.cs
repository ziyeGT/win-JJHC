namespace Huaci.App.Services.Ocr;

/// <summary>A rectangle in source-image pixel coordinates.</summary>
public readonly record struct OcrRectangle(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;
}

public sealed record OcrTextBlock(
    string Text,
    double Confidence,
    OcrRectangle Bounds);

public enum OcrRecognitionStatus
{
    Success,
    NoText,
    EmptyImage,
    InvalidImage,
    EngineUnavailable,
    Cancelled,
    Disposed,
    Failed,
}

/// <summary>
/// A non-throwing OCR result. <see cref="Diagnostic"/> is intended for local
/// diagnostics and should not be displayed as recognized text.
/// </summary>
public sealed record OcrRecognitionResult(
    OcrRecognitionStatus Status,
    string Text,
    IReadOnlyList<OcrTextBlock> Blocks,
    string? Diagnostic = null)
{
    private static readonly IReadOnlyList<OcrTextBlock> NoBlocks = Array.Empty<OcrTextBlock>();

    public bool IsSuccess => Status == OcrRecognitionStatus.Success && !string.IsNullOrWhiteSpace(Text);

    public static OcrRecognitionResult WithoutText(
        OcrRecognitionStatus status,
        string? diagnostic = null) =>
        new(status, string.Empty, NoBlocks, diagnostic);
}

/// <summary>
/// Offline OCR over encoded image bytes. The contract intentionally has no
/// WPF or System.Drawing dependency, so screen capture can pass its in-memory
/// PNG directly and avoid a temporary file containing private text.
/// </summary>
public interface IOcrService : IDisposable
{
    bool IsAvailable { get; }

    string AvailabilityMessage { get; }

    Task<OcrRecognitionResult> RecognizeAsync(
        ReadOnlyMemory<byte> encodedImage,
        CancellationToken cancellationToken = default);

    Task<OcrRecognitionResult> RecognizeAsync(
        Stream encodedImage,
        CancellationToken cancellationToken = default);
}
