using AfkManager.Config;
using AfkManager.Localization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace AfkManager.Core;

public sealed class AfkService
{
    private const string Prefix = "[afkmanager]";
    private const float MovementToleranceUnits = 8.0f;
    private const float ViewAngleToleranceDegrees = 3.0f;
    private readonly ILogger _logger;
    private readonly Action<string> _executeServerCommand;
    private readonly Dictionary<int, PlayerAfkState> _states = [];
    private readonly HashSet<int> _seenSlots = [];
    private readonly List<int> _staleSlots = [];
    private readonly AfkLanguageManager _languageManager;
    private AfkManagerConfig _config;

    public AfkService(AfkManagerConfig config, AfkLanguageManager languageManager, ILogger logger, Action<string> executeServerCommand)
    {
        _config = config;
        _languageManager = languageManager;
        _logger = logger;
        _executeServerCommand = executeServerCommand;
    }

    public void UpdateConfig(AfkManagerConfig config)
    {
        _config = config;
    }

    public void Clear()
    {
        _states.Clear();
    }

    public void RemovePlayer(int slot)
    {
        _states.Remove(slot);
    }

    public IReadOnlyCollection<PlayerAfkState> States => _states.Values;

    public void ResetPlayer(CCSPlayerController player)
    {
        var now = DateTimeOffset.UtcNow;
        var state = GetOrCreateState(player, now);
        state.ResetAllTracking(now);

        if (TryReadSample(player, out var position, out var viewAngles, out var buttons))
        {
            state.SetSample(position, viewAngles, buttons);
        }
    }

    public void MarkPlayerSpawned(CCSPlayerController player)
    {
        if (!IsValidHumanCandidate(player))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var state = GetOrCreateState(player, now);
        state.MarkSpawned(now);

        if (TryReadSample(player, out var position, out var viewAngles, out var buttons))
        {
            state.SetSample(position, viewAngles, buttons);
        }
    }

    public void MarkPlayerSpawnedIfAlive(CCSPlayerController player)
    {
        if (!IsValidHumanCandidate(player) || !IsPlayableTeam(GetTeam(player)) || !IsPlayerAlive(player))
        {
            return;
        }

        MarkPlayerSpawned(player);
    }

    public void MarkAliveTeamPlayersSpawned()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            MarkPlayerSpawnedIfAlive(player);
        }
    }

    public void MarkPlayerNotAlive(CCSPlayerController player)
    {
        if (!IsValidHumanCandidate(player))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var state = GetOrCreateState(player, now);
        state.MarkNotAlive(now);
    }

    public void CheckPlayers()
    {
        if (!_config.Enabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _seenSlots.Clear();

        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsValidHumanCandidate(player))
            {
                continue;
            }

            _seenSlots.Add(player.Slot);
            CheckPlayer(player, now);
        }

        RemoveStaleStates();
    }

    public string DescribePlayer(CCSPlayerController player)
    {
        var now = DateTimeOffset.UtcNow;
        var state = GetOrCreateState(player, now);
        var team = GetTeam(player);
        var inactiveSeconds = (int)(now - state.LastActivityAt).TotalSeconds;
        var noTeamSeconds = state.NoTeamSince is null ? 0 : (int)(now - state.NoTeamSince.Value).TotalSeconds;
        var spawnSeconds = state.SpawnedAt is null ? 0 : (int)(now - state.SpawnedAt.Value).TotalSeconds;
        var status = IsNoTeam(team)
            ? $"no-team {noTeamSeconds}s"
            : state.SpawnedAt is not null && !state.HasActivitySinceSpawn
                ? $"spawn-afk {spawnSeconds}s"
                : $"inactive {inactiveSeconds}s";

        return $"#{player.UserId} {player.PlayerName} team={team} {status} warned={state.WasWarned || state.WasSpawnWarned} moved={state.MovedToSpectatorForAfk || state.MovedToSpectatorForSpawn || state.MovedToSpectatorForNoTeam} kick_attempted={state.KickAttempted || state.NoTeamKickAttempted}";
    }

    public IEnumerable<CCSPlayerController> FindPlayers(string target)
    {
        var query = target.Trim();
        var matches = new List<CCSPlayerController>();

        if (query.StartsWith('#') && int.TryParse(query[1..], out var userId))
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (IsValidHumanCandidate(player) && player.UserId == userId)
                {
                    matches.Add(player);
                }
            }

            return matches;
        }

        foreach (var player in Utilities.GetPlayers())
        {
            if (IsValidHumanCandidate(player) && player.PlayerName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(player);
            }
        }

        return matches;
    }

    private void CheckPlayer(CCSPlayerController player, DateTimeOffset now)
    {
        var state = GetOrCreateState(player, now);
        var team = GetTeam(player);

        if (ShouldSkipForImmunity(player))
        {
            state.ResetAllTracking(now);
            return;
        }

        if (IsNoTeam(team))
        {
            state.ResetSpawnTracking();
            HandleNoTeamPlayer(player, state, now);
            return;
        }

        if (team == CsTeam.Spectator)
        {
            if (HandleSpectatorPlayer(player, state, now))
            {
                return;
            }

            if (_config.IgnoreSpectators)
            {
                state.ResetAllTracking(now);
                return;
            }

            state.ResetSpawnTracking();
            UpdateActivitySample(player, state, now);
            HandleTeamAfk(player, state, now, team);
            return;
        }

        state.ResetNoTeamTracking();

        if (!IsPlayerAlive(player))
        {
            state.MarkNotAlive(now);
            return;
        }

        if (!state.HasAliveState)
        {
            state.MarkAliveObserved(now);
        }
        else if (!state.IsAlive)
        {
            state.MarkSpawned(now);
        }

        UpdateActivitySample(player, state, now);

        if (HandleSpawnAfk(player, state, now))
        {
            return;
        }

        HandleTeamAfk(player, state, now, team);
    }

    private bool HandleSpawnAfk(CCSPlayerController player, PlayerAfkState state, DateTimeOffset now)
    {
        if (state.SpawnedAt is null || state.HasActivitySinceSpawn)
        {
            return false;
        }

        var spawnAfkSeconds = (float)(now - state.SpawnedAt.Value).TotalSeconds;

        if (_config.IsSpawnMoveEnabled
            && !state.MovedToSpectatorForSpawn
            && spawnAfkSeconds >= _config.SpawnMoveToSpectatorTimeSeconds)
        {
            MoveToSpectator(player, state, AfkWarningKind.Spawn);
            return true;
        }

        MaybeSendCountdownWarning(
            player,
            state,
            spawnAfkSeconds,
            _config.SpawnWarningTimeSeconds,
            _config.SpawnMoveToSpectatorTimeSeconds,
            AfkWarningKind.Spawn,
            AfkActionKind.Move,
            now);

        return _config.IsSpawnMoveEnabled
            && !state.MovedToSpectatorForSpawn
            && spawnAfkSeconds < _config.SpawnMoveToSpectatorTimeSeconds;
    }

    private void HandleTeamAfk(CCSPlayerController player, PlayerAfkState state, DateTimeOffset now, CsTeam team)
    {
        var inactiveSeconds = (float)(now - state.LastActivityAt).TotalSeconds;

        if (_config.IsMoveEnabled
            && !state.MovedToSpectatorForAfk
            && inactiveSeconds >= _config.MoveToSpectatorTimeSeconds
            && team != CsTeam.Spectator)
        {
            MoveToSpectator(player, state, AfkWarningKind.Normal);
            return;
        }

        if (_config.IsKickEnabled && !state.KickAttempted && inactiveSeconds >= _config.KickTimeSeconds)
        {
            KickPlayer(player, state, GetLanguage(player).KickReason, AfkWarningKind.Normal);
            return;
        }

        if (_config.IsMoveEnabled && !state.MovedToSpectatorForAfk && team != CsTeam.Spectator)
        {
            MaybeSendCountdownWarning(
                player,
                state,
                inactiveSeconds,
                _config.WarningTimeSeconds,
                _config.MoveToSpectatorTimeSeconds,
                AfkWarningKind.Normal,
                AfkActionKind.Move,
                now);
            return;
        }

        if (_config.IsKickEnabled && !state.KickAttempted)
        {
            MaybeSendCountdownWarning(
                player,
                state,
                inactiveSeconds,
                _config.WarningTimeSeconds,
                _config.KickTimeSeconds,
                AfkWarningKind.Normal,
                AfkActionKind.Kick,
                now);
        }
    }

    private void HandleNoTeamPlayer(CCSPlayerController player, PlayerAfkState state, DateTimeOffset now)
    {
        state.NoTeamSince ??= now;
        var noTeamSeconds = (float)(now - state.NoTeamSince.Value).TotalSeconds;

        if (_config.IsNoTeamMoveEnabled
            && !state.MovedToSpectatorForNoTeam
            && noTeamSeconds >= _config.NoTeamMoveToSpectatorTimeSeconds)
        {
            MoveToSpectator(player, state, AfkWarningKind.NoTeam);
            return;
        }

        if (_config.IsNoTeamKickEnabled && !state.NoTeamKickAttempted && noTeamSeconds >= _config.NoTeamKickTimeSeconds)
        {
            KickPlayer(player, state, GetLanguage(player).NoTeamKickReason, AfkWarningKind.NoTeam);
            return;
        }
    }

    private bool HandleSpectatorPlayer(CCSPlayerController player, PlayerAfkState state, DateTimeOffset now)
    {
        if (state.MovedToSpectatorForAfk && _config.IsKickEnabled && !state.KickAttempted)
        {
            var inactiveSeconds = (float)(now - state.LastActivityAt).TotalSeconds;
            if (inactiveSeconds >= _config.KickTimeSeconds)
            {
                KickPlayer(player, state, GetLanguage(player).KickReason, AfkWarningKind.Normal);
            }

            return true;
        }

        if (state.MovedToSpectatorForNoTeam && _config.IsNoTeamKickEnabled && !state.NoTeamKickAttempted)
        {
            var noTeamSeconds = state.NoTeamSince is null ? 0.0f : (float)(now - state.NoTeamSince.Value).TotalSeconds;
            if (noTeamSeconds >= _config.NoTeamKickTimeSeconds)
            {
                KickPlayer(player, state, GetLanguage(player).NoTeamKickReason, AfkWarningKind.NoTeam);
            }

            return true;
        }

        return false;
    }

    private void UpdateActivitySample(CCSPlayerController player, PlayerAfkState state, DateTimeOffset now)
    {
        if (!TryReadSample(player, out var position, out var viewAngles, out var buttons))
        {
            return;
        }

        if (!state.HasSample)
        {
            state.SetSample(position, viewAngles, buttons);
            if (state.SpawnedAt is not null && !state.HasActivitySinceSpawn)
            {
                state.ResetSpawnClock(now);
            }

            return;
        }

        var movementToleranceSquared = MovementToleranceUnits * MovementToleranceUnits;
        var moved = position.DistanceSquaredTo(state.LastPosition) > movementToleranceSquared;
        var looked = viewAngles.DifferenceTo(state.LastViewAngles) > ViewAngleToleranceDegrees;
        var pressedButtons = buttons != state.LastButtons;

        if (moved || looked || pressedButtons)
        {
            state.MarkActive(now, position, viewAngles, buttons);
        }
    }

    private static bool TryReadSample(
        CCSPlayerController player,
        out PositionSample position,
        out AngleSample viewAngles,
        out PlayerButtons buttons)
    {
        position = default;
        viewAngles = default;
        buttons = default;

        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid)
        {
            return false;
        }

        var origin = pawn.AbsOrigin;
        var angles = pawn.EyeAngles;
        if (origin is null || angles is null)
        {
            return false;
        }

        position = PositionSample.FromVector(origin);
        viewAngles = AngleSample.FromQAngle(angles);
        buttons = player.Buttons;
        return true;
    }

    private void MaybeSendCountdownWarning(
        CCSPlayerController player,
        PlayerAfkState state,
        float elapsedSeconds,
        float warningLeadSeconds,
        float actionTimeSeconds,
        AfkWarningKind warningKind,
        AfkActionKind actionKind,
        DateTimeOffset now)
    {
        if (!_config.IsRepeatWarningEnabled || warningLeadSeconds <= 0 || actionTimeSeconds <= 0)
        {
            return;
        }

        var effectiveWarningLeadSeconds = Math.Min(warningLeadSeconds, actionTimeSeconds);
        var remainingSeconds = actionTimeSeconds - elapsedSeconds;
        var warningStepSeconds = _config.RepeatWarningIntervalSecondsInt;
        if (remainingSeconds <= 0 || remainingSeconds > effectiveWarningLeadSeconds)
        {
            return;
        }

        var displayRemainingSeconds = GetCountdownRemainingSeconds(remainingSeconds, effectiveWarningLeadSeconds, warningStepSeconds);
        if (WasCountdownWarningAlreadySent(state, warningKind, displayRemainingSeconds))
        {
            return;
        }

        MarkCountdownWarningSent(state, warningKind, displayRemainingSeconds, now);
        SendWarning(player, warningKind, actionKind, displayRemainingSeconds);
    }

    private void SendWarning(CCSPlayerController player, AfkWarningKind warningKind, AfkActionKind actionKind, int remainingSeconds)
    {
        var language = GetLanguage(player);
        var nextAction = actionKind == AfkActionKind.Kick
            ? language.FormatKickAction(remainingSeconds)
            : language.FormatMoveAction(remainingSeconds);
        var message = warningKind == AfkWarningKind.Spawn
            ? language.FormatSpawnWarning(nextAction)
            : language.FormatAfkWarning(nextAction);

        TrySendPlayerMessage(player, message);
    }

    private static int GetCountdownRemainingSeconds(float remainingSeconds, float warningLeadSeconds, int warningStepSeconds)
    {
        var rounded = (int)(Math.Ceiling(Math.Max(0.0f, remainingSeconds) / warningStepSeconds) * warningStepSeconds);
        var maxWarning = Math.Max(warningStepSeconds, (int)Math.Round(warningLeadSeconds));
        return Math.Clamp(rounded, warningStepSeconds, maxWarning);
    }

    private static bool WasCountdownWarningAlreadySent(PlayerAfkState state, AfkWarningKind kind, int remainingSeconds)
    {
        return kind == AfkWarningKind.Spawn
            ? state.LastSpawnWarningRemainingSeconds == remainingSeconds
            : state.LastWarningRemainingSeconds == remainingSeconds;
    }

    private static void MarkCountdownWarningSent(PlayerAfkState state, AfkWarningKind kind, int remainingSeconds, DateTimeOffset now)
    {
        if (kind == AfkWarningKind.Spawn)
        {
            state.WasSpawnWarned = true;
            state.LastSpawnWarningAt = now;
            state.LastSpawnWarningRemainingSeconds = remainingSeconds;
            return;
        }

        state.WasWarned = true;
        state.LastWarningAt = now;
        state.LastWarningRemainingSeconds = remainingSeconds;
    }

    private void MoveToSpectator(CCSPlayerController player, PlayerAfkState state, AfkWarningKind kind)
    {
        try
        {
            if (GetTeam(player) == CsTeam.Spectator)
            {
                MarkMoveAttempted(state, kind);
                return;
            }

            MarkMoveAttempted(state, kind);
            player.ChangeTeam(CsTeam.Spectator);

            if (_config.LogActions)
            {
                _logger.LogInformation("{Prefix} Moved {PlayerName} (#{UserId}) to spectator for {Reason}.", Prefix, player.PlayerName, player.UserId, GetLogReason(kind));
            }

            if (kind != AfkWarningKind.NoTeam)
            {
                AnnounceMoveToSpectator(player.PlayerName, kind);
            }
        }
        catch (Exception exception)
        {
            if (_config.LogActions)
            {
                _logger.LogWarning(exception, "{Prefix} Failed to move {PlayerName} (#{UserId}) to spectator.", Prefix, player.PlayerName, player.UserId);
            }
        }
    }

    private static void MarkMoveAttempted(PlayerAfkState state, AfkWarningKind kind)
    {
        if (kind == AfkWarningKind.NoTeam)
        {
            state.MovedToSpectatorForNoTeam = true;
            return;
        }

        if (kind == AfkWarningKind.Spawn)
        {
            state.MovedToSpectatorForSpawn = true;
        }

        state.MovedToSpectatorForAfk = true;
    }

    private void AnnounceMoveToSpectator(string playerName, AfkWarningKind kind)
    {
        foreach (var target in Utilities.GetPlayers())
        {
            if (!target.IsValid || target.UserId is null || target.UserId < 0)
            {
                continue;
            }

            try
            {
                var language = GetLanguage(target);
                target.PrintToChat(language.FormatMovedToSpectator(playerName, kind == AfkWarningKind.Spawn));
            }
            catch (Exception exception)
            {
                if (_config.LogActions)
                {
                    _logger.LogDebug(exception, "{Prefix} Failed to send move announcement to {PlayerName}.", Prefix, target.PlayerName);
                }
            }
        }
    }

    private void KickPlayer(CCSPlayerController player, PlayerAfkState state, string reason, AfkWarningKind kind)
    {
        if (kind == AfkWarningKind.NoTeam)
        {
            state.NoTeamKickAttempted = true;
        }
        else
        {
            state.KickAttempted = true;
        }

        var userId = player.UserId;
        if (userId is null || userId < 0)
        {
            return;
        }

        var safeReason = EscapeCommandArgument(reason);
        _executeServerCommand($"kickid {userId} \"{safeReason}\"");

        if (_config.LogActions)
        {
            _logger.LogInformation("{Prefix} Kicked {PlayerName} (#{UserId}) for {Reason}.", Prefix, player.PlayerName, player.UserId, GetLogReason(kind));
        }
    }

    private PlayerAfkState GetOrCreateState(CCSPlayerController player, DateTimeOffset now)
    {
        if (!_states.TryGetValue(player.Slot, out var state))
        {
            state = new PlayerAfkState(player.Slot, player.UserId ?? -1, player.PlayerName, now);
            _states[player.Slot] = state;
            return state;
        }

        state.RefreshIdentity(player.UserId ?? -1, player.PlayerName, now);
        return state;
    }

    private static CsTeam GetTeam(CCSPlayerController player)
    {
        return player.Team;
    }

    private static bool IsNoTeam(CsTeam team)
    {
        return team == CsTeam.None;
    }

    private static bool IsPlayableTeam(CsTeam team)
    {
        return team == CsTeam.Terrorist || team == CsTeam.CounterTerrorist;
    }

    private static bool IsPlayerAlive(CCSPlayerController player)
    {
        try
        {
            return player.PawnIsAlive;
        }
        catch
        {
            return false;
        }
    }

    private bool IsValidHumanCandidate(CCSPlayerController player)
    {
        if (!player.IsValid || player.UserId is null || player.UserId < 0)
        {
            return false;
        }

        if (_config.IgnoreBots && player.IsBot)
        {
            RemovePlayer(player.Slot);
            return false;
        }

        return true;
    }

    private bool ShouldSkipForImmunity(CCSPlayerController player)
    {
        if (!_config.AdminImmunity || _config.AdminImmunityFlagsArray.Length == 0)
        {
            return false;
        }

        try
        {
            return AdminManager.PlayerHasPermissions(player, _config.AdminImmunityFlagsArray);
        }
        catch
        {
            return false;
        }
    }

    private void TrySendPlayerMessage(CCSPlayerController player, string message)
    {
        try
        {
            player.PrintToChat(message);
        }
        catch (Exception exception)
        {
            if (_config.LogActions)
            {
                _logger.LogDebug(exception, "{Prefix} Failed to send chat warning to {PlayerName}.", Prefix, player.PlayerName);
            }
        }
    }

    private void RemoveStaleStates()
    {
        _staleSlots.Clear();

        foreach (var slot in _states.Keys)
        {
            if (!_seenSlots.Contains(slot))
            {
                _staleSlots.Add(slot);
            }
        }

        foreach (var slot in _staleSlots)
        {
            _states.Remove(slot);
        }
    }

    private static string EscapeCommandArgument(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string GetLogReason(AfkWarningKind kind)
    {
        return kind switch
        {
            AfkWarningKind.NoTeam => "not choosing a team",
            AfkWarningKind.Spawn => "spawn AFK",
            _ => "AFK"
        };
    }

    private AfkLanguage GetLanguage(CCSPlayerController player)
    {
        return _languageManager.Load(_config.Language);
    }
}

public enum AfkWarningKind
{
    Normal,
    Spawn,
    NoTeam
}

public enum AfkActionKind
{
    Move,
    Kick
}
