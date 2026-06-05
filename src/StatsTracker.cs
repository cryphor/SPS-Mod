using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SPSMod
{
    public static class StatsTracker
    {
        private static MatchRecord _currentMatch;
        private static bool _matchActive;

        // Players in Play phase per team (for PP detection)
        private static int _bluePlayCount;
        private static int _redPlayCount;
        private static bool _blueOnPP;
        private static bool _redOnPP;

        // Track goalie who was last on defense when puck entered goal
        private static readonly Dictionary<int, string> _pendingGoalieGA = new();

        // ── TOI tracking ──
        private static readonly Dictionary<string, float> _playerPlayEnterTime = new();

        // ── Faceoff tracking ──
        private static bool _pendingFaceoff;
        private static string _firstTouchAfterFaceoff; // steamId

        // ── Goal sequence for GWG computation ──
        private static int _goalIndex;

        // ── Blocked shot tracking ──
        // For each puck, track which defending players touched it since last attacking touch
        // puck instance ID → set of defending steamIds who've touched it since last shot
        private static readonly Dictionary<int, HashSet<string>> _defenderTouchesSinceLastShot = new();

        private static StatsDatabase _database;

        // ── Init / Shutdown ──────────────────────────────────────────────

        public static void Initialize()
        {
            _database = StatsApi.Load();
            _matchActive = false;

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

        public static void RecordHit(string steamId)
        {
            if (!_matchActive || _currentMatch == null) return;

            if (_currentMatch.Players.TryGetValue(steamId, out var stats))
                stats.Hits++;
        }

        public static void RecordFaceoffWin(string steamId)
        {
            if (!_matchActive || _currentMatch == null || !_pendingFaceoff) return;

            if (_currentMatch.Players.TryGetValue(steamId, out var stats))
            {
                stats.FaceoffWins++;

                // Give faceoff loss to all opponents on the ice
                var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (pm != null)
                {
                    var winnerTeam = pm.GetPlayerBySteamId(steamId)?.Team ?? PlayerTeam.None;
                    foreach (var player in pm.GetPlayers())
                    {
                        if (player.Phase == PlayerPhase.Play && player.Team != winnerTeam && player.Team != PlayerTeam.None && player.Team != PlayerTeam.Spectator)
                        {
                            var sid = player.SteamId.Value.ToString();
                            if (_currentMatch.Players.TryGetValue(sid, out var loserStats))
                                loserStats.FaceoffLosses++;
                        }
                    }
                }
            }

            _pendingFaceoff = false;
        }

        public static void RecordDefenderTouch(string puckId, string steamId, PlayerTeam defenderTeam)
        {
            if (!_matchActive || _currentMatch == null) return;

            if (!_defenderTouchesSinceLastShot.ContainsKey(puckId))
                _defenderTouchesSinceLastShot[puckId] = new HashSet<string>();

            _defenderTouchesSinceLastShot[puckId].Add(steamId);
        }

        public static void OnPuckStickHit(string steamId, int puckId)
        {
            // If we're waiting for a faceoff touch, this is the faceoff win
            if (_pendingFaceoff)
            {
                RecordFaceoffWin(steamId);
            }

            // Reset defender touches for this puck (attacker just touched it)
            _defenderTouchesSinceLastShot.Remove(puckId);
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
            }

            // ── Goalie involvement on defending side ──
            var defenderCollisions = puck.GetPlayerCollisionsByTeam(defendingTeam);
            foreach (var kv in defenderCollisions)
            {
                var defender = kv.Key;
                if (defender.Role == PlayerRole.Goalie)
                {
                    var gid = defender.SteamId.Value.ToString();
                    var gname = defender.Username.Value.ToString();
                    GetOrCreatePlayerStats(gid, gname).ShotsAgainst++;

                    _pendingGoalieGA[puck.GetInstanceID()] = gid;
                    break;
                }
            }

            // ── Blocked shots: defending players who touched puck after last attacker touch ──
            int puckId = puck.GetInstanceID();
            if (_defenderTouchesSinceLastShot.TryGetValue(puckId, out var defenders))
            {
                foreach (var defSid in defenders)
                {
                    if (_currentMatch.Players.TryGetValue(defSid, out var blockStats))
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

            // ── PPG / SHG ──
            bool isPPG = scoringTeam == PlayerTeam.Blue ? _blueOnPP : _redOnPP;
            bool isSHG = scoringTeam == PlayerTeam.Blue ? _redOnPP : _blueOnPP;

            if (isPPG)
            {
                scorerStats.PowerPlayGoals++;
                if (scoringTeam == PlayerTeam.Blue)
                    _currentMatch.BlueTeam.PPG++;
                else
                    _currentMatch.RedTeam.PPG++;
            }

            if (isSHG)
            {
                scorerStats.ShortHandedGoals++;
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
                IsPowerPlay = isPPG,
                IsShortHanded = isSHG,
                GoalIndex = _goalIndex
            };

            if (assistPlayer != null && assistPlayer.SteamId.Value.Value.Length > 0)
                goalEvent.AssistSteamIds.Add(assistPlayer.SteamId.Value.ToString());
            if (secondAssist != null && secondAssist.SteamId.Value.Value.Length > 0)
                goalEvent.AssistSteamIds.Add(secondAssist.SteamId.Value.ToString());

            _currentMatch.Goals.Add(goalEvent);

            // ── Team shots tracking ──
            _currentMatch.BlueTeam.ShotsFor += CountTeamShots(PlayerTeam.Blue);
            _currentMatch.RedTeam.ShotsFor += CountTeamShots(PlayerTeam.Red);

            Plugin.Log($"Goal: {scorerName} — {_currentMatch.BlueScore}-{_currentMatch.RedScore}" +
                       (isPPG ? " (PPG)" : "") + (isSHG ? " (SHG)" : ""));
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

            UpdatePlayCounts();
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
            _defenderTouchesSinceLastShot.Clear();
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

            UpdatePlayCounts();
            Plugin.Log("New match started");
        }

        private static void FinalizeMatch()
        {
            if (_currentMatch == null) return;

            // Flush any remaining TOI (players still in Play phase)
            FlushRemainingTOI();

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

            _matchActive = false;
            var mId = _currentMatch.MatchId;
            _currentMatch = null;

            Plugin.Log($"Match {mId} finalized and saved");
        }

        private static void FlushRemainingTOI()
        {
            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm == null) return;

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

            _playerPlayEnterTime.Clear();
        }

        private static void ComputeGameWinningGoal()
        {
            if (_currentMatch.Goals.Count == 0) return;

            int blueFinal = _currentMatch.BlueScore;
            int redFinal = _currentMatch.RedScore;

            if (blueFinal == redFinal) return; // tie — no GWG

            bool blueWon = blueFinal > redFinal;
            int losingFinalScore = blueWon ? redFinal : blueFinal;

            // Simulate the game goal by goal to find the GWG
            int blueScore = 0;
            int redScore = 0;

            foreach (var goal in _currentMatch.Goals)
            {
                if (goal.ScoringTeam == "blue") blueScore++;
                else redScore++;

                int winningTeamScore = blueWon ? blueScore : redScore;

                // If this is the first goal where the winning team's score
                // exceeds what the losing team will end with, it's the GWG
                if (winningTeamScore > losingFinalScore)
                {
                    // Mark the scorer
                    if (_currentMatch.Players.TryGetValue(goal.ScorerSteamId, out var scorer))
                        scorer.GameWinningGoal = true;
                    break;
                }
            }
        }

        private static int CountTeamShots(PlayerTeam team)
        {
            int count = 0;
            foreach (var kv in _currentMatch.Players)
            {
                if (/* we don't have team in PlayerMatchStats, approximate via */
                    true) // This is an approximation - we count all shots
                {
                    count += kv.Value.Shots;
                }
            }
            return count;
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

        private static void UpdatePlayCounts()
        {
            _bluePlayCount = 0;
            _redPlayCount = 0;

            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm == null) return;

            foreach (var player in pm.GetPlayers())
            {
                if (player.Phase == PlayerPhase.Play)
                {
                    if (player.Team == PlayerTeam.Blue) _bluePlayCount++;
                    else if (player.Team == PlayerTeam.Red) _redPlayCount++;
                }
            }

            _blueOnPP = _bluePlayCount > _redPlayCount;
            _redOnPP = _redPlayCount > _bluePlayCount;
        }
    }
}
