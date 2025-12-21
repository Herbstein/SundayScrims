using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace SundayScrims;

/*
 * CREATE TABLE IF NOT EXISTS player_ratings (
    steam_id BIGINT UNSIGNED NOT NULL,
    rating INT NOT NULL DEFAULT 1000,
    wins INT NOT NULL DEFAULT 0,
    losses INT NOT NULL DEFAULT 0,
    last_played DATETIME DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (steam_id)
);
 */

public class SundayScrims : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "SundayScrims";
    public override string ModuleVersion => "0.0.1";

    public Config Config { get; set; } = new();

    private string ConnectionString =>
        $"Server={Config.DbServer}; User ID={Config.DbUsername}; Password={Config.DbPassword}; Database={Config.DbDatabase}; Pooling=true";

    private readonly ConcurrentDictionary<ulong, int> _eloCache = new();
    private readonly ConcurrentDictionary<ulong, CsTeam> _teamAssignments = new();

    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    private bool _isLive = false;

    public void OnConfigParsed(Config config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("Getting ready to load {ModuleName}@{ModuleVersion}", ModuleName, ModuleVersion);

        AddCommand("balance", "Balance two teams and start match", OnBalanceCommand);
        AddCommand("scrims", "Plugin description", (p, _) =>
        {
            if (p != null)
            {
                p.PrintToChat("https://github.com/Herbstein/SundayScrims");
            }
        });
        AddCommand("reset", "Stop everything", (player, info) =>
        {
            _isLive = false;
            _teamAssignments.Clear();
            _mainThreadActions.Clear();
        });


        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventCsWinPanelMatch>(OnWinPanelMatch);

        RegisterListener<Listeners.OnTick>(HandleTick);

        if (hotReload)
        {
            RestoreState();
        }

        Logger.LogInformation("{ModuleName}@{ModuleVersion} has been loaded!", ModuleName, ModuleVersion);
    }

    private void RestoreState()
    {
        var players = Utilities.GetPlayers();

        // TODO(herbstein): need a more robust check!
        if (players.Any(p => p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            _isLive = true;
        }

        foreach (var p in players)
        {
            if (p.IsBot)
            {
                continue;
            }

            var steamId = p.SteamID;

            switch (p.Team)
            {
                case CsTeam.Terrorist:
                    _teamAssignments[steamId] = CsTeam.Terrorist;
                    break;
                case CsTeam.CounterTerrorist:
                    _teamAssignments[steamId] = CsTeam.CounterTerrorist;
                    break;
            }

            var name = p.PlayerName;

            Task.Run(async () =>
            {
                var elo = await GetEloFromDb(steamId);
                _eloCache[steamId] = elo;
                Logger.LogInformation("Restore Elo for {Name}: {Elo}", name, elo);
            });
        }
    }

    private void HandleTick()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.LogInformation("Error in main thread action: {message}", ex.Message);
            }
        }
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
            .Where(p => p is { IsBot: false, Connected: PlayerConnectedState.PlayerConnected })
            .ToList();

        if (players.Count < 2)
        {
            player.PrintToCenterAlert("Not enough players");
            Logger.LogInformation("Too few players to balance");
            return;
        }

        var (ctTeam, tTeam) = BalanceTeams(players);

        _teamAssignments.Clear();

        foreach (var p in tTeam)
        {
            _teamAssignments[p.SteamID] = CsTeam.Terrorist;
        }

        foreach (var p in ctTeam)
        {
            _teamAssignments[p.SteamID] = CsTeam.CounterTerrorist;
        }

        foreach (var p in tTeam)
        {
            p.SwitchTeam(CsTeam.Terrorist);
        }

        foreach (var p in ctTeam)
        {
            p.SwitchTeam(CsTeam.CounterTerrorist);
        }

        _isLive = true;

        Server.PrintToChatAll("[Scrims] Teams Balanced! Restarting game...");
        Server.ExecuteCommand("mp_warmup_end");
        Server.ExecuteCommand("mp_restartgame 1");
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot)
        {
            return HookResult.Continue;
        }

        var steamId = player.SteamID;
        var playerName = player.PlayerName;

        Logger.LogInformation("Player {PlayerName} joined with id {SteamId}", playerName, steamId);

        Task.Run(async () =>
        {
            var elo = await GetEloFromDb(steamId);
            _eloCache[steamId] = elo;

            Logger.LogInformation("Player {PlayerName} has rating {Elo}", playerName, elo);

            _mainThreadActions.Enqueue(() =>
            {
                var p = Utilities.GetPlayerFromSteamId64(steamId);
                if (p == null || p is not { IsValid: true, Connected: PlayerConnectedState.PlayerConnected })
                {
                    return;
                }

                p.PrintToChat($"[Scrims] Your rating: {elo}");

                if (_isLive)
                {
                    if (_teamAssignments.TryGetValue(steamId, out var assignedTeam))
                    {
                        p.SwitchTeam(assignedTeam);
                        p.PrintToChat($"[Scrims] Restored you to team {assignedTeam}");
                    }
                    else
                    {
                        var newTeam = GetWeakerTeam();
                        _teamAssignments[steamId] = newTeam;

                        p.PrintToChat($"[Scrims] Late join! Assigning you to {newTeam} to balance Elo.");

                        Logger.LogInformation("Late joiner {name} assigned to {team}", playerName, newTeam);

                        Server.NextFrame(() => { p.SwitchTeam(newTeam); });
                    }
                }
            });
        });

        return HookResult.Continue;
    }

    private CsTeam GetWeakerTeam()
    {
        long tElo = 0;
        long tCount = 0;
        long ctElo = 0;
        long ctCount = 0;

        foreach (var (playerId, team) in _teamAssignments)
        {
            var playerElo = _eloCache.GetValueOrDefault(playerId, 1000);
            if (team == CsTeam.Terrorist)
            {
                tElo += playerElo;
                tCount++;
            }
            else
            {
                ctElo += playerElo;
                ctCount++;
            }
        }

        if (tCount > 0)
        {
            tElo /= tCount;
        }

        if (ctCount > 0)
        {
            ctElo /= ctCount;
        }

        return tElo <= ctElo ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
    }

    private HookResult OnWinPanelMatch(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        Server.PrintToChatAll("[Scrims] Start Game Over");

        if (!_isLive)
        {
            return HookResult.Continue;
        }

        var ctScore = 0;
        var tScore = 0;

        var teams = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
        foreach (var team in teams)
        {
            Server.PrintToChatAll($"[Scrims] {team.InitialTeamNum}: {team.Score}");

            switch (team.InitialTeamNum)
            {
                case (int)CsTeam.CounterTerrorist:
                    ctScore = team.Score;
                    break;
                case (int)CsTeam.Terrorist:
                    tScore = team.Score;
                    break;
            }
        }


        Server.PrintToChatAll($"[Scrims] Match over. Final Score - CT: {ctScore}, T: {tScore}");

        var winningTeam = CsTeam.None;
        if (ctScore > tScore)
        {
            winningTeam = CsTeam.CounterTerrorist;
        }
        else if (ctScore < tScore)
        {
            winningTeam = CsTeam.Terrorist;
        }

        Server.PrintToChatAll($"[Scrims] Winning team: {winningTeam}");

        if (winningTeam == CsTeam.None)
        {
            _isLive = false;
            _teamAssignments.Clear();
            return HookResult.Continue;
        }

        List<ulong> winners = [];
        List<ulong> losers = [];

        foreach (var (playerId, team) in _teamAssignments)
        {
            if (team == winningTeam)
            {
                winners.Add(playerId);
            }
            else
            {
                losers.Add(playerId);
            }
        }

        var winnerAvg = winners.Count != 0 ? winners.Average(id => _eloCache.GetValueOrDefault(id, 1000)) : 1000;
        var loserAvg = losers.Count != 0 ? losers.Average(id => _eloCache.GetValueOrDefault(id, 1000)) : 1000;

        var expectedWin = 1.0 / (1.0 + Math.Pow(10, (loserAvg - winnerAvg) / 400.0));
        const int kFactor = 32;
        var eloChange = (int)(kFactor * (1.0 - expectedWin));

        Task.Run(async () =>
        {
            foreach (var steamId in winners)
            {
                await UpdateEloInDb(steamId, eloChange, true);
            }

            foreach (var steamId in losers)
            {
                await UpdateEloInDb(steamId, -eloChange, false);
            }
        });

        Server.PrintToChatAll($"[Scrims] Match Finished! Winers gained {eloChange} Elo.");

        _isLive = false;
        _teamAssignments.Clear();

        return HookResult.Continue;
    }

    private (List<CCSPlayerController> T, List<CCSPlayerController> CT) BalanceTeams(List<CCSPlayerController> players)
    {
        var sorted = players.OrderByDescending(p => _eloCache.GetValueOrDefault(p.SteamID, 1000)).ToList();

        var team1 = new List<CCSPlayerController>();
        var team2 = new List<CCSPlayerController>();
        var team1Elo = 0;
        var team2Elo = 0;

        foreach (var p in sorted)
        {
            var pElo = _eloCache.GetValueOrDefault(p.SteamID, 1000);

            if (team1.Count < team2.Count)
            {
                team1.Add(p);
                team1Elo += pElo;
            }
            else if (team2.Count < team1.Count)
            {
                team2.Add(p);
                team2Elo += pElo;
            }
            else if (team1Elo <= team2Elo)
            {
                team1.Add(p);
                team1Elo += pElo;
            }
            else
            {
                team2.Add(p);
                team2Elo += pElo;
            }
        }

        return (team1, team2);
    }

    private async Task<int> GetEloFromDb(ulong steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(ConnectionString);
            var result = await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT rating FROM player_ratings WHERE steam_id = @Id", new { Id = steamId });
            return result ?? 1000;
        }
        catch (Exception e)
        {
            return 1000;
        }
    }

    private async Task UpdateEloInDb(ulong steamId, int eloChange, bool isWin)
    {
        try
        {
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.ExecuteAsync(
                "INSERT INTO player_ratings (steam_id, rating, wins, losses) VALUES (@Id, 1000 + @Chg, @W, @L ON DUPLICATE KEY UPDATE rating = rating + @Chg, wins = wins + @W, losses = losses + @L, last_played = NOW()",
                new { Id = steamId, Chg = eloChange, W = isWin ? 1 : 0, L = isWin ? 0 : 1 });
            if (_eloCache.ContainsKey(steamId))
            {
                _eloCache[steamId] += eloChange;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Error when updating elo for {SteamId}: {ExMessage}", steamId, ex.Message);
        }
    }
}