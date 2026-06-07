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

        // ── Pending shot tracking ──
        private class PendingShotInfo
        {
            public string SteamId;
            public string PlayerName;
            public string Team; // "blue" or "red"
            public float Timestamp; // Time.time
        }
        private static readonly Dictionary<int, PendingShotInfo> _pendingShotByPuck = new();
        private const float SHOT_TIMEOUT_SECONDS = 3.0f;

        // ── Goal sequence for GWG computation ──
        private static int _goalIndex;

        // ── 30s live push timer (main-thread safe) ──
        private static DateTime _lastPushTime = DateTime.MinValue;

        // ── Player session data (survives disconnect for live view) ──
        private class PlayerLiveInfo
        {
            public int Number;
            public string Team = "";
            public string Role = "";
        }
        private static readonly Dictionary<string, PlayerLiveInfo> _playerLiveInfo = new();

        private static StatsDatabase _database;

        private static string _serverName = "Server";

        // ── Init / Shutdown ──────────────────────────────────────────────

        /// <summary>
        /// Called from StatsApi.LoadAsync when the background fetch completes.
        /// Replaces the in-memory database without blocking startup.
        /// </summary>
        internal static void ReplaceDatabase(StatsDatabase db)
        {
            if (db != null)
                _database = db;
        }

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

                    // Record faceoff win in play-by-play
                    var pbpPm = MonoBehaviourSingleton<PlayerManager>.Instance;
                    var pbpTeam = pbpPm?.GetPlayerBySteamId(steamId)?.Team ?? PlayerTeam.None;
                    _currentMatch.PlayByPlay.Add(new PlayByPlayEvent
                    {
                        Type = "faceoff",
                        Team = pbpTeam == PlayerTeam.Blue ? "blue" : pbpTeam == PlayerTeam.Red ? "red" : "",
                        PlayerName = winner.Username
                    });

                    // Give faceoff loss to all opponents on the ice
                    var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                    if (pm != null)
                    {
                        var winnerTeam = pbpTeam; // already resolved above
                        var playersList = pm.GetPlayers();
                        var matchPlayers = _currentMatch.Players; // cache
                        foreach (var player in playersList)
                        {
                            if (player.Phase == PlayerPhase.Play &&
                                player.Team != winnerTeam &&
                                player.Team != PlayerTeam.None &&
                                player.Team != PlayerTeam.Spectator)
                            {
                                var sid = player.SteamId.Value.ToString();
                                if (matchPlayers.TryGetValue(sid, out var loserStats))
                                    loserStats.FaceoffLosses++;
                            }
                        }
                    }
                }
            }

        }

        /// <summary>
        /// Called from PatchPuckCollisions when a stick leaves the puck
        /// with ShotSpeed above threshold. Records a pending shot attempt
        /// that will later be resolved as SOG, block, or miss.
        /// </summary>
        public static void OnShotAttempt(string steamId, int puckId, float speed, string playerName, string team)
        {
            if (!_matchActive || _currentMatch == null) return;

            _pendingShotByPuck[puckId] = new PendingShotInfo
            {
                SteamId = steamId,
                PlayerName = playerName,
                Team = team,
                Timestamp = Time.time
            };

            GetOrCreatePlayerStats(steamId, playerName).ShotAttempts++;
        }

        /// <summary>
        /// Checks if a player touching the puck is blocking a pending shot.
        /// Called from PatchPuckCollisions.OnCollisionEnter.
        /// </summary>
        public static bool CheckShotBlocked(string steamId, int puckId)
        {
            if (!_matchActive || _currentMatch == null) return false;

            if (!_pendingShotByPuck.TryGetValue(puckId, out var shot))
                return false;

            // Same player touching their own shot — not a block
            if (steamId == shot.SteamId)
                return false;

            // Check if they're on opposite teams
            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm == null) return false;

            var hitter = pm.GetPlayerBySteamId(steamId);
            var shooter = pm.GetPlayerBySteamId(shot.SteamId);
            if (hitter == null || shooter == null) return false;

            var hitterTeam = hitter.Team == PlayerTeam.Blue ? "blue"
                : hitter.Team == PlayerTeam.Red ? "red" : "";

            if (string.IsNullOrEmpty(shot.Team) || string.IsNullOrEmpty(hitterTeam))
                return false;

            // Blocked if defender and shooter are on opposite teams
            if (shot.Team != hitterTeam)
            {
                OnShotBlocked(steamId, puckId, hitter.Username.Value.ToString());
                return true;
            }

            return false;
        }

        /// <summary>
        /// Records a blocked shot event.
        /// </summary>
        private static void OnShotBlocked(string blockerSteamId, int puckId, string blockerName)
        {
            if (!_pendingShotByPuck.TryGetValue(puckId, out var shot))
                return;

            // Increment blocker's shot blocks
            if (_currentMatch.Players.TryGetValue(blockerSteamId, out var blockStats))
                blockStats.ShotsBlocked++;

            // Record play-by-play
            _currentMatch.PlayByPlay.Add(new PlayByPlayEvent
            {
                Type = "shot_blocked",
                Team = shot.Team,
                PlayerName = shot.PlayerName,
                Detail = $"blocked by {blockerName}"
            });

            _pendingShotByPuck.Remove(puckId);
        }

        /// <summary>
        /// Resolve stale pending shots that never reached the goal
        /// and weren't blocked — these are missed shots.
        /// </summary>
        private static void ResolveStaleShots()
        {
            if (_pendingShotByPuck.Count == 0) return;

            var now = Time.time;
            var expired = new List<int>(_pendingShotByPuck.Count / 2);
            foreach (var kv in _pendingShotByPuck)
            {
                if (now - kv.Value.Timestamp > SHOT_TIMEOUT_SECONDS)
                    expired.Add(kv.Key);
            }
            foreach (var id in expired)
                _pendingShotByPuck.Remove(id);
        }

        /// <summary>
        /// Resolve pending goalie GA entries where the puck entered the
        /// goal area but no goal followed — these are saves.
        /// </summary>
        private static void ResolvePendingSaves()
        {
            if (_pendingGoalieGA.Count == 0) return;

            foreach (var kv in _pendingGoalieGA)
            {
                if (_currentMatch.Players.TryGetValue(kv.Value, out var goalieStats))
                    goalieStats.Saves++;
            }
            _pendingGoalieGA.Clear();
        }

        // ── Event handlers ───────────────────────────────────────────────

        private static void OnPuckEnterGoal(Dictionary<string, object> message)
        {
            if (!_matchActive || _currentMatch == null) return;

            // Only count shots during active Play phase — prevents dribbles,
            // practice pucks, and post-goal pucks from inflating shot totals
            var gm = GameManager.Instance;
            if (gm == null || gm.GameState.Value.Phase != GamePhase.Play) return;

            var defendingTeam = (PlayerTeam)message["team"];
            var puck = (Puck)message["puck"];
            int puckId = puck.GetInstanceID();

            // ── Resolve previous pending saves ──
            // Any _pendingGoalieGA entries not consumed by OnGoalScored = saves
            ResolvePendingSaves();

            var attackingTeam = defendingTeam == PlayerTeam.Blue ? PlayerTeam.Red : PlayerTeam.Blue;

            // ── Shooter: prefer pending shot, fall back to collision buffer ──
            string shooterSteamId = null;
            string shooterName = null;
            string shooterTeamStr = null;

            if (_pendingShotByPuck.TryGetValue(puckId, out var pendingShot))
            {
                // Use the shooter who initiated the pending shot
                shooterSteamId = pendingShot.SteamId;
                shooterName = pendingShot.PlayerName;
                shooterTeamStr = pendingShot.Team;
                _pendingShotByPuck.Remove(puckId);
            }
            else
            {
                // Fall back to collision buffer (for deflections or sub-threshold shots)
                var attackerCollisions = puck.GetPlayerCollisionsByTeam(attackingTeam);
                if (attackerCollisions.Count > 0)
                {
                    var shooter = attackerCollisions[attackerCollisions.Count - 1].Key;
                    shooterSteamId = shooter.SteamId.Value.ToString();
                    shooterName = shooter.Username.Value.ToString();
                    shooterTeamStr = attackingTeam == PlayerTeam.Blue ? "blue" : "red";
                }
            }

            if (shooterSteamId != null)
            {
                GetOrCreatePlayerStats(shooterSteamId, shooterName).Shots++;

                // Record shot in play-by-play
                _currentMatch.PlayByPlay.Add(new PlayByPlayEvent
                {
                    Type = "shot",
                    Team = shooterTeamStr,
                    PlayerName = shooterName
                });

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
                    _pendingGoalieGA[puckId] = gid;
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

            // ── Cache player IDs at handler entry ──
            var scorerId = goalPlayer.SteamId.Value.ToString();
            var scorerName = goalPlayer.Username.Value.ToString();

            // ── Goal scorer ──
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
                var onIcePlayers = pm.GetPlayers();
                var matchPlayers = _currentMatch.Players; // cache
                foreach (var player in onIcePlayers)
                {
                    if (player.Phase == PlayerPhase.Play)
                    {
                        var sid = player.SteamId.Value.ToString();
                        var stats = GetOrCreatePlayerStats(sid, player.Username.Value.ToString());
                        if (player.Team == scoringTeam)
                            stats.PlusMinus++;
                        else if (player.Team == defendingTeam)
                            stats.PlusMinus--;

                        // Keep session info up to date for live view
                        _playerLiveInfo[sid] = new PlayerLiveInfo
                        {
                            Number = player.Number.Value,
                            Team = player.Team == PlayerTeam.Blue ? "blue" : "red",
                            Role = player.Role == PlayerRole.Goalie ? "goalie" : "attacker"
                        };
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

            // Record goal in play-by-play
            var assistParts = new List<string>();
            if (assistPlayer != null && assistPlayer.SteamId.Value.Value.Length > 0)
                assistParts.Add(assistPlayer.Username.Value.ToString());
            if (secondAssist != null && secondAssist.SteamId.Value.Value.Length > 0)
                assistParts.Add(secondAssist.Username.Value.ToString());
            var assistDetail = assistParts.Count > 0 ? $"({string.Join(", ", assistParts)})" : "";

            _currentMatch.PlayByPlay.Add(new PlayByPlayEvent
            {
                Type = "goal",
                Team = scoringTeam == PlayerTeam.Blue ? "blue" : "red",
                PlayerName = scorerName,
                Detail = assistDetail
            });

            Plugin.Log($"Goal: {scorerName} — {_currentMatch.BlueScore}-{_currentMatch.RedScore}");

            // Don't PushLive here — 30s periodic timer handles it
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

            // Play starting after FaceOff → arm faceoff detection + push live
            // (GameState.Tick is now tickRemainder = period clock, not faceoff timer)
            if (newState.Phase == GamePhase.Play && oldState.Phase == GamePhase.FaceOff)
            {
                _pendingFaceoff = _matchActive;
                if (_matchActive && _currentMatch != null)
                    PushLive();
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

            // Period transition → update period + push live state
            if (newState.Phase == GamePhase.Intermission || newState.Phase == GamePhase.FaceOff)
            {
                if (_matchActive && _currentMatch != null)
                {
                    var gm = GameManager.Instance;
                    if (gm != null)
                    {
                        var newPeriod = gm.GameState.Value.Period;

                        // Period ended: Play → Intermission
                        if (oldState.Phase == GamePhase.Play && newState.Phase == GamePhase.Intermission)
                        {
                            _currentMatch.PlayByPlay.Add(new PlayByPlayEvent
                            {
                                Type = "period",
                                PlayerName = $"End of Period {oldState.Period}"
                            });
                        }

                        // Period started: Intermission → FaceOff
                        if (oldState.Phase == GamePhase.Intermission && newState.Phase == GamePhase.FaceOff)
                        {
                            _currentMatch.PlayByPlay.Add(new PlayByPlayEvent
                            {
                                Type = "period",
                                PlayerName = $"Period {newPeriod}"
                            });
                        }

                        _currentMatch.Period = newPeriod;
                    }
                    PushLive();
                }
            }

            // 30-second periodic live push (main-thread safe, no background timer)
            if (_matchActive && _currentMatch != null && (DateTime.UtcNow - _lastPushTime).TotalSeconds >= 30)
            {
                _lastPushTime = DateTime.UtcNow;
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
                await FetchServerNameFromPuckStats(capturedPort);
            });
        }

        private static async System.Threading.Tasks.Task FetchServerNameFromPuckStats(ushort? ownPort)
        {
            const string ip = "207.2.120.215";

            try
            {
                if (ownPort == null)
                {
                    Plugin.Log("No server port available, skipping puckstats.io fetch");
                    return;
                }

                var client = StatsApi.Client;
                var url = $"https://puckstats.io/api/server-list/server?ip={ip}&port={ownPort}";
                var json = await client.GetStringAsync(url);

                var data = JObject.Parse(json);
                var names = data["names"] as JArray;
                if (names != null && names.Count > 0)
                {
                    var firstName = names[0]["name"]?.Value<string>();
                    if (!string.IsNullOrEmpty(firstName))
                    {
                        _serverName = firstName;
                        Plugin.Log($"Server name '{_serverName}' from puckstats.io API (port {ownPort})");
                        return;
                    }
                }

                Plugin.Log($"No server name found at port {ownPort}");
            }
            catch (Exception ex)
            {
                Plugin.Log($"Failed to fetch from puckstats.io: {ex.Message}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static void StartNewMatch()
        {
            _currentMatch = new MatchRecord
            {
                Timestamp = DateTime.UtcNow.ToString("o")
            };
            _matchActive = true;
            _pendingGoalieGA.Clear();
            _playerPlayEnterTime.Clear();
            _pendingFaceoff = false;
            _goalIndex = 0;

            // Add Period 1 start marker at beginning of play-by-play
            _currentMatch.PlayByPlay.Add(new PlayByPlayEvent
            {
                Type = "period",
                PlayerName = "Period 1"
            });

            // Snapshot current players
            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            _playerLiveInfo.Clear();
            if (pm != null)
            {
                foreach (var player in pm.GetPlayers())
                {
                    var sid = player.SteamId.Value.ToString();
                    var name = player.Username.Value.ToString();
                    GetOrCreatePlayerStats(sid, name);
                    _playerLiveInfo[sid] = new PlayerLiveInfo
                    {
                        Number = player.Number.Value,
                        Team = player.Team == PlayerTeam.Blue ? "blue" : player.Team == PlayerTeam.Red ? "red" : "",
                        Role = player.Role == PlayerRole.Goalie ? "goalie" : "attacker"
                    };
                }
            }

            Plugin.Log("New match started");

            // Push initial live state
            _lastPushTime = DateTime.UtcNow;
            PushLive();
        }

        private static void FinalizeMatch()
        {
            if (_currentMatch == null) return;

            // Resolve any remaining pending shots and saves
            ResolveStaleShots();
            ResolvePendingSaves();

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
            PatchPuckCollisions.ClearDictionaries();

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
            var finalState = new LiveMatchState
            {
                MatchId = _currentMatch.MatchId,
                ServerName = _serverName,
                Active = false,
                Period = _currentMatch.Period,
                BlueScore = _currentMatch.BlueScore,
                RedScore = _currentMatch.RedScore,
                TimeRemaining = 0,
                Goals = _currentMatch.Goals,
                PlayByPlay = _currentMatch.PlayByPlay
            };
            // Include final player stats
            foreach (var kv in _currentMatch.Players)
            {
                if (_playerLiveInfo.TryGetValue(kv.Key, out var li))
                {
                    finalState.Players.Add(new LivePlayerEntry
                    {
                        SteamId = kv.Key,
                        Username = kv.Value.Username,
                        Number = li.Number,
                        Team = li.Team,
                        Role = li.Role,
                        Goals = kv.Value.Goals,
                        Assists = kv.Value.Assists,
                        Shots = kv.Value.Shots,
                        ShotAttempts = kv.Value.ShotAttempts,
                        Saves = kv.Value.Saves,
                        ShotsAgainst = kv.Value.ShotsAgainst,
                        GoalsAgainst = kv.Value.GoalsAgainst,
                        PlusMinus = kv.Value.PlusMinus,
                        Hits = kv.Value.Hits,
                        ShotsBlocked = kv.Value.ShotsBlocked,
                        FaceoffWins = kv.Value.FaceoffWins,
                        FaceoffLosses = kv.Value.FaceoffLosses,
                        TimeOnIceSeconds = kv.Value.TimeOnIceSeconds,
                    });
                }
            }
            StatsApi.PushLiveState(finalState);

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

            // Snapshot session info for live view (survives disconnect)
            if (!_playerLiveInfo.ContainsKey(steamId))
            {
                var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (pm != null)
                {
                    var p = pm.GetPlayerBySteamId(steamId);
                    if (p != null)
                    {
                        _playerLiveInfo[steamId] = new PlayerLiveInfo
                        {
                            Number = p.Number.Value,
                            Team = p.Team == PlayerTeam.Blue ? "blue" : p.Team == PlayerTeam.Red ? "red" : "",
                            Role = p.Role == PlayerRole.Goalie ? "goalie" : "attacker"
                        };
                    }
                }
            }

            return stats;
        }

        // ── Live state push ─────────────────────────────────────────────

        private static void PushLive()
        {
            if (_currentMatch == null) return;

            // Resolve stale shots and saves before pushing
            ResolveStaleShots();
            ResolvePendingSaves();

            var state = new LiveMatchState
            {
                MatchId = _currentMatch.MatchId,
                ServerName = _serverName,
                Active = true,
                Period = _currentMatch.Period,
                BlueScore = _currentMatch.BlueScore,
                RedScore = _currentMatch.RedScore,
                TimeRemaining = GameManager.Instance?.GameState.Value.Tick ?? 0
            };

            // Include goal events and play-by-play
            state.Goals = _currentMatch.Goals;
            state.PlayByPlay = _currentMatch.PlayByPlay;

            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;

            // Iterate ALL players who participated (not just currently connected)
            foreach (var kv in _currentMatch.Players)
            {
                var sid = kv.Key;
                var stats = kv.Value;

                // Try to get current connection info from PlayerManager
                var livePlayer = pm?.GetPlayerBySteamId(sid);
                string team, role;
                int number;

                if (livePlayer != null)
                {
                    // Player is still connected — use live data
                    if (livePlayer.Team == PlayerTeam.None || livePlayer.Team == PlayerTeam.Spectator)
                        continue; // skip spectators in live view

                    number = livePlayer.Number.Value;
                    team = livePlayer.Team == PlayerTeam.Blue ? "blue" : "red";
                    role = livePlayer.Role == PlayerRole.Goalie ? "goalie" : "attacker";

                    // Update stored session info
                    _playerLiveInfo[sid] = new PlayerLiveInfo { Number = number, Team = team, Role = role };
                }
                else
                {
                    // Player disconnected — use stored session info
                    if (!_playerLiveInfo.TryGetValue(sid, out var stored))
                        continue; // never seen this player, skip

                    if (string.IsNullOrEmpty(stored.Team) || stored.Team == "")
                        continue;

                    number = stored.Number;
                    team = stored.Team;
                    role = stored.Role;
                }

                state.Players.Add(new LivePlayerEntry
                {
                    SteamId = sid,
                    Username = stats.Username,
                    Number = number,
                    Team = team,
                    Role = role,
                    Goals = stats.Goals,
                    Assists = stats.Assists,
                    Shots = stats.Shots,
                    ShotAttempts = stats.ShotAttempts,
                    Saves = stats.Saves,
                    ShotsAgainst = stats.ShotsAgainst,
                    GoalsAgainst = stats.GoalsAgainst,
                    PlusMinus = stats.PlusMinus,
                    Hits = stats.Hits,
                    ShotsBlocked = stats.ShotsBlocked,
                    FaceoffWins = stats.FaceoffWins,
                    FaceoffLosses = stats.FaceoffLosses,
                    TimeOnIceSeconds = stats.TimeOnIceSeconds,
                });
            }

            StatsApi.PushLiveState(state);
        }
    }
}
