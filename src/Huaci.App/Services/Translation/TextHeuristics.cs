using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Huaci.App.Services.Translation;

public static class TextHeuristics
{
    private static readonly Regex HorizontalWhitespace = new("[^\\S\\r\\n]+", RegexOptions.Compiled);
    private static readonly Regex ExcessBlankLines = new("\\n{3,}", RegexOptions.Compiled);

    public static bool TryPrepareForTranslation(string? value, out string normalized)
    {
        normalized = Normalize(value);
        return IsLikelyTranslatableNormalized(normalized);
    }

    public static bool IsLikelyTranslatable(string? value) =>
        TryPrepareForTranslation(value, out _);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string text = value
            .Normalize(NormalizationForm.FormKC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ')
            .Replace('\u2007', ' ')
            .Replace('\u202F', ' ')
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal);

        text = HorizontalWhitespace.Replace(text, " ");
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            lines[index] = lines[index].Trim();
        }

        return ExcessBlankLines.Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    public static bool IsChineseOnly(string? value)
    {
        string normalized = Normalize(value);
        if (normalized.Length == 0)
        {
            return false;
        }

        bool hasChineseLetter = false;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (!IsLetter(rune))
            {
                continue;
            }

            if (!IsChinese(rune))
            {
                return false;
            }

            hasChineseLetter = true;
        }

        return hasChineseLetter;
    }

    private static bool IsLikelyTranslatableNormalized(string normalized)
    {
        if (normalized.Length == 0)
        {
            return false;
        }

        int digitCount = 0;
        bool hasLetter = false;
        bool hasChineseLetter = false;
        bool hasNonChineseLetter = false;

        foreach (Rune rune in normalized.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.DecimalDigitNumber)
            {
                digitCount++;
                continue;
            }

            if (!IsLetterCategory(category))
            {
                continue;
            }

            hasLetter = true;
            if (IsChinese(rune))
            {
                hasChineseLetter = true;
            }
            else
            {
                hasNonChineseLetter = true;
            }
        }

        // Pure punctuation/symbols and pure numbers carry no translatable language. The explicit
        // short-number check also rejects common accidental selections such as "1" or "42".
        if (!hasLetter)
        {
            return digitCount == 0 ? false : digitCount > 3 && ContainsNonNumericSemanticRune(normalized);
        }

        // Chinese text mixed with Latin/Japanese/Korean/etc. is still eligible; only pure Chinese
        // selections are skipped.
        return !hasChineseLetter || hasNonChineseLetter;
    }

    private static bool ContainsNonNumericSemanticRune(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.CurrencySymbol or UnicodeCategory.MathSymbol)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLetter(Rune rune) => IsLetterCategory(Rune.GetUnicodeCategory(rune));

    private static bool IsLetterCategory(UnicodeCategory category) => category is
        UnicodeCategory.UppercaseLetter
        or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter
        or UnicodeCategory.OtherLetter;

    private static bool IsChinese(Rune rune)
    {
        int value = rune.Value;
        return value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2EBEF
            or >= 0x2F800 and <= 0x2FA1F
            or >= 0x30000 and <= 0x323AF;
    }
}
