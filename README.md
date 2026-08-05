# AFK Manager

## Detection Rules

- Players are considered active only when their view angles change. The check uses a small tolerance so tiny jitter does not count as movement.
- The inactivity clock is **paused** while a player is dead, and resumes where it left off once they respawn. Waiting to respawn therefore neither accumulates AFK time nor clears it, so a player cannot reset their AFK timer just by dying every round.
- Spawn AFK tracking restarts on player spawn, on team change once the player is alive on T/CT, and at round freeze end for alive T/CT players. This window is separate from the inactivity clock above.
- The view angle baseline is taken on the first check after spawning rather than at the spawn event itself, because pawn angles at spawn time may not reflect the spawn point yet.
- Spectators are tracked through their observer pawn, so camera movement counts as activity. If the plugin moved someone to spectator, moving the camera again cancels a pending kick.
- Spectators are ignored by default with `ignore_spectators`. This does not ignore players who are still in team selection.
- Players with no selected team are tracked separately by time spent in the no-team/team-selection state. They are not treated as spectators and are not exempted by `ignore_spectators`.
- Admins matching `admin_immunity_flags` are skipped when `admin_immunity` is enabled. If the admin lookup fails, the player is treated as **not** immune so a broken admin config cannot silently disable AFK checks.

## Performance

- The plugin checks players on `check_interval_seconds`; it does not run AFK detection every server tick.
- The default interval is 5 seconds. For very full public servers, 5-10 seconds is a good practical range.
- The periodic check reuses its internal slot collections and caches admin immunity flags.
- Language files are parsed on plugin load and on config reload, and the resolved language is then served from a field, so formatting a warning does not re-resolve it.
- Logging is disabled by default. If `log_actions` is enabled, only real actions such as move/kick attempts are logged; repeated warnings are not logged.

## Language

The plugin uses the language configured in CounterStrikeSharp `ServerLanguage`.

Example: if CounterStrikeSharp is configured with `ServerLanguage` set to `es`, AFK Manager uses `Lang/es.json`.

A region-specific value falls back to its neutral language (`pt-br` uses `pt.json`), and an unknown value falls back to `en.json`. Every fallback is written to the server log, so a typo in `ServerLanguage` is visible instead of silently serving English.

Language files live in:

```text
AfkManager/Lang/
```

Included files:

- `de.json`
- `es.json`
- `en.json`
- `fr.json`
- `pt.json`
- `ru.json`
- `zh.json`

Warnings and kick reasons are loaded from the language file. Warnings are chat-only.

## Action Flow

All thresholds are seconds. Set a threshold to `0` to disable that stage.

For team players:

1. `spawn_warning_time_seconds`: how many seconds before the spawn move action countdown warnings start.
2. `spawn_move_to_spectator_time_seconds`: moves a player to spectator if they spawned and never moved their view.
3. After the player has moved their view once, `warning_time_seconds` controls how many seconds before the normal move/kick action countdown warnings start.
4. `move_to_spectator_time_seconds`: moves the player to spectator once.
5. `kick_time_seconds`: kicks the player once with the language file's `kick_reason`. If the plugin already moved the player to spectator, the kick timer can still finish without sending spectator warning spam. Default is `0`, disabled.

For players who have not chosen a team:

1. `no_team_move_to_spectator_time_seconds`: moves them to spectator if they do not choose a team. Default is `10`.
2. `no_team_kick_time_seconds`: kicks them with `no_team_kick_reason`. If `no_team_move_to_spectator_time_seconds` moved them first, the kick timer can still finish. Default is `0`, disabled.

Countdown warning steps are controlled by `repeat_warning_interval_seconds`.
Example: if `spawn_move_to_spectator_time_seconds` is `15`, `spawn_warning_time_seconds` is `10`, and `repeat_warning_interval_seconds` is `5`, the player is warned at 10 seconds remaining and 5 seconds remaining.
If a warning time is higher than the action time, it is capped internally to the action time.

## Project Structure

```text
afkmanager/
  AfkManager.csproj
  AfkManagerPlugin.cs
  Config/
    AfkManagerConfig.cs
  Core/
    AfkService.cs
    PlayerAfkState.cs
  Localization/
    AfkLanguage.cs
    AfkLanguageManager.cs
    ChatText.cs
  Tests/
    AfkManager.Tests.csproj
    Program.cs
  Lang/
    de.json
    es.json
    en.json
    fr.json
    pt.json
    ru.json
    zh.json
  Samples/
    afkmanager.example.json
  README.md
```

## Install

1. Create a plugin folder on the server:

```text
game/csgo/addons/counterstrikesharp/plugins/afkmanager/
```

2. Copy the release output files into that folder.
3. Start the server once so CounterStrikeSharp can generate the config, or copy `Samples/afkmanager.example.json` into the plugin config location used by your CounterStrikeSharp install.
4. Adjust thresholds in CounterStrikeSharp's generated plugin config and reload the plugin or run `css_afk_reload`.

The active config is normally generated by CounterStrikeSharp under:

```text
game/csgo/addons/counterstrikesharp/configs/plugins/afkmanager/afkmanager.json
```

## Commands

- `css_afk_reload`: reload config. Requires `@css/config`.
- `css_afk_config`: print active runtime config values. Requires `@css/config`.
- `css_afk_enabled`: print runtime enabled state. Requires `@css/config`.
- `css_afk_enabled <0|1>`: disable or enable AFK Manager until config reload/map/plugin reload. Requires `@css/config`.
- `css_afk_status`: print status for all players. Requires `@css/generic`.
- `css_afk_status <name|#userid>`: print status for matching players.
- `css_afk_reset`: reset your AFK timer. Requires `@css/kick`.
- `css_afk_reset <name|#userid>`: reset a target timer.

`css_afk_reset` clears AFK enforcement for its target, so it is gated behind `@css/kick` rather than `@css/generic` to keep it from being used as a self-exemption bind.

## Tests

The AFK clock state machine has behavioural tests covering round restarts, death pauses, and reconnects. They are plain assertions with no test framework dependency, and run in CI.

```bash
dotnet run --project Tests
```

## Notes

- No team balancing logic is included.
- Bot, spectator, and admin immunity behavior are configurable.
- State is cleared on map end, map start, disconnect, plugin unload, and reconnect identity changes.
- Kicks use the CounterStrikeSharp typed disconnect API rather than a `kickid` console string, so a language file can never inject console commands.
- Player names are stripped of control characters before being printed to chat, so a crafted name cannot recolour or spoof the move announcement.

## Upgrading from 1.0.x

- `css_afk_reset` now requires `@css/kick` instead of `@css/generic`.
- The unused `action_moved_soon`, `action_kicked_soon`, and `action_punished_soon` keys were removed from the language files. Leaving them in a custom language file is harmless; they are ignored.
- The plugin config schema is unchanged, so existing `afkmanager.json` files keep working.
- `move_to_spectator_time_seconds` and `kick_time_seconds` now actually accumulate across rounds. If you previously set them low because they never seemed to fire, re-check those values before deploying.
