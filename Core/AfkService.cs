using AfkManager.Config;
using AfkManager.Localization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Logging;

namespace AfkManager.Core;

public sealed class AfkService
{
    private const string Prefix = "[afkmanager]";
    private const float ViewAngleToleranceDegrees = 3.0f;
    private readonly ILogger _logger;
    private readonly Dictionary<int, PlayerAfkState> _states = [];
    private readonly HashSet<int> _seenSlots = [];
    private readonly List<int> _staleSlots = [];
    private readonly AfkLanguageManager _languageManager;
    private AfkManagerConfig _config;

    public AfkService(AfkManagerConfig config, AfkLanguageManager languageManager, ILogger logger)
    {
        _config = config;
        _languageManager = languageManager;
        _logger = logger;
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

    /// <summary>
    /// Joining a playable team is a deliberate action, so it clears accumulated inactivity. The
    /// plugin only ever moves players to spectator and never onto T/CT, so this cannot be
    /// triggered by its own enforcement and cancel a pending kick.
    /// </summary>
    public void MarkPlayerReturnedToTeam(CCSPlayerController player)
    {
        if (!IsValidHumanCandidate(player))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        GetOrCreateState(player, now).ResetActivity(now);
    }

    /// <summary>
    /// Opens a new spawn-AFK window. No view angle sample is taken here on purpose: at spawn time
    /// the pawn angles may not reflect the spawn point yet, and a stale sample would register as
    /// movement on the next check and defeat spawn-AFK detection. The first check tick establishes
    /// the baseline and restarts the spawn clock.
    /// </summary>
    public void MarkPlayerSpawned(CCSPlayerController player)
    {
        if (!IsValidHumanCandidate(player))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        GetOrCreateState(player, now).MarkSpawned(now);
    }

    public void MarkPlayerSpawnedIfAlive(CCSPlayerController player)
    {
        if (!IsValidHumanCandidate(player) || !IsPlayableTeam(player.Team) || !IsPlayerAlive(player))
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
        GetOrCreateState(player, now).MarkNotAlive(now);
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
        var team = player.Team;
        var status = IsNoTeam(team)
            ? $"no-team {(int)state.GetNoTeamSeconds(now)}s"
            : state.SpawnedAt is not null && !state.HasActivitySinceSpawn
                ? $"spawn-afk {(int)state.GetSpawnAfkSeconds(now)}s"
                : $"inactive {(int)state.GetInactiveSeconds(now)}s{(state.DeadSince is null ? string.Empty : " (paused, dead)")}";

        return $"#{player.UserId} {ChatText.SanitizeName(player.PlayerName)} team={team} {status} "
            + $"warned={state.WasWarned || state.WasSpawnWarned} "
            + $"moved={state.MovedToSpectatorForAfk || state.MovedToSpectatorForSpawn || state.MovedToSpectatorForNoTeam} "
            + $"kick_attempted={state.KickAttempted || state.NoTeamKickAttempted}";
    }

    public List<CCSPlayerController> FindPlayers(string target)
    {
        var query = target.Trim();
        var matches = new List<CCSPlayerController>();
        var userId = 0;
        var byUserId = query.StartsWith('#') && int.TryParse(query[1..], out userId);

        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsValidHumanCandidate(player))
            {
                continue;
            }

            var isMatch = byUserId
                ? player.UserId == userId
                : player.PlayerName.Contains(query, StringComparison.OrdinalIgnoreCase);

            if (isMatch)
            {
                matches.Add(player);
            }
        }

        return matches;
    }

    private void CheckPlayer(CCSPlayerController player, DateTimeOffset now)
    {
        var state = GetOrCreateState(player, now);
        var team = player.Team;

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
            state.ResetSpawnTracking();

            // Spectators are judged on camera movement, not on respawn state, so the inactivity
            // clock must not stay paused from the death that put them here.
            state.ResumeActivityClock(now);

            // Sampled before acting so a spectator who starts moving again clears a pending kick.
            UpdateActivitySample(player, state, now);

            if (HandleSpectatorPlayer(player, state, now))
            {
                return;
            }

            if (_config.IgnoreSpectators)
            {
                state.ResetAllTracking(now);
                return;
            }

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

        var spawnAfkSeconds = state.GetSpawnAfkSeconds(now);

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
        var inactiveSeconds = state.GetInactiveSeconds(now);

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
            KickPlayer(player, state, AfkWarningKind.Normal);
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
        var noTeamSeconds = state.GetNoTeamSeconds(now);

        if (_config.IsNoTeamMoveEnabled
            && !state.MovedToSpectatorForNoTeam
            && noTeamSeconds >= _config.NoTeamMoveToSpectatorTimeSeconds)
        {
            MoveToSpectator(player, state, AfkWarningKind.NoTeam);
            return;
        }

        if (_config.IsNoTeamKickEnabled && !state.NoTeamKickAttempted && noTeamSeconds >= _config.NoTeamKickTimeSeconds)
        {
            KickPlayer(player, state, AfkWarningKind.NoTeam);
        }
    }

    private bool HandleSpectatorPlayer(CCSPlayerController player, PlayerAfkState state, DateTimeOffset now)
    {
        if (state.MovedToSpectatorForAfk && _config.IsKickEnabled && !state.KickAttempted)
        {
            if (state.GetInactiveSeconds(now) >= _config.KickTimeSeconds)
            {
                KickPlayer(player, state, AfkWarningKind.Normal);
            }

            return true;
        }

        if (state.MovedToSpectatorForNoTeam && _config.IsNoTeamKickEnabled && !state.NoTeamKickAttempted)
        {
            if (state.GetNoTeamSeconds(now) >= _config.NoTeamKickTimeSeconds)
            {
                KickPlayer(player, state, AfkWarningKind.NoTeam);
            }

            return true;
        }

        return false;
    }

    private void UpdateActivitySample(CCSPlayerController player, PlayerAfkState state, DateTimeOffset now)
    {
        if (!TryReadViewAngles(player, out var viewAngles, out var fromObserverPawn))
        {
            return;
        }

        // A source change (player pawn <-> observer pawn) yields angles that are not comparable to
        // the previous sample, so re-baseline instead of reading the switch itself as movement.
        // Without this, being moved to spectator would immediately look like activity and clear the
        // enforcement flags that a follow-up kick depends on.
        if (!state.HasSample || state.SampleFromObserverPawn != fromObserverPawn)
        {
            state.SetSample(viewAngles, fromObserverPawn);
            if (state.SpawnedAt is not null && !state.HasActivitySinceSpawn)
            {
                state.ResetSpawnClock(now);
            }

            return;
        }

        if (viewAngles.DifferenceTo(state.LastViewAngles) > ViewAngleToleranceDegrees)
        {
            state.MarkActive(now, viewAngles, fromObserverPawn);
        }
    }

    private static bool TryReadViewAngles(CCSPlayerController player, out AngleSample viewAngles, out bool fromObserverPawn)
    {
        viewAngles = default;
        fromObserverPawn = false;

        var playerPawn = player.PlayerPawn.Value;
        if (playerPawn is not null && playerPawn.IsValid)
        {
            var eyeAngles = playerPawn.EyeAngles;
            if (eyeAngles is not null)
            {
                viewAngles = AngleSample.FromQAngle(eyeAngles);
                return true;
            }
        }

        // Spectators have no player pawn. v_angle lives on the shared CBasePlayerPawn base and is
        // populated for the observer pawn, so camera movement is still detected.
        var pawn = player.Pawn.Value;
        if (pawn is null || !pawn.IsValid)
        {
            return false;
        }

        var angles = pawn.V_angle;
        if (angles is null)
        {
            return false;
        }

        viewAngles = AngleSample.FromQAngle(angles);
        fromObserverPawn = true;
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

        MarkCountdownWarningSent(state, warningKind, displayRemainingSeconds);
        SendWarning(player, warningKind, actionKind, displayRemainingSeconds);
    }

    private void SendWarning(CCSPlayerController player, AfkWarningKind warningKind, AfkActionKind actionKind, int remainingSeconds)
    {
        var language = Language;
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

    private static void MarkCountdownWarningSent(PlayerAfkState state, AfkWarningKind kind, int remainingSeconds)
    {
        if (kind == AfkWarningKind.Spawn)
        {
            state.WasSpawnWarned = true;
            state.LastSpawnWarningRemainingSeconds = remainingSeconds;
            return;
        }

        state.WasWarned = true;
        state.LastWarningRemainingSeconds = remainingSeconds;
    }

    private void MoveToSpectator(CCSPlayerController player, PlayerAfkState state, AfkWarningKind kind)
    {
        try
        {
            if (player.Team == CsTeam.Spectator)
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
        var message = Language.FormatMovedToSpectator(playerName, kind == AfkWarningKind.Spawn);

        foreach (var target in Utilities.GetPlayers())
        {
            if (!target.IsValid || target.UserId is null || target.UserId < 0)
            {
                continue;
            }

            TrySendPlayerMessage(target, message);
        }
    }

    private void KickPlayer(CCSPlayerController player, PlayerAfkState state, AfkWarningKind kind)
    {
        if (kind == AfkWarningKind.NoTeam)
        {
            state.NoTeamKickAttempted = true;
        }
        else
        {
            state.KickAttempted = true;
        }

        var language = Language;
        var reason = kind == AfkWarningKind.NoTeam ? language.NoTeamKickReason : language.KickReason;
        TrySendPlayerMessage(player, language.FormatKickNotice(reason));

        try
        {
            // Typed disconnect instead of building a "kickid <id> <reason>" console string: the
            // Source console tokenizer does not honour backslash escaping, so no amount of quoting
            // makes an arbitrary reason string safe to concatenate into a command.
            player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED_IDLE);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "{Prefix} Failed to kick {PlayerName} (#{UserId}).", Prefix, player.PlayerName, player.UserId);
            return;
        }

        if (_config.LogActions)
        {
            _logger.LogInformation("{Prefix} Kicked {PlayerName} (#{UserId}) for {Reason} ({KickReason}).", Prefix, player.PlayerName, player.UserId, GetLogReason(kind), reason);
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
            // Reading through a pawn handle that the engine has already torn down. Treat as dead;
            // this runs for every player on every check tick, so it must not log.
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
        catch (Exception exception)
        {
            // Fail towards "not immune" so a broken admin config cannot silently disable AFK checks.
            _logger.LogDebug(exception, "{Prefix} Admin immunity check failed for {PlayerName}.", Prefix, player.PlayerName);
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
                _logger.LogDebug(exception, "{Prefix} Failed to send chat message to {PlayerName}.", Prefix, player.PlayerName);
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

    private static string GetLogReason(AfkWarningKind kind)
    {
        return kind switch
        {
            AfkWarningKind.NoTeam => "not choosing a team",
            AfkWarningKind.Spawn => "spawn AFK",
            _ => "AFK"
        };
    }

    private AfkLanguage Language => _languageManager.LoadCounterStrikeSharpLanguage();
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
