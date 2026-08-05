// Behavioural tests for the AFK clock state machine. Run with: dotnet run --project Tests
// Exits non-zero on failure so CI fails the build.

using AfkManager.Core;

var t0 = DateTimeOffset.UnixEpoch;
DateTimeOffset At(double s) => t0.AddSeconds(s);
int fails = 0;
void Check(string label, float actual, float expected)
{
    var ok = Math.Abs(actual - expected) < 0.01f;
    if (!ok) fails++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}: got {actual:0.##}s, expected {expected:0.##}s");
}

Console.WriteLine("Scenario 1: idle player survives round restarts (the round-reset bug)");
{
    var s = new PlayerAfkState(0, 1, "idle", At(0));
    s.MarkSpawned(At(0));
    s.MarkActive(At(10), new AngleSample(1, 1, 1), false);   // moves once, then goes idle
    Check("inactive at t=60", s.GetInactiveSeconds(At(60)), 50);
    s.MarkNotAlive(At(90));                            // dies at t=90 -> clock pauses at 80
    Check("inactive while dead t=100", s.GetInactiveSeconds(At(100)), 80);
    Check("inactive while dead t=110", s.GetInactiveSeconds(At(110)), 80);
    s.MarkSpawned(At(115));                            // new round: must NOT reset the clock
    Check("inactive after respawn t=115", s.GetInactiveSeconds(At(115)), 80);
    Check("inactive at t=125", s.GetInactiveSeconds(At(125)), 90);
    Console.WriteLine($"  -> reaches the 90s move threshold: {s.GetInactiveSeconds(At(125)) >= 90}");
    if (s.GetInactiveSeconds(At(125)) < 90) fails++;
}

Console.WriteLine("Scenario 2: dead time is never counted as inactivity");
{
    var s = new PlayerAfkState(0, 1, "dead", At(0));
    s.MarkSpawned(At(0));
    s.MarkActive(At(10), new AngleSample(1, 1, 1), false);
    s.MarkNotAlive(At(20));                            // 10s inactive, then dead for 300s
    s.MarkNotAlive(At(100));                           // repeated ticks while dead must be idempotent
    s.MarkNotAlive(At(200));
    s.MarkSpawned(At(320));
    Check("inactive after 300s dead", s.GetInactiveSeconds(At(320)), 10);
}

Console.WriteLine("Scenario 3: real movement still clears everything");
{
    var s = new PlayerAfkState(0, 1, "active", At(0));
    s.MarkSpawned(At(0));
    s.MovedToSpectatorForAfk = true;
    s.KickAttempted = true;
    s.MarkActive(At(50), new AngleSample(5, 5, 5), false);
    Check("inactive after moving", s.GetInactiveSeconds(At(50)), 0);
    Console.WriteLine($"  [{(!s.MovedToSpectatorForAfk && !s.KickAttempted ? "PASS" : "FAIL")}] punishment flags cleared");
    if (s.MovedToSpectatorForAfk || s.KickAttempted) fails++;
}

Console.WriteLine("Scenario 3b: switching pawn source is not movement");
{
    // Being moved to spectator swaps the angle source from player pawn to observer pawn. The two
    // are not comparable, so the switch must not clear the flags a follow-up kick depends on.
    var s = new PlayerAfkState(0, 1, "moved", At(0));
    s.MarkSpawned(At(0));
    s.SetSample(new AngleSample(10, 20, 0), false);   // alive, player pawn
    s.MovedToSpectatorForAfk = true;
    var sourceChanged = s.SampleFromObserverPawn != true;
    Console.WriteLine($"  [{(sourceChanged ? "PASS" : "FAIL")}] source change is detectable");
    if (!sourceChanged) fails++;
    s.SetSample(new AngleSample(-170, 95, 0), true);  // observer pawn, wildly different angles
    Console.WriteLine($"  [{(s.MovedToSpectatorForAfk ? "PASS" : "FAIL")}] re-baselining kept the pending kick");
    if (!s.MovedToSpectatorForAfk) fails++;
}

Console.WriteLine("Scenario 4: spawn window restarts each round, independent of the idle clock");
{
    var s = new PlayerAfkState(0, 1, "spawn", At(0));
    s.MarkSpawned(At(0));
    Console.WriteLine($"  [{(!s.HasActivitySinceSpawn && !s.HasSample ? "PASS" : "FAIL")}] fresh spawn window, no pre-seeded sample");
    if (s.HasActivitySinceSpawn || s.HasSample) fails++;
    Check("spawn-afk seconds at t=15", s.GetSpawnAfkSeconds(At(15)), 15);
    s.MarkSpawned(At(100));
    Check("spawn-afk resets next round", s.GetSpawnAfkSeconds(At(105)), 5);
}

Console.WriteLine("Scenario 4b: rejoining a team clears accumulated inactivity");
{
    // Pausing instead of resetting means a player returning from spectator would otherwise carry
    // their old AFK time into the new life and could be actioned before they can move.
    var s = new PlayerAfkState(0, 1, "returned", At(0));
    s.MarkSpawned(At(0));
    s.MarkActive(At(10), new AngleSample(1, 1, 1), false);
    Check("inactive before rejoining", s.GetInactiveSeconds(At(200)), 190);
    s.ResetActivity(At(200));                          // what MarkPlayerReturnedToTeam does
    Check("inactive after rejoining", s.GetInactiveSeconds(At(200)), 0);
}

Console.WriteLine("Scenario 5: reconnect on the same slot wipes state");
{
    var s = new PlayerAfkState(3, 1, "old", At(0));
    s.MarkSpawned(At(0));
    s.MarkNotAlive(At(50));
    s.RefreshIdentity(2, "new", At(60));
    Check("inactive reset for new user id", s.GetInactiveSeconds(At(60)), 0);
    Console.WriteLine($"  [{(s.PlayerName == "new" ? "PASS" : "FAIL")}] identity swapped");
    if (s.PlayerName != "new") fails++;
}

Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAILURES");
return fails == 0 ? 0 : 1;
