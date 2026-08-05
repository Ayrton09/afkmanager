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
            if (char.IsControl(character))
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
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
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
