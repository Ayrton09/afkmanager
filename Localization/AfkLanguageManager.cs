using System.Globalization;
using System.Text.Json;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace AfkManager.Localization;

public sealed class AfkLanguageManager
{
    private const string Prefix = "[afkmanager]";
    private const string BuiltInFallbackLanguage = "en";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _languageDirectory;
    private readonly ILogger _logger;
    private readonly Dictionary<string, AfkLanguage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private AfkLanguage? _serverLanguage;

    public AfkLanguageManager(string pluginDirectory, ILogger logger)
    {
        _languageDirectory = Path.Combine(pluginDirectory, "Lang");
        _logger = logger;
    }

    /// <summary>
    /// Resolves the server language once and then serves it from a field. Warning messages are
    /// formatted per player per check tick, so this must not re-resolve on every call.
    /// </summary>
    public AfkLanguage LoadCounterStrikeSharpLanguage()
    {
        return _serverLanguage ??= Resolve(GetCounterStrikeSharpLanguage());
    }

    public string GetCounterStrikeSharpLanguageName()
    {
        return GetCounterStrikeSharpLanguage();
    }

    public void ClearCache()
    {
        _cache.Clear();
        _serverLanguage = null;
    }

    private AfkLanguage Resolve(string language)
    {
        if (TryLoad(language, out var loaded))
        {
            return loaded;
        }

        var neutralLanguage = GetNeutralLanguage(language);
        if (!string.Equals(neutralLanguage, language, StringComparison.OrdinalIgnoreCase)
            && TryLoad(neutralLanguage, out loaded))
        {
            _logger.LogInformation(
                "{Prefix} No language file for \"{Language}\", using \"{Neutral}\" instead.",
                Prefix, language, neutralLanguage);
            return loaded;
        }

        if (!string.Equals(BuiltInFallbackLanguage, language, StringComparison.OrdinalIgnoreCase)
            && TryLoad(BuiltInFallbackLanguage, out loaded))
        {
            _logger.LogWarning(
                "{Prefix} No language file for \"{Language}\" in {Directory}, falling back to \"{Fallback}\".",
                Prefix, language, _languageDirectory, BuiltInFallbackLanguage);
            return loaded;
        }

        _logger.LogWarning(
            "{Prefix} No usable language file found in {Directory}, using built-in English defaults.",
            Prefix, _languageDirectory);
        return new AfkLanguage();
    }

    private bool TryLoad(string language, out AfkLanguage loaded)
    {
        if (_cache.TryGetValue(language, out var cached))
        {
            loaded = cached;
            return true;
        }

        loaded = new AfkLanguage();
        var languagePath = Path.Combine(_languageDirectory, $"{language}.json");

        if (!File.Exists(languagePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(languagePath);
            loaded = JsonSerializer.Deserialize<AfkLanguage>(json, JsonOptions) ?? new AfkLanguage();
            _cache[language] = loaded;
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "{Prefix} Failed to load language file: {Path}.", Prefix, languagePath);
            return false;
        }
    }

    private static string NormalizeLanguage(string? language, string fallback)
    {
        return string.IsNullOrWhiteSpace(language) ? fallback : language.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Prefers the CounterStrikeSharp core config value directly. CounterStrikeSharp also applies
    /// it to the process culture, which is used as a fallback if the value is unavailable.
    /// </summary>
    private static string GetCounterStrikeSharpLanguage()
    {
        var configured = CoreConfig.ServerLanguage;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return NormalizeLanguage(configured, BuiltInFallbackLanguage);
        }

        var culture = CultureInfo.DefaultThreadCurrentUICulture ?? CultureInfo.CurrentUICulture;
        return NormalizeLanguage(culture.Name, BuiltInFallbackLanguage);
    }

    private static string GetNeutralLanguage(string language)
    {
        var separatorIndex = language.IndexOfAny(['-', '_']);
        return separatorIndex <= 0 ? language : language[..separatorIndex];
    }
}
