using AfkManager.Config;
using AfkManager.Core;
using AfkManager.Localization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace AfkManager;

[MinimumApiVersion(369)]
public sealed class AfkManagerPlugin : BasePlugin, IPluginConfig<AfkManagerConfig>
{
    private const string Prefix = "[afkmanager]";
    private AfkService? _service;
    private AfkLanguageManager? _languageManager;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _checkTimer;

    public override string ModuleName => "afkmanager";
    public override string ModuleVersion => "1.1.1";
    public override string ModuleAuthor => "Ayrton09";
    public override string ModuleDescription => "Warns, moves to spectator, and kicks AFK players.";

    public AfkManagerConfig Config { get; set; } = new();

    public void OnConfigParsed(AfkManagerConfig config)
    {
        config.Normalize();
        Config = config;
        _service?.UpdateConfig(config);
    }

    public override void Load(bool hotReload)
    {
        _languageManager = new AfkLanguageManager(GetPluginDirectory(), Logger);
        ReloadLanguage();
        _service = new AfkService(Config, _languageManager, Logger);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientDisconnectPost>(OnClientDisconnectPost);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);

        StartCheckTimer();
    }

    public override void Unload(bool hotReload)
    {
        StopCheckTimer();
        _service?.Clear();
        _service = null;
        _languageManager = null;
    }

    private void OnMapStart(string mapName)
    {
        _service?.Clear();
        StartCheckTimer();
    }

    private void OnMapEnd()
    {
        StopCheckTimer();
        _service?.Clear();
    }

    private void OnClientDisconnectPost(int playerSlot)
    {
        _service?.RemovePlayer(playerSlot);
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not null)
        {
            _service?.MarkPlayerSpawned(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not null)
        {
            _service?.MarkPlayerNotAlive(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        var newTeam = (CsTeam)@event.Team;
        var oldTeam = (CsTeam)@event.Oldteam;

        // Coming off the sideline onto a playable team is a deliberate action, so it clears
        // accumulated inactivity. Restricted to None/Spectator as the old team on purpose: the
        // plugin only ever moves players to spectator, and T<->CT swaps are usually the server
        // auto-balancing, neither of which is evidence the player is at the keyboard.
        var joinedFromSideline = (oldTeam == CsTeam.None || oldTeam == CsTeam.Spectator)
            && (newTeam == CsTeam.Terrorist || newTeam == CsTeam.CounterTerrorist);

        if (joinedFromSideline)
        {
            _service?.MarkPlayerReturnedToTeam(player);
        }

        // The controller must not be captured by the timer: if the player disconnects within the
        // delay the callback would dereference a pointer the engine has already freed. Re-resolve
        // from the slot instead, and match the user id so a slot reused by a new connection is not
        // mistaken for the original player.
        var slot = player.Slot;
        var userId = player.UserId;

        AddTimer(0.2f, () =>
        {
            var target = Utilities.GetPlayerFromSlot(slot);
            if (target is null || !target.IsValid || target.UserId != userId)
            {
                return;
            }

            _service?.MarkPlayerSpawnedIfAlive(target);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _service?.MarkAliveTeamPlayersSpawned();
        return HookResult.Continue;
    }

    private void StartCheckTimer()
    {
        StopCheckTimer();
        _checkTimer = AddTimer(
            Config.CheckIntervalSeconds,
            () => _service?.CheckPlayers(),
            TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopCheckTimer()
    {
        _checkTimer?.Kill();
        _checkTimer = null;
    }

    [ConsoleCommand("css_afk_reload", "Reloads AFK Manager settings.")]
    [RequiresPermissions("@css/config")]
    public void OnReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        // Reload throws if the config file is missing or malformed. Without this guard the command
        // would abort here, leaving the language, service config, and check timer un-refreshed.
        try
        {
            Config.Reload();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "{Prefix} Failed to reload the config file; keeping the values already in memory.", Prefix);
            command.ReplyToCommand($"{Prefix} Could not read the config file, keeping current values. See the server log for details.");
        }

        Config.Normalize();
        ReloadLanguage();
        _service?.UpdateConfig(Config);
        StartCheckTimer();
        command.ReplyToCommand($"{Prefix} Settings reloaded. css_language={GetCounterStrikeSharpLanguageName()}");
    }

    [ConsoleCommand("css_afk_enabled", "Gets or sets AFK Manager enabled state.")]
    [CommandHelper(minArgs: 0, usage: "[0|1]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/config")]
    public void OnEnabledCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand($"{Prefix} enabled={(Config.Enabled ? 1 : 0)}");
            return;
        }

        var value = command.GetArg(1);
        if (!TryParseEnabledValue(value, out var enabled))
        {
            command.ReplyToCommand($"{Prefix} Usage: css_afk_enabled <0|1>");
            return;
        }

        Config.Enabled = enabled;
        _service?.UpdateConfig(Config);

        if (!enabled)
        {
            _service?.Clear();
        }

        command.ReplyToCommand($"{Prefix} enabled={(Config.Enabled ? 1 : 0)}");
    }

    [ConsoleCommand("css_afk_config", "Prints AFK Manager runtime config.")]
    [RequiresPermissions("@css/config")]
    public void OnConfigCommand(CCSPlayerController? player, CommandInfo command)
    {
        command.ReplyToCommand($"{Prefix} enabled={(Config.Enabled ? 1 : 0)} css_language={GetCounterStrikeSharpLanguageName()} check_interval={Config.CheckIntervalSeconds}s");
        command.ReplyToCommand($"{Prefix} spawn_warning={FormatConfigSeconds(Config.SpawnWarningTimeSeconds)} spawn_move={FormatConfigSeconds(Config.SpawnMoveToSpectatorTimeSeconds)} repeat_warning={FormatConfigSeconds(Config.RepeatWarningIntervalSeconds)}");
        command.ReplyToCommand($"{Prefix} warning={FormatConfigSeconds(Config.WarningTimeSeconds)} move={FormatConfigSeconds(Config.MoveToSpectatorTimeSeconds)} kick={FormatConfigSeconds(Config.KickTimeSeconds)}");
        command.ReplyToCommand($"{Prefix} no_team_move={FormatConfigSeconds(Config.NoTeamMoveToSpectatorTimeSeconds)} no_team_kick={FormatConfigSeconds(Config.NoTeamKickTimeSeconds)}");
    }

    [ConsoleCommand("css_afk_status", "Prints AFK status for all players, or for a target name/#userid.")]
    [CommandHelper(minArgs: 0, usage: "[target]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/generic")]
    public void OnStatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        var service = _service;
        if (service is null)
        {
            command.ReplyToCommand($"{Prefix} Plugin is not loaded.");
            return;
        }

        var players = command.ArgCount >= 2
            ? service.FindPlayers(command.GetArg(1))
            : Utilities.GetPlayers().Where(candidate => candidate.IsValid && candidate.UserId is not null && candidate.UserId >= 0).ToList();

        if (players.Count == 0)
        {
            command.ReplyToCommand($"{Prefix} No matching players.");
            return;
        }

        foreach (var target in players)
        {
            command.ReplyToCommand($"{Prefix} {service.DescribePlayer(target)}");
        }
    }

    private void ReloadLanguage()
    {
        _languageManager ??= new AfkLanguageManager(GetPluginDirectory(), Logger);
        _languageManager.ClearCache();
        _languageManager.LoadCounterStrikeSharpLanguage();
    }

    private string GetCounterStrikeSharpLanguageName()
    {
        _languageManager ??= new AfkLanguageManager(GetPluginDirectory(), Logger);
        return _languageManager.GetCounterStrikeSharpLanguageName();
    }

    private string GetPluginDirectory()
    {
        return Path.GetDirectoryName(ModulePath) ?? AppContext.BaseDirectory;
    }

    private static bool TryParseEnabledValue(string value, out bool enabled)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "on":
            case "yes":
                enabled = true;
                return true;
            case "0":
            case "false":
            case "off":
            case "no":
                enabled = false;
                return true;
            default:
                enabled = false;
                return false;
        }
    }

    private static string FormatConfigSeconds(float value)
    {
        return value <= 0 ? "0s" : $"{value:0.##}s";
    }
}
