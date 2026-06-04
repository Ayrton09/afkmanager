using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;

namespace AfkManager.Core;

public sealed class PlayerAfkState
{
    public PlayerAfkState(int slot, int userId, string playerName, DateTimeOffset now)
    {
        Slot = slot;
        UserId = userId;
        PlayerName = playerName;
        FirstSeenAt = now;
        LastActivityAt = now;
    }

    public int Slot { get; }
    public int UserId { get; private set; }
    public string PlayerName { get; private set; }
    public DateTimeOffset FirstSeenAt { get; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public DateTimeOffset? SpawnedAt { get; private set; }
    public DateTimeOffset? NoTeamSince { get; set; }
    public DateTimeOffset? LastWarningAt { get; set; }
    public DateTimeOffset? LastSpawnWarningAt { get; set; }
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
    public PositionSample LastPosition { get; private set; }
    public AngleSample LastViewAngles { get; private set; }
    public PlayerButtons LastButtons { get; private set; }

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

    public void MarkSpawned(DateTimeOffset now)
    {
        HasAliveState = true;
        IsAlive = true;
        SpawnedAt = now;
        HasActivitySinceSpawn = false;
        WasSpawnWarned = false;
        LastSpawnWarningAt = null;
        LastSpawnWarningRemainingSeconds = 0;
        MovedToSpectatorForSpawn = false;
        HasSample = false;
        ResetActivity(now);
    }

    public void MarkNotAlive(DateTimeOffset now)
    {
        HasAliveState = true;
        IsAlive = false;
        SpawnedAt = null;
        HasActivitySinceSpawn = true;
        WasSpawnWarned = false;
        LastSpawnWarningAt = null;
        LastSpawnWarningRemainingSeconds = 0;
        MovedToSpectatorForSpawn = false;
        ResetActivity(now);
    }

    public void MarkAliveObserved(DateTimeOffset now)
    {
        HasAliveState = true;
        IsAlive = true;
        ResetActivity(now);
    }

    public void ResetSpawnClock(DateTimeOffset now)
    {
        SpawnedAt = now;
        WasSpawnWarned = false;
        LastSpawnWarningAt = null;
        LastSpawnWarningRemainingSeconds = 0;
        MovedToSpectatorForSpawn = false;
    }

    public void SetSample(PositionSample position, AngleSample viewAngles, PlayerButtons buttons)
    {
        LastPosition = position;
        LastViewAngles = viewAngles;
        LastButtons = buttons;
        HasSample = true;
    }

    public void MarkActive(DateTimeOffset now, PositionSample position, AngleSample viewAngles, PlayerButtons buttons)
    {
        SetSample(position, viewAngles, buttons);
        HasActivitySinceSpawn = true;
        ResetActivity(now);
    }

    public void ResetActivity(DateTimeOffset now)
    {
        LastActivityAt = now;
        LastWarningAt = null;
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
        LastSpawnWarningAt = null;
        LastSpawnWarningRemainingSeconds = 0;
        MovedToSpectatorForSpawn = false;
    }
}

public readonly record struct PositionSample(float X, float Y, float Z)
{
    public static PositionSample FromVector(Vector vector)
    {
        return new PositionSample(vector.X, vector.Y, vector.Z);
    }

    public float DistanceSquaredTo(PositionSample other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return dx * dx + dy * dy + dz * dz;
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
