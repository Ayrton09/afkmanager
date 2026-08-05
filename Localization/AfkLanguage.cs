using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;

namespace AfkManager.Localization;

public sealed class AfkLanguage
{
    private string? _formattedPrefix;

    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = "[afk manager]";

    [JsonPropertyName("afk_warning")]
    public string AfkWarning { get; set; } = "{prefix} You are AFK. Move or you will be {action}.";

    [JsonPropertyName("spawn_warning")]
    public string SpawnWarning { get; set; } = "{prefix} You are AFK at spawn. Move or you will be {action}.";

    [JsonPropertyName("action_move_seconds")]
    public string ActionMoveSeconds { get; set; } = "moved to spectator in {seconds} seconds";

    [JsonPropertyName("action_kick_seconds")]
    public string ActionKickSeconds { get; set; } = "kicked in {seconds} seconds";

    [JsonPropertyName("kick_reason")]
    public string KickReason { get; set; } = "Kicked for being AFK";

    [JsonPropertyName("no_team_kick_reason")]
    public string NoTeamKickReason { get; set; } = "Kicked for not choosing a team";

    [JsonPropertyName("moved_to_spectator_afk")]
    public string MovedToSpectatorAfk { get; set; } = "{prefix} {player} was moved to spectator for being AFK.";

    [JsonPropertyName("moved_to_spectator_spawn_afk")]
    public string MovedToSpectatorSpawnAfk { get; set; } = "{prefix} {player} was moved to spectator for being AFK at spawn.";

    public string FormatAfkWarning(string action)
    {
        return ReplaceCommon(AfkWarning, action);
    }

    public string FormatSpawnWarning(string action)
    {
        return ReplaceCommon(SpawnWarning, action);
    }

    public string FormatMoveAction(int seconds)
    {
        return ReplaceSeconds(ActionMoveSeconds, seconds);
    }

    public string FormatKickAction(int seconds)
    {
        return ReplaceSeconds(ActionKickSeconds, seconds);
    }

    public string FormatMovedToSpectator(string playerName, bool spawnAfk)
    {
        var template = spawnAfk ? MovedToSpectatorSpawnAfk : MovedToSpectatorAfk;
        return ReplacePrefix(template)
            .Replace("{player}", FormatPlayerName(playerName), StringComparison.Ordinal);
    }

    public string FormatKickNotice(string reason)
    {
        return $"{FormatPrefix()} {reason}";
    }

    private string ReplaceCommon(string value, string action)
    {
        return ReplacePrefix(value)
            .Replace("{action}", action, StringComparison.Ordinal);
    }

    private string ReplacePrefix(string value)
    {
        return value.Replace("{prefix}", FormatPrefix(), StringComparison.Ordinal);
    }

    private string FormatPrefix()
    {
        if (_formattedPrefix is not null)
        {
            return _formattedPrefix;
        }

        var cleanPrefix = ChatText.Sanitize(Prefix).Trim();
        _formattedPrefix = $" {ChatColors.LightPurple}{cleanPrefix}{ChatColors.Default}";
        return _formattedPrefix;
    }

    private static string FormatPlayerName(string playerName)
    {
        return $"{ChatColors.Green}{ChatText.SanitizeName(playerName)}{ChatColors.Default}";
    }

    private static string ReplaceSeconds(string value, int seconds)
    {
        return value.Replace("{seconds}", FormatSeconds(seconds), StringComparison.Ordinal);
    }

    private static string FormatSeconds(int seconds)
    {
        return $"{ChatColors.Yellow}{seconds}{ChatColors.Default}";
    }
}
