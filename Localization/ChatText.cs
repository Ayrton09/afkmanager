namespace AfkManager.Localization;

internal static class ChatText
{
    private const string EmptyPlaceholder = "?";

    /// <summary>
    /// Removes control characters from text that is echoed back to players.
    /// CS2 chat colours are control bytes (0x01-0x10), so an unsanitised player name can be used
    /// to recolour or fake plugin/admin messages in the announcement it appears in.
    /// </summary>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EmptyPlaceholder;
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

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? EmptyPlaceholder : sanitized;
    }
}
