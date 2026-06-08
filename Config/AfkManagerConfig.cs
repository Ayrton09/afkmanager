using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace AfkManager.Config;

public sealed class AfkManagerConfig : BasePluginConfig
{
    public override int Version { get; set; } = 4;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("check_interval_seconds")]
    public float CheckIntervalSeconds { get; set; } = 5.0f;

    [JsonPropertyName("spawn_warning_time_seconds")]
    public float SpawnWarningTimeSeconds { get; set; } = 10.0f;

    [JsonPropertyName("spawn_move_to_spectator_time_seconds")]
    public float SpawnMoveToSpectatorTimeSeconds { get; set; } = 15.0f;

    [JsonPropertyName("warning_time_seconds")]
    public float WarningTimeSeconds { get; set; } = 30.0f;

    [JsonPropertyName("move_to_spectator_time_seconds")]
    public float MoveToSpectatorTimeSeconds { get; set; } = 90.0f;

    [JsonPropertyName("kick_time_seconds")]
    public float KickTimeSeconds { get; set; } = 0.0f;

    [JsonPropertyName("no_team_move_to_spectator_time_seconds")]
    public float NoTeamMoveToSpectatorTimeSeconds { get; set; } = 10.0f;

    [JsonPropertyName("no_team_kick_time_seconds")]
    public float NoTeamKickTimeSeconds { get; set; } = 0.0f;

    [JsonPropertyName("repeat_warning_interval_seconds")]
    public float RepeatWarningIntervalSeconds { get; set; } = 5.0f;

    [JsonPropertyName("ignore_bots")]
    public bool IgnoreBots { get; set; } = true;

    [JsonPropertyName("ignore_spectators")]
    public bool IgnoreSpectators { get; set; } = true;

    [JsonPropertyName("admin_immunity")]
    public bool AdminImmunity { get; set; } = false;

    [JsonPropertyName("admin_immunity_flags")]
    public List<string> AdminImmunityFlags { get; set; } = ["@css/generic"];

    [JsonIgnore]
    public string[] AdminImmunityFlagsArray { get; private set; } = ["@css/generic"];

    [JsonPropertyName("log_actions")]
    public bool LogActions { get; set; } = false;

    public void Normalize()
    {
        CheckIntervalSeconds = Math.Clamp(CheckIntervalSeconds, 1.0f, 60.0f);
        SpawnWarningTimeSeconds = NormalizeThreshold(SpawnWarningTimeSeconds);
        SpawnMoveToSpectatorTimeSeconds = NormalizeThreshold(SpawnMoveToSpectatorTimeSeconds);
        WarningTimeSeconds = NormalizeThreshold(WarningTimeSeconds);
        MoveToSpectatorTimeSeconds = NormalizeThreshold(MoveToSpectatorTimeSeconds);
        KickTimeSeconds = NormalizeThreshold(KickTimeSeconds);
        NoTeamMoveToSpectatorTimeSeconds = NormalizeThreshold(NoTeamMoveToSpectatorTimeSeconds);
        NoTeamKickTimeSeconds = NormalizeThreshold(NoTeamKickTimeSeconds);
        RepeatWarningIntervalSeconds = NormalizeThreshold(RepeatWarningIntervalSeconds);

        AdminImmunityFlags ??= [];
        AdminImmunityFlags = AdminImmunityFlags
            .Where(flag => !string.IsNullOrWhiteSpace(flag))
            .Select(flag => flag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AdminImmunityFlagsArray = AdminImmunityFlags.ToArray();
    }

    public bool IsWarningEnabled => WarningTimeSeconds > 0;
    public bool IsSpawnWarningEnabled => SpawnWarningTimeSeconds > 0;
    public bool IsSpawnMoveEnabled => SpawnMoveToSpectatorTimeSeconds > 0;
    public bool IsMoveEnabled => MoveToSpectatorTimeSeconds > 0;
    public bool IsKickEnabled => KickTimeSeconds > 0;
    public bool IsNoTeamMoveEnabled => NoTeamMoveToSpectatorTimeSeconds > 0;
    public bool IsNoTeamKickEnabled => NoTeamKickTimeSeconds > 0;
    public bool IsRepeatWarningEnabled => RepeatWarningIntervalSeconds > 0;
    public int RepeatWarningIntervalSecondsInt => Math.Max(1, (int)Math.Round(RepeatWarningIntervalSeconds));

    private static float NormalizeThreshold(float value)
    {
        return value <= 0 ? -1.0f : value;
    }
}
