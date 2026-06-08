using System.Globalization;
using System.Text.Json;
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

    private readonly string _pluginDirectory;
    private readonly ILogger _logger;
    private readonly Dictionary<string, AfkLanguage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AfkLanguage> _resolvedCache = new(StringComparer.OrdinalIgnoreCase);

    public AfkLanguageManager(string pluginDirectory, ILogger logger)
    {
        _pluginDirectory = pluginDirectory;
        _logger = logger;
    }

    public AfkLanguage Load(string language)
    {
        var normalizedLanguage = NormalizeLanguage(language, BuiltInFallbackLanguage);
        return LoadWithFallback(normalizedLanguage, BuiltInFallbackLanguage, logMissing: true);
    }

    public AfkLanguage LoadCounterStrikeSharpLanguage()
    {
        return LoadWithFallback(GetCounterStrikeSharpLanguage(), BuiltInFallbackLanguage, logMissing: false);
    }

    public string GetCounterStrikeSharpLanguageName()
    {
        return GetCounterStrikeSharpLanguage();
    }

    public void ClearCache()
    {
        _cache.Clear();
        _resolvedCache.Clear();
    }

    private AfkLanguage LoadWithFallback(string language, string fallbackLanguage, bool logMissing)
    {
        var resolvedKey = $"{language}|{fallbackLanguage}";
        if (_resolvedCache.TryGetValue(resolvedKey, out var cachedResolved))
        {
            return cachedResolved;
        }

        if (TryLoad(language, logMissing, out var loaded))
        {
            _resolvedCache[resolvedKey] = loaded;
            return loaded;
        }

        var neutralLanguage = GetNeutralLanguage(language);
        if (!string.Equals(neutralLanguage, language, StringComparison.OrdinalIgnoreCase)
            && TryLoad(neutralLanguage, logMissing, out loaded))
        {
            _resolvedCache[resolvedKey] = loaded;
            return loaded;
        }

        var normalizedFallback = NormalizeLanguage(fallbackLanguage, BuiltInFallbackLanguage);
        if (!string.Equals(normalizedFallback, language, StringComparison.OrdinalIgnoreCase)
            && TryLoad(normalizedFallback, logMissing, out loaded))
        {
            _resolvedCache[resolvedKey] = loaded;
            return loaded;
        }

        loaded = new AfkLanguage();
        _resolvedCache[resolvedKey] = loaded;
        return loaded;
    }

    private bool TryLoad(string language, bool logMissing, out AfkLanguage loaded)
    {
        loaded = new AfkLanguage();

        if (_cache.TryGetValue(language, out var cached))
        {
            loaded = cached;
            return true;
        }

        var languagePath = Path.Combine(_pluginDirectory, "Lang", $"{language}.json");
        if (!File.Exists(languagePath))
        {
            if (logMissing)
            {
                _logger.LogWarning("{Prefix} Language file not found: {Path}.", Prefix, languagePath);
            }

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

    private static string GetCounterStrikeSharpLanguage()
    {
        var culture = CultureInfo.DefaultThreadCurrentUICulture ?? CultureInfo.CurrentUICulture;
        return NormalizeLanguage(culture.Name, BuiltInFallbackLanguage);
    }

    private static string GetNeutralLanguage(string language)
    {
        var separatorIndex = language.IndexOfAny(['-', '_']);
        return separatorIndex <= 0 ? language : language[..separatorIndex];
    }
}
