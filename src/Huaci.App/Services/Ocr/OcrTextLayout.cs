using System.Text;

namespace Huaci.App.Services.Ocr;

/// <summary>
/// Reconstructs paragraph structure from OCR line boxes before translation.
/// The translator receives natural paragraphs instead of one hard line break
/// for every visual source line, which makes the replacement view read like a
/// document rather than a list of OCR fragments.
/// </summary>
public static class OcrTextLayout
{
    public static string ReconstructParagraphs(
        IReadOnlyList<OcrTextBlock>? blocks,
        string? fallbackText = null)
    {
        OcrTextBlock[] ordered = blocks?
            .Where(block => !string.IsNullOrWhiteSpace(block.Text)
                && block.Bounds.Width > 0
                && block.Bounds.Height > 0)
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
            .ToArray()
            ?? Array.Empty<OcrTextBlock>();

        if (ordered.Length == 0)
        {
            return fallbackText?.Trim() ?? string.Empty;
        }

        double[] heights = ordered
            .Select(block => (double)block.Bounds.Height)
            .OrderBy(height => height)
            .ToArray();
        double medianHeight = Math.Max(1d, heights[heights.Length / 2]);
        double paragraphGap = Math.Max(4d, medianHeight * 0.62d);
        float minimumX = ordered.Min(block => block.Bounds.X);

        var text = new StringBuilder();
        OcrTextBlock previous = ordered[0];
        text.Append(previous.Text.Trim());

        for (int index = 1; index < ordered.Length; index++)
        {
            OcrTextBlock current = ordered[index];
            string previousText = previous.Text.Trim();
            string currentText = current.Text.Trim();
            double previousCenter = previous.Bounds.Y + (previous.Bounds.Height / 2d);
            double currentCenter = current.Bounds.Y + (current.Bounds.Height / 2d);
            bool sameVisualLine = Math.Abs(currentCenter - previousCenter) <= medianHeight * 0.42d
                && current.Bounds.X >= previous.Bounds.X;
            double verticalGap = current.Bounds.Y - previous.Bounds.Bottom;
            bool beginsIndentedParagraph = current.Bounds.X - minimumX > medianHeight * 1.15d
                && verticalGap > medianHeight * 0.22d
                && EndsSentence(previousText);
            bool beginsParagraph = !sameVisualLine
                && (verticalGap > paragraphGap || beginsIndentedParagraph);

            if (beginsParagraph)
            {
                text.Append("\n\n");
            }
            else if (NeedsWordSeparator(previousText, currentText))
            {
                text.Append(' ');
            }

            text.Append(currentText);
            previous = current;
        }

        return text.ToString().Trim();
    }

    private static bool NeedsWordSeparator(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        char last = left[^1];
        char first = right[0];
        return !char.IsWhiteSpace(last)
            && !char.IsWhiteSpace(first)
            && !IsCjk(last)
            && !IsCjk(first);
    }

    private static bool EndsSentence(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        return value[^1] is '.' or '!' or '?' or '。' or '！' or '？' or '”' or '’' or '"';
    }

    private static bool IsCjk(char value) => value is
        >= '\u3400' and <= '\u9fff'
        or >= '\uf900' and <= '\ufaff';
}
