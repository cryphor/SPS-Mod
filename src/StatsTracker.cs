using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SPSMod
{
    public static class StatsTracker
    {
        private static MatchRecord _currentMatch;
        private static bool _matchActive;

        // Track goalie who was last on defense when puck entered goal
        private static readonly Dictionary<int, string> _pendingGoalieGA = new();

        // ── TOI tracking ──
        private static readonly Dictionary<string, float> _playerPlayEnterTime = new();

        // ── Faceoff tracking ──
        private static bool _pendingFaceoff;

        // ── Goal sequence for GWG computation ──
        private static int _goalIndex;

        private static StatsDatabase _database;

        private static string _serverName = "Server";

        // ── Init / Shutdown ──────────────────────────────────────────────

        public static void Initialize()
        {
            _database = StatsApi.Load();
            _matchActive = false;

            // Fetch server name from puckstats.io — delayed 10s after startup
            ScheduleServerNameFetch();

            Plugin.Log($"Server name: {_serverName}");

            EventManager.AddEventListener("Event_Server_OnPuckEnterGoal", OnPuckEnterGoal);
            EventManager.AddEventListener("Event_Everyone_OnGoalScored", OnGoalScored);
            EventManager.AddEventListener("Event_Everyone_OnPlayerGameStateChanged", OnPlayerGameStateChanged);
            EventManager.AddEventListener("Event_Everyone_OnGameStateChanged", OnGameStateChanged);

            Plugin.Log("StatsTracker initialized");
        }

        public static void Shutdown()
        {
            if (_matchActive && _currentMatch != null)
                FinalizeMatch();

            EventManager.RemoveEventListener("Event_Server_OnPuckEnterGoal", OnPuckEnterGoal);
            EventManager.RemoveEventListener("Event_Everyone_OnGoalScored", OnGoalScored);
            EventManager.RemoveEventListener("Event_Everyone_OnPlayerGameStateChanged", OnPlayerGameStateChanged);
            EventManager.RemoveEventListener("Event_Everyone_OnGameStateChanged", OnGameStateChanged);
        }

        // ── Called from PatchPuckCollisions ──────────────────────────────

        public static void RecordPass(string steamId)
        {
            if (!_matchActive || _currentMatch == null) return;
            if (_currentMatch.Players.TryGetValue(steamId, out var stats))
                stats.Passes++;
        }

        public static void OnPuckStickHit(string steamId, int puckId)
        {
            if (!_matchActive || _currentMatch == null) return;

            // First touch after faceoff → faceoff win
            if (_pendingFaceoff)
            {
                _pendingFaceoff = false;

                if (_currentMatch.Players.TryGetValue(steamId, out var winner))
                {
                    winner.FaceoffWins++;

                    // Give faceoff loss to all opponents on the ice
                    var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                    if (pm != null)
                    {
                        var winnerTeam = pm.GetPlayerBySteamId(steamId)?.Team ?? PlayerTeam.None;
                        foreach (var player in pm.GetPlayers())
                        {
                            if (player.Phase == PlayerPhase.Play &&
                                player.Team != winnerTeam &&
                                player.Team != PlayerTeam.None &&
                                player.Team != PlayerTeam.Spectator)
                            {
                                var sid = player.SteamId.Value.ToString();
                                if (_currentMatch.Players.TryGetValue(sid, out var loserStats))
                                    loserStats.FaceoffLosses++;
                            }
                        }
                    }
                }
            }

        }

        // ── Event handlers ───────────────────────────────────────────────

        private static void OnPuckEnterGoal(Dictionary<string, object> message)
        {
            if (!_matchActive || _currentMatch == null) return;

            var defendingTeam = (PlayerTeam)message["team"];
            var puck = (Puck)message["puck"];

            var attackingTeam = defendingTeam == PlayerTeam.Blue ? PlayerTeam.Red : PlayerTeam.Blue;

            // ── Shooter: last attacking player to touch the puck ──
            var attackerCollisions = puck.GetPlayerCollisionsByTeam(attackingTeam);
            if (attackerCollisions.Count > 0)
            {
                var shooter = attackerCollisions[0].Key;
                var sid = shooter.SteamId.Value.ToString();
                var name = shooter.Username.Value.ToString();
                GetOrCreatePlayerStats(sid, name).Shots++;

                // Team shots
                if (attackingTeam == PlayerTeam.Blue)
                {
                    _currentMatch.BlueTeam.ShotsFor++;
                    _currentMatch.RedTeam.ShotsAgainst++;
                }
                else
                {
                    _currentMatch.RedTeam.ShotsFor++;
                    _currentMatch.BlueTeam.ShotsAgainst++;
                }
            }

            // ── Defender tracking: goalie saves + blocked shots ──
            var defenderCollisions = puck.GetPlayerCollisionsByTeam(defendingTeam);
            bool goalieFound = false;
            foreach (var kv in defenderCollisions)
            {
                var defender = kv.Key;
                if (defender.Role == PlayerRole.Goalie && !goalieFound)
                {
                    goalieFound = true;
                    var gid = defender.SteamId.Value.ToString();
                    var gname = defender.Username.Value.ToString();
                    GetOrCreatePlayerStats(gid, gname).ShotsAgainst++;
                    _pendingGoalieGA[puck.GetInstanceID()] = gid;
                }
                else if (defender.Role != PlayerRole.Goalie)
                {
                    var did = defender.SteamId.Value.ToString();
                    if (_currentMatch.Players.TryGetValue(did, out var blockStats))
                        blockStats.ShotsBlocked++;
                }
            }
        }

        private static void OnGoalScored(Dictionary<string, object> message)
        {
            if (!_matchActive || _currentMatch == null) return;

            var scoringTeam = (PlayerTeam)message["byTeam"];
            var goalPlayer = (Player)message["goalPlayer"];
            var assistPlayer = (Player)message["assistPlayer"];
            var secondAssist = (Player)message["secondAssistPlayer"];
            var puck = (Puck)message["puck"];

            var defendingTeam = scoringTeam == PlayerTeam.Blue ? PlayerTeam.Red : PlayerTeam.Blue;

            // ── Goal scorer ──
            var scorerId = goalPlayer.SteamId.Value.ToString();
            var scorerName = goalPlayer.Username.Value.ToString();
            var scorerStats = GetOrCreatePlayerStats(scorerId, scorerName);
            scorerStats.Goals++;

            // ── Assists ──
            if (assistPlayer != null && assistPlayer.SteamId.Value.Value.Length > 0)
            {
                var aid = assistPlayer.SteamId.Value.ToString();
                var aname = assistPlayer.Username.Value.ToString();
                GetOrCreatePlayerStats(aid, aname).Assists++;
            }

            if (secondAssist != null && secondAssist.SteamId.Value.Value.Length > 0)
            {
                var sid = secondAssist.SteamId.Value.ToString();
                var sname = secondAssist.Username.Value.ToString();
                GetOrCreatePlayerStats(sid, sname).Assists++;
            }

            // ── Goalie goals-against ──
            int puckIdInstance = puck.GetInstanceID();
            if (_pendingGoalieGA.TryGetValue(puckIdInstance, out var goalieId))
            {
                if (_currentMatch.Players.TryGetValue(goalieId, out var gStats))
                    gStats.GoalsAgainst++;
                _pendingGoalieGA.Remove(puckIdInstance);
            }

            // ── Team scores ──
            if (scoringTeam == PlayerTeam.Blue)
            {
                _currentMatch.BlueScore++;
                _currentMatch.BlueTeam.GoalsFor++;
                _currentMatch.RedTeam.GoalsAgainst++;
            }
            else
            {
                _currentMatch.RedScore++;
                _currentMatch.RedTeam.GoalsFor++;
                _currentMatch.BlueTeam.GoalsAgainst++;
            }

            // ── Plus/minus: all players on ice at even strength ──
            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm != null)
            {
                foreach (var player in pm.GetPlayers())
                {
                    if (player.Phase == PlayerPhase.Play)
                    {
                        var sid = player.SteamId.Value.ToString();
                        var stats = GetOrCreatePlayerStats(sid, player.Username.Value.ToString());
                        if (player.Team == scoringTeam)
                            stats.PlusMinus++;
                        else if (player.Team == defendingTeam)
                            stats.PlusMinus--;
                    }
                }
            }

            // ── Track goal sequence for GWG ──
            _goalIndex++;
            var goalEvent = new GoalEvent
            {
                ScoringTeam = scoringTeam == PlayerTeam.Blue ? "blue" : "red",
                ScorerSteamId = scorerId,
                GoalIndex = _goalIndex
            };

            if (assistPlayer != null && assistPlayer.SteamId.Value.Value.Length > 0)
                goalEvent.AssistSteamIds.Add(assistPlayer.SteamId.Value.ToString());
            if (secondAssist != null && secondAssist.SteamId.Value.Value.Length > 0)
                goalEvent.AssistSteamIds.Add(secondAssist.SteamId.Value.ToString());

            _currentMatch.Goals.Add(goalEvent);

            Plugin.Log($"Goal: {scorerName} — {_currentMatch.BlueScore}-{_currentMatch.RedScore}");

            // ── Push live state ──
            PushLive();
        }

        private static void OnPlayerGameStateChanged(Dictionary<string, object> message)
        {
            if (!_matchActive) return;

            var player = (Player)message["player"];
            var oldState = (PlayerGameState)message["oldGameState"];
            var newState = (PlayerGameState)message["newGameState"];

            if (oldState.Phase != newState.Phase)
            {
                var sid = player.SteamId.Value.ToString();
                var name = player.Username.Value.ToString();

                // Entering Play phase → start TOI timer
                if (newState.Phase == PlayerPhase.Play && oldState.Phase != PlayerPhase.Play)
                {
                    _playerPlayEnterTime[sid] = Time.time;
                }

                // Leaving Play phase → accumulate TOI
                if (oldState.Phase == PlayerPhase.Play && newState.Phase != PlayerPhase.Play)
                {
                    if (_playerPlayEnterTime.TryGetValue(sid, out var enterTime))
                    {
                        var elapsed = (int)(Time.time - enterTime);
                        var stats = GetOrCreatePlayerStats(sid, name);
                        stats.TimeOnIceSeconds += elapsed;
                        _playerPlayEnterTime.Remove(sid);
                    }
                }
            }
        }

        private static void OnGameStateChanged(Dictionary<string, object> message)
        {
            var oldState = (GameState)message["oldGameState"];
            var newState = (GameState)message["newGameState"];

            // FaceOff → match start (first period only, not period transitions)
            if (newState.Phase == GamePhase.FaceOff && oldState.Phase != GamePhase.FaceOff)
            {
                if (!_matchActive)
                    StartNewMatch();
            }

            // Play starting after FaceOff → arm faceoff detection
            if (newState.Phase == GamePhase.Play && oldState.Phase == GamePhase.FaceOff)
            {
                _pendingFaceoff = _matchActive;
            }

            // Play → anything else → disarm faceoff
            if (oldState.Phase == GamePhase.Play && newState.Phase != GamePhase.Play)
            {
                _pendingFaceoff = false;
            }

            // GameOver = match end
            if (newState.Phase == GamePhase.GameOver)
            {
                if (_matchActive && _currentMatch != null)
                    FinalizeMatch();
            }

            // Forced match end: Play/FaceOff → Warmup/None/PreGame while match active
            if (_matchActive && _currentMatch != null)
            {
                bool wasPlaying = oldState.Phase == GamePhase.Play || oldState.Phase == GamePhase.FaceOff
                    || oldState.Phase == GamePhase.Replay || oldState.Phase == GamePhase.Intermission;
                bool isStopped = newState.Phase == GamePhase.Warmup || newState.Phase == GamePhase.None
                    || newState.Phase == GamePhase.PreGame || newState.Phase == GamePhase.PostGame;

                if (wasPlaying && isStopped)
                {
                    Plugin.Log("Match forcefully ended — finalizing");
                    FinalizeMatch();
                }
            }

            // Period transition → push live state
            if (newState.Phase == GamePhase.Intermission || newState.Phase == GamePhase.FaceOff)
            {
                if (_matchActive && _currentMatch != null)
                    PushLive();
            }
        }

        // ── Server name from puckstats.io ──────────────────────────────

        private static void ScheduleServerNameFetch()
        {
            // Capture the server port now (on main thread, ServerManager should be up)
            ushort? ownPort = null;
            try
            {
                var sm = NetworkBehaviourSingleton<ServerManager>.Instance;
                if (sm?.ServerConfig != null)
                    ownPort = sm.ServerConfig.port;
            }
            catch { }

            var capturedPort = ownPort;
            Plugin.Log($"Will fetch server name from puckstats.io in 10s (port: {capturedPort?.ToString() ?? "unknown"})");

            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(10000);
                FetchServerNameFromPuckStats(capturedPort);
            });
        }

        private static void FetchServerNameFromPuckStats(ushort? ownPort)
        {
            const string ip = "207.2.120.215";

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.Add("User-Agent", "SPSMod/1.0");

                // Try own port first, then scan 1-50
                var portsToTry = new List<ushort>();
                if (ownPort.HasValue)
                    portsToTry.Add(ownPort.Value);
                for (ushort p = 1; p <= 50; p++)
                {
                    if (!portsToTry.Contains(p))
                        portsToTry.Add(p);
                }

                foreach (var port in portsToTry)
                {
                    try
                    {
                        var url = $"https://puckstats.io/api/server-list/server?ip={ip}&port={port}";
                        var json = client.GetStringAsync(url).Result;

                        var data = JObject.Parse(json);
                        var names = data["names"] as JArray;
                        if (names != null && names.Count > 0)
                        {
                            var firstName = names[0]["name"]?.Value<string>();
                            if (!string.IsNullOrEmpty(firstName))
                            {
                                _serverName = firstName;
                                Plugin.Log($"Server name '{_serverName}' from puckstats.io API (port {port})");
                                return;
                            }
                        }
                    }
                    catch
                    {
                        // Port has no server or timed out — keep scanning
                    }
                }

                Plugin.Log($"No server name found on any port 1-50");
            }
            catch (Exception ex)
            {
                Plugin.Log($"Failed to fetch from puckstats.io: {ex.Message}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static void StartNewMatch()
        {
            // Re-fetch from API to pick up any external changes (e.g. data reset)
            _database = StatsApi.Load();

            _currentMatch = new MatchRecord
            {
                Timestamp = DateTime.UtcNow.ToString("o")
            };
            _matchActive = true;
            _pendingGoalieGA.Clear();
            _playerPlayEnterTime.Clear();
            _pendingFaceoff = false;
            _goalIndex = 0;

            // Snapshot current players
            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm != null)
            {
                foreach (var player in pm.GetPlayers())
                {
                    var sid = player.SteamId.Value.ToString();
                    var name = player.Username.Value.ToString();
                    GetOrCreatePlayerStats(sid, name);
                }
            }

            Plugin.Log("New match started");

            // Push initial live state
            PushLive();
        }

        private static void FinalizeMatch()
        {
            if (_currentMatch == null) return;

            // Flush any remaining TOI (players still in Play phase)
            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm != null)
            {
                foreach (var player in pm.GetPlayers())
                {
                    if (player.Phase == PlayerPhase.Play)
                    {
                        var sid = player.SteamId.Value.ToString();
                        if (_playerPlayEnterTime.TryGetValue(sid, out var enterTime))
                        {
                            var elapsed = (int)(Time.time - enterTime);
                            if (_currentMatch.Players.TryGetValue(sid, out var stats))
                                stats.TimeOnIceSeconds += elapsed;
                        }
                    }
                }
            }
            _playerPlayEnterTime.Clear();

            // Sync final scores from game state
            var gm = GameManager.Instance;
            if (gm != null)
            {
                _currentMatch.BlueScore = gm.BlueScore;
                _currentMatch.RedScore = gm.RedScore;
                _currentMatch.Period = gm.Period;

                _currentMatch.BlueTeam.GoalsFor = gm.BlueScore;
                _currentMatch.RedTeam.GoalsFor = gm.RedScore;
                _currentMatch.BlueTeam.GoalsAgainst = gm.RedScore;
                _currentMatch.RedTeam.GoalsAgainst = gm.BlueScore;
            }

            // ── Compute GWG ──
            ComputeGameWinningGoal();

            _database.Matches.Add(_currentMatch);
            StatsApi.Save(_database);

            // Push inactive live state so frontend knows match ended
            StatsApi.PushLiveState(new LiveMatchState
            {
                MatchId = _currentMatch.MatchId,
                ServerName = _serverName,
                Active = false,
                Period = _currentMatch.Period,
                BlueScore = _currentMatch.BlueScore,
                RedScore = _currentMatch.RedScore
            });

            _matchActive = false;
            var mId = _currentMatch.MatchId;
            _currentMatch = null;

            Plugin.Log($"Match {mId} finalized and saved");
        }

        private static void ComputeGameWinningGoal()
        {
            if (_currentMatch.Goals.Count == 0) return;

            int blueFinal = _currentMatch.BlueScore;
            int redFinal = _currentMatch.RedScore;

            if (blueFinal == redFinal) return; // tie — no GWG

            bool blueWon = blueFinal > redFinal;
            int losingFinalScore = blueWon ? redFinal : blueFinal;

            int blueScore = 0;
            int redScore = 0;

            foreach (var goal in _currentMatch.Goals)
            {
                if (goal.ScoringTeam == "blue") blueScore++;
                else redScore++;

                int winningTeamScore = blueWon ? blueScore : redScore;

                // First goal where winning team's score exceeds losing team's final score
                if (winningTeamScore > losingFinalScore)
                {
                    if (_currentMatch.Players.TryGetValue(goal.ScorerSteamId, out var scorer))
                        scorer.GameWinningGoal = true;
                    break;
                }
            }
        }

        private static PlayerMatchStats GetOrCreatePlayerStats(string steamId, string username)
        {
            if (_currentMatch == null) return null;

            if (!_currentMatch.Players.TryGetValue(steamId, out var stats))
            {
                stats = new PlayerMatchStats
                {
                    SteamId = steamId,
                    Username = username
                };
                _currentMatch.Players[steamId] = stats;
            }
            return stats;
        }

        // ── Live state push ─────────────────────────────────────────────

        private static void PushLive()
        {
            if (_currentMatch == null) return;

            var state = new LiveMatchState
            {
                MatchId = _currentMatch.MatchId,
                ServerName = _serverName,
                Active = true,
                Period = _currentMatch.Period,
                BlueScore = _currentMatch.BlueScore,
                RedScore = _currentMatch.RedScore
            };

            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm != null)
            {
                foreach (var player in pm.GetPlayers())
                {
                    if (player.Team == PlayerTeam.None || player.Team == PlayerTeam.Spectator)
                        continue;

                    var sid = player.SteamId.Value.ToString();
                    var stats = _currentMatch.Players.TryGetValue(sid, out var ms) ? ms : null;

                    state.Players.Add(new LivePlayerEntry
                    {
                        SteamId = sid,
                        Username = player.Username.Value.ToString(),
                        Number = player.Number.Value,
                        Team = player.Team == PlayerTeam.Blue ? "blue" : "red",
                        Role = player.Role == PlayerRole.Goalie ? "goalie" : "attacker",
                        Goals = stats?.Goals ?? 0,
                        Assists = stats?.Assists ?? 0,
                        Shots = stats?.Shots ?? 0,
                        Saves = player.Role == PlayerRole.Goalie ? stats?.ShotsAgainst ?? 0 : 0,
                        ShotsAgainst = stats?.ShotsAgainst ?? 0,
                        GoalsAgainst = stats?.GoalsAgainst ?? 0,
                        PlusMinus = stats?.PlusMinus ?? 0,
                        Hits = stats?.Hits ?? 0,
                        ShotsBlocked = stats?.ShotsBlocked ?? 0,
                        FaceoffWins = stats?.FaceoffWins ?? 0,
                        FaceoffLosses = stats?.FaceoffLosses ?? 0,
                        TimeOnIceSeconds = stats?.TimeOnIceSeconds ?? 0,
                    });
                }
            }

            StatsApi.PushLiveState(state);
        }
    }
}
