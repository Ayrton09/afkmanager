using CounterStrikeSharp.API.Modules.Utils;

namespace AfkManager.Core;

public sealed class PlayerAfkState
{
    public PlayerAfkState(int slot, int userId, string playerName, DateTimeOffset now)
    {
        Slot = slot;
        UserId = userId;
        PlayerName = playerName;
        LastActivityAt = now;
    }

    public int Slot { get; }
    public int UserId { get; private set; }
    public string PlayerName { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public DateTimeOffset? SpawnedAt { get; private set; }
    public DateTimeOffset? DeadSince { get; private set; }
    public DateTimeOffset? NoTeamSince { get; set; }
    public int LastWarningRemainingSeconds { get; set; }
    public int LastSpawnWarningRemainingSeconds { get; set; }
    public bool WasWarned { get; set; }
    public bool WasSpawnWarned { get; set; }
    public bool HasActivitySinceSpawn { get; private set; } = true;
    public bool MovedToSpectatorForSpawn { get; set; }
    public bool MovedToSpectatorForAfk { get; set; }
    public bool MovedToSpectatorForNoTeam { get; set; }
    public bool KickAttempted { get; set; }
    public bool NoTeamKickAttempted { get; set; }
    public bool HasSample { get; private set; }
    public bool HasAliveState { get; private set; }
    public bool IsAlive { get; private set; }
    public AngleSample LastViewAngles { get; private set; }

    /// <summary>
    /// Which pawn the last sample came from. Player pawn and observer pawn angles are not
    /// comparable, so a source change must re-baseline rather than read as movement.
    /// </summary>
    public bool SampleFromObserverPawn { get; private set; }

    /// <summary>
    /// Seconds since the player last moved their view. The clock is paused while the player is
    /// dead so that waiting to respawn neither accumulates nor clears inactivity.
    /// </summary>
    public float GetInactiveSeconds(DateTimeOffset now)
    {
        var reference = DeadSince ?? now;
        return reference <= LastActivityAt ? 0.0f : (float)(reference - LastActivityAt).TotalSeconds;
    }

    public float GetSpawnAfkSeconds(DateTimeOffset now)
    {
        return SpawnedAt is null || now <= SpawnedAt.Value
            ? 0.0f
            : (float)(now - SpawnedAt.Value).TotalSeconds;
    }

    public float GetNoTeamSeconds(DateTimeOffset now)
    {
        return NoTeamSince is null || now <= NoTeamSince.Value
            ? 0.0f
            : (float)(now - NoTeamSince.Value).TotalSeconds;
    }

    public void RefreshIdentity(int userId, string playerName, DateTimeOffset now)
    {
        if (UserId == userId)
        {
            PlayerName = playerName;
            return;
        }

        UserId = userId;
        PlayerName = playerName;
        ResetAllTracking(now);
        HasAliveState = false;
        IsAlive = false;
        HasSample = false;
    }

    /// <summary>
    /// Starts a new spawn-AFK window. The inactivity clock is deliberately left untouched so a
    /// player cannot clear accumulated AFK time simply by respawning every round.
    /// </summary>
    public void MarkSpawned(DateTimeOffset now)
    {
        HasAliveState = true;
        IsAlive = true;
        ResumeActivityClock(now);
        SpawnedAt = now;
        HasActivitySinceSpawn = false;
        HasSample = false;
        WasSpawnWarned = false;
        LastSpawnWarningRemainingSeconds = 0;
        MovedToSpectatorForSpawn = false;
    }

    public void MarkNotAlive(DateTimeOffset now)
    {
        HasAliveState = true;
        IsAlive = false;
        DeadSince ??= now;
        ResetSpawnTracking();
    }

    public void MarkAliveObserved(DateTimeOffset now)
    {
        HasAliveState = true;
        IsAlive = true;
        ResumeActivityClock(now);
    }

    public void ResetSpawnClock(DateTimeOffset now)
    {
        SpawnedAt = now;
        WasSpawnWarned = false;
        LastSpawnWarningRemainingSeconds = 0;
        MovedToSpectatorForSpawn = false;
    }

    public void SetSample(AngleSample viewAngles, bool fromObserverPawn)
    {
        LastViewAngles = viewAngles;
        SampleFromObserverPawn = fromObserverPawn;
        HasSample = true;
    }

    public void MarkActive(DateTimeOffset now, AngleSample viewAngles, bool fromObserverPawn)
    {
        SetSample(viewAngles, fromObserverPawn);
        HasActivitySinceSpawn = true;
        ResetActivity(now);
    }

    public void ResetActivity(DateTimeOffset now)
    {
        LastActivityAt = now;
        DeadSince = null;
        LastWarningRemainingSeconds = 0;
        WasWarned = false;
        MovedToSpectatorForAfk = false;
        KickAttempted = false;
    }

    public void ResetAllTracking(DateTimeOffset now)
    {
        ResetActivity(now);
        ResetNoTeamTracking();
        ResetSpawnTracking();
    }

    public void ResetNoTeamTracking()
    {
        NoTeamSince = null;
        MovedToSpectatorForNoTeam = false;
        NoTeamKickAttempted = false;
    }

    public void ResetSpawnTracking()
    {
        SpawnedAt = null;
        HasActivitySinceSpawn = true;
        WasSpawnWarned = false;
        LastSpawnWarningRemainingSeconds = 0;
        MovedToSpectatorForSpawn = false;
    }

    /// <summary>
    /// Rolls the inactivity clock forward by the time spent dead so the paused window is not
    /// retroactively counted as inactivity once the player is alive (or spectating) again.
    /// </summary>
    public void ResumeActivityClock(DateTimeOffset now)
    {
        if (DeadSince is null)
        {
            return;
        }

        var pausedFor = now - DeadSince.Value;
        DeadSince = null;

        if (pausedFor <= TimeSpan.Zero)
        {
            return;
        }

        LastActivityAt += pausedFor;

        if (LastActivityAt > now)
        {
            LastActivityAt = now;
        }
    }
}

public readonly record struct AngleSample(float X, float Y, float Z)
{
    public static AngleSample FromQAngle(QAngle angle)
    {
        return new AngleSample(angle.X, angle.Y, angle.Z);
    }

    public float DifferenceTo(AngleSample other)
    {
        return Math.Abs(NormalizedDelta(X, other.X))
            + Math.Abs(NormalizedDelta(Y, other.Y))
            + Math.Abs(NormalizedDelta(Z, other.Z));
    }

    private static float NormalizedDelta(float left, float right)
    {
        var delta = left - right;

        while (delta > 180.0f)
        {
            delta -= 360.0f;
        }

        while (delta < -180.0f)
        {
            delta += 360.0f;
        }

        return delta;
    }
}
