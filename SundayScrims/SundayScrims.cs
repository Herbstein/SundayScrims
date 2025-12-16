using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace SundayScrims;

public class SundayScrims : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "SundayScrims";
    public override string ModuleVersion => "0.0.1";

    public Config Config { get; set; } = new();

    private string ConnectionString =>
        $"Server={Config.DbServer}; User ID={Config.DbUsername}; Password={Config.DbPassword}; Database={Config.DbDatabase}";


    private MySqlConnection _connection = null!;

    public void OnConfigParsed(Config config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("Getting ready to load {ModuleName}@{ModuleVersion}", ModuleName, ModuleVersion);

        Logger.LogInformation("Setting up handlers");

        AddCommand("balance", "Balance all players on the server", OnBalanceCommand);

        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventGameEnd>(OnGameEnd);

        Logger.LogInformation("Connecting to database");

        _connection = new MySqlConnection(ConnectionString);
        _connection.Open();

        Logger.LogInformation("{ModuleName}@{ModuleVersion} has been loaded!", ModuleName, ModuleVersion);
    }

    private void OnBalanceCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null)
        {
            Logger.LogInformation("Received balance command not from player");
            return;
        }

        Logger.LogInformation("Trying to balance player(s)!");

        var players = Utilities.GetPlayers()
            .Where(p => p is { IsValid: true, IsBot: false, Connected: PlayerConnectedState.PlayerConnected })
            .ToList();

        Logger.LogInformation("Found {PlayersCount} players", players.Count);

        // TODO(herbstein): just here to test DB connection 
        var result = _connection.QuerySingle<int>("select 42");
        Logger.LogInformation("Received {Result} from database", result);

        if (players.Count < 2)
        {
            Logger.LogInformation("Too few players to balance");
            return;
        }

        var playerIds = players.Select(p => p.AuthorizedSteamID!.SteamId64).ToArray();

        Logger.LogInformation("Players found: {PlayerIds}", playerIds);
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        Logger.LogInformation("Round finished. Won by {Winner}", @event.Winner);
        return HookResult.Continue;
    }

    private HookResult OnGameEnd(EventGameEnd @event, GameEventInfo info)
    {
        Logger.LogInformation("Game was won by {EventWinner}", @event.Winner);
        return HookResult.Continue;
    }
}