using System.Text;

namespace AfkManager.Localization;

internal static class ChatText
{
    private const string NamePlaceholder = "?";

    /// <summary>
    /// Removes control characters from text that is echoed back to players.
    /// CS2 chat colours are control bytes (0x01-0x10), so unsanitised text can be used to recolour
    /// or fake plugin and admin messages in whatever line it appears in.
    /// </summary>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsSanitizing = false;
        foreach (var character in value)
        {
            if (IsStripped(character))
            {
                needsSanitizing = true;
                break;
            }
        }

        if (!needsSanitizing)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!IsStripped(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    // Control characters (chat colours are 0x01-0x10) and the bidi embedding, override and
    // isolate characters, which can reverse the rest of the line a name is printed in.
    private static bool IsStripped(char character)
    {
        return char.IsControl(character)
            || character is >= '\u202A' and <= '\u202E'
            || character is >= '\u2066' and <= '\u2069';
    }

    /// <summary>
    /// Sanitises a player name, substituting a placeholder if nothing printable is left. An empty
    /// result would otherwise let a name made entirely of control characters render as no name.
    /// </summary>
    internal static string SanitizeName(string? playerName)
    {
        var sanitized = Sanitize(playerName).Trim();
        return sanitized.Length == 0 ? NamePlaceholder : sanitized;
    }
}
