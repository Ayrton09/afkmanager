<div align="center">

# AFK Manager

**Warns, moves to spectator and kicks AFK players in Counter-Strike 2.**

[![Release](https://img.shields.io/github/v/release/Ayrton09/afkmanager?style=flat-square&color=success)](https://github.com/Ayrton09/afkmanager/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/Ayrton09/afkmanager/build.yml?branch=main&style=flat-square&label=build)](https://github.com/Ayrton09/afkmanager/actions/workflows/build.yml)
[![Downloads](https://img.shields.io/github/downloads/Ayrton09/afkmanager/total?style=flat-square&color=brightgreen)](https://github.com/Ayrton09/afkmanager/releases)
[![License](https://img.shields.io/github/license/Ayrton09/afkmanager?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![CounterStrikeSharp](https://img.shields.io/badge/CounterStrikeSharp-%E2%89%A5%201.0.380-F5834A?style=flat-square)](https://docs.cssharp.dev/)

</div>

---

Players who stop moving their view get countdown warnings in chat, are moved to spectator, and can optionally be kicked. Players who sit at the spawn without moving and players who never pick a team are handled on their own, shorter timers. Freeze time, match pauses and timeouts never count as AFK time.

## Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Configuration](#configuration)
- [Commands](#commands)
- [How Detection Works](#how-detection-works)
- [Languages](#languages)
- [Building](#building)
- [Upgrading from 1.1.x](#upgrading-from-11x)
- [Upgrading from 1.0.x](#upgrading-from-10x)
- [License](#license)

## Features

- Countdown warnings in chat before every action
- Move to spectator for idle players, with an optional kick afterwards
- A separate, shorter spawn AFK window for players who never move after spawning
- Players stuck in team selection are moved to spectator and optionally kicked
- The AFK clock pauses while a player is dead, so dying every round does not reset it
- Clocks stop during freeze time, including `mp_pause_match` pauses and timeouts
- Optional immunity for admins by permission flag
- Bots, SourceTV and spectators can be left out
- Seven languages, following CounterStrikeSharp's `ServerLanguage`

## Requirements

- CounterStrikeSharp **1.0.380** or newer (and the Metamod:Source build it requires)

## Installation

**1.** Download the latest ZIP from [Releases](https://github.com/Ayrton09/afkmanager/releases).

**2.** Extract the `afkmanager` folder into `game/csgo/addons/counterstrikesharp/plugins/`. The resulting layout:

```text
game/csgo/addons/counterstrikesharp/plugins/afkmanager/
  afkmanager.dll
  Lang/
    de.json  en.json  es.json  fr.json  pt.json  ru.json  zh.json
```

**3.** Start the server. CounterStrikeSharp generates the config at:

```text
game/csgo/addons/counterstrikesharp/configs/plugins/afkmanager/afkmanager.json
```

`Samples/afkmanager.example.json` has the same defaults if you prefer to create it by hand.

**4.** Adjust the values and run `css_afk_reload`.

## Configuration

All times are in seconds. Set an action time to `0` to turn that action off.

| Option | Default | Description |
| --- | --- | --- |
| `enabled` | `true` | Turns the plugin on or off |
| `check_interval_seconds` | `5` | How often players are checked (1 to 60). 5 to 10 is a good range for full servers |
| `spawn_warning_time_seconds` | `10` | Spawn AFK warnings start this many seconds before the move |
| `spawn_move_to_spectator_time_seconds` | `15` | Moves a player who spawned and never moved their view |
| `warning_time_seconds` | `30` | Regular warnings start this many seconds before the move or kick |
| `move_to_spectator_time_seconds` | `90` | Moves an idle player to spectator |
| `kick_time_seconds` | `0` | Kicks an idle player. Counts total idle time, so it should be higher than the move time |
| `no_team_move_to_spectator_time_seconds` | `10` | Moves a player who has not picked a team |
| `no_team_kick_time_seconds` | `0` | Kicks a player who has not picked a team |
| `repeat_warning_interval_seconds` | `5` | Step between countdown warnings. `0` turns warnings off |
| `ignore_bots` | `true` | Leaves bots out. SourceTV is always left out |
| `ignore_spectators` | `true` | Leaves spectators out, unless the plugin moved them there and a kick is pending. Players in team selection are still tracked |
| `admin_immunity` | `false` | Skips players who have every flag in `admin_immunity_flags` |
| `admin_immunity_flags` | `["@css/generic"]` | Permission flags from `configs/admins.json`. A player needs all of them to be immune |
| `log_actions` | `false` | Logs every move and kick to the server log. Warnings are never logged |

Countdown example: with `spawn_move_to_spectator_time_seconds` at `15`, `spawn_warning_time_seconds` at `10` and `repeat_warning_interval_seconds` at `5`, the player is warned with 10 and 5 seconds left. A warning time higher than its action time is capped to the action time.

The kick reason from the language file is sent to the player in chat right before the kick. The disconnect itself uses the engine's idle kick reason.

## Commands

| Command | Permission | Description |
| --- | --- | --- |
| `css_afk_reload` | `@css/config` | Reloads the config and the language file |
| `css_afk_config` | `@css/config` | Prints the active values |
| `css_afk_enabled [0\|1]` | `@css/config` | Prints or changes the enabled state |
| `css_afk_status [name\|#userid]` | `@css/generic` | Prints the AFK state of every player, or of the matching ones. Players without tracking show as not tracked |

`css_afk_enabled` lasts until the next config or plugin reload; map changes keep it. Switching it clears all tracking, so nothing that happened while the plugin was off is acted on later.

## How Detection Works

- A player counts as active only when their view angles change, with a small tolerance so jitter does not count.
- The idle clock pauses while the player is dead and resumes on respawn. Waiting to respawn neither adds AFK time nor clears it.
- The spawn AFK window opens on spawn, at the end of freeze time for alive T/CT players, and when an alive player changes team. Its baseline is taken on the first check after it opens, because the view at the spawn event may not match the spawn point yet.
- Every clock stops during freeze time, match pauses and timeouts, so buying during freeze time or waiting out a pause never counts.
- Spectators are tracked through their observer view. If the plugin moved someone to spectator, moving the view cancels the pending kick.
- Joining T or CT from spectator or team selection clears accumulated idle time. T/CT swaps do not, since they are usually auto-balance.
- If the admin lookup fails, the player is treated as not immune, so a broken admin config cannot turn AFK checks off.
- Tracking is cleared on map change, disconnect, reconnect on the same slot, plugin unload and whenever the enabled state changes.
- Kicks use CounterStrikeSharp's typed disconnect instead of a `kickid` console string, so a language file cannot inject console commands. Player names are stripped of control characters and text direction overrides before they reach chat or the log, so a crafted name cannot fake colours or extra lines.

## Languages

The language follows CounterStrikeSharp's `ServerLanguage`: with `es`, the plugin uses `Lang/es.json`. A regional value falls back to its base language (`pt-br` uses `pt.json`) and an unknown one falls back to `en.json`. Every fallback is written to the server log, so a typo in `ServerLanguage` does not go unnoticed.

Included: German (`de`), English (`en`), Spanish (`es`), French (`fr`), Portuguese (`pt`), Russian (`ru`) and Chinese (`zh`). Edit the files in `afkmanager/Lang/` to change the chat messages, then run `css_afk_reload`. A key missing from a file uses the built-in English text. Replies to the admin commands are always in English.

## Building

```bash
dotnet build AfkManager.csproj -c Release
```

The plugin is written to `bin/Release/net10.0/`.

## Upgrading from 1.1.x

- CounterStrikeSharp 1.0.380 or newer is now required.
- Freeze time, match pauses and timeouts no longer count as AFK time, so on servers with long freeze times players are moved a little later than before.
- `css_afk_enabled` and a config reload that changes `enabled` now clear all tracking, and nothing is tracked while the plugin is off.
- The config schema is unchanged, so existing `afkmanager.json` files keep working.

## Upgrading from 1.0.x

- `css_afk_reset` was removed: it could be bound as a self exemption. Use `admin_immunity` instead.
- The unused `action_moved_soon`, `action_kicked_soon` and `action_punished_soon` language keys were removed. Leaving them in a custom file is harmless.
- `move_to_spectator_time_seconds` and `kick_time_seconds` now accumulate across rounds. If you set them low because they never seemed to fire, check them again.

## License

Released under the [MIT License](LICENSE).
