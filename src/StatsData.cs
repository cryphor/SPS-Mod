using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SPSMod
{
    // ─── Per-player stats for one match ──────────────────────────────────────
    public class PlayerMatchStats
    {
        [JsonProperty("steamId")]
        public string SteamId { get; set; } = "";

        [JsonProperty("username")]
        public string Username { get; set; } = "";

        [JsonProperty("goals")]
        public int Goals { get; set; }

        [JsonProperty("assists")]
        public int Assists { get; set; }

        [JsonProperty("shots")]
        public int Shots { get; set; }

        [JsonProperty("passes")]
        public int Passes { get; set; }

        [JsonProperty("saves")]
        public int Saves { get; set; }

        [JsonProperty("shotsAgainst")]
        public int ShotsAgainst { get; set; }

        [JsonProperty("goalsAgainst")]
        public int GoalsAgainst { get; set; }

        [JsonProperty("plusMinus")]
        public int PlusMinus { get; set; }

        // ── New NHL-style stats ──

        [JsonProperty("shotAttempts")]
        public int ShotAttempts { get; set; }

        [JsonProperty("gameWinningGoal")]
        public bool GameWinningGoal { get; set; }

        [JsonProperty("timeOnIceSeconds")]
        public int TimeOnIceSeconds { get; set; }

        [JsonProperty("faceoffWins")]
        public int FaceoffWins { get; set; }

        [JsonProperty("faceoffLosses")]
        public int FaceoffLosses { get; set; }

        [JsonProperty("hits")]
        public int Hits { get; set; }

        [JsonProperty("shotsBlocked")]
        public int ShotsBlocked { get; set; }

        // computed
        [JsonIgnore]
        public int Points => Goals + Assists;

        [JsonIgnore]
        public double ShootingPercentage =>
            Shots > 0 ? Math.Round((double)Goals / Shots * 100, 1) : 0.0;

        [JsonIgnore]
        public double SavePercentage =>
            ShotsAgainst > 0
                ? Math.Round((double)(ShotsAgainst - GoalsAgainst) / ShotsAgainst * 100, 1)
                : 0.0;

        [JsonIgnore]
        public double GAA =>
            GoalsAgainst > 0
                ? Math.Round((double)GoalsAgainst * 60.0 / 20.0, 2)
                : 0.0;

        [JsonIgnore]
        public double FaceoffPct =>
            (FaceoffWins + FaceoffLosses) > 0
                ? Math.Round((double)FaceoffWins / (FaceoffWins + FaceoffLosses) * 100, 1)
                : 0.0;

        [JsonIgnore]
        public string TimeOnIceFormatted =>
            $"{TimeOnIceSeconds / 60}:{TimeOnIceSeconds % 60:D2}";
    }

    // ─── One goal event in a match ──────────────────────────────────────────
    public class GoalEvent
    {
        [JsonProperty("scoringTeam")]
        public string ScoringTeam { get; set; } = ""; // "blue" or "red"

        [JsonProperty("scorerSteamId")]
        public string ScorerSteamId { get; set; } = "";

        [JsonProperty("assistSteamIds")]
        public List<string> AssistSteamIds { get; set; } = new();

        [JsonProperty("goalIndex")]
        public int GoalIndex { get; set; } // 1st goal of game, 2nd, etc.
    }

    // ─── Play-by-play event (goal, shot, faceoff) ─────────────────────────
    public class PlayByPlayEvent
    {
        [JsonProperty("type")]
        public string Type { get; set; } = ""; // "goal", "shot", "faceoff"

        [JsonProperty("team")]
        public string Team { get; set; } = ""; // "blue" or "red"

        [JsonProperty("playerName")]
        public string PlayerName { get; set; } = "";

        [JsonProperty("detail")]
        public string Detail { get; set; } = ""; // assists for goals, "won" for faceoff
    }

    // ─── Per-team stats for one match ────────────────────────────────────────
    public class TeamMatchStats
    {
        [JsonProperty("goalsFor")]
        public int GoalsFor { get; set; }

        [JsonProperty("goalsAgainst")]
        public int GoalsAgainst { get; set; }

        [JsonProperty("shotsFor")]
        public int ShotsFor { get; set; }

        [JsonProperty("shotsAgainst")]
        public int ShotsAgainst { get; set; }
    }

    // ─── One completed match ─────────────────────────────────────────────────
    public class MatchRecord
    {
        [JsonProperty("matchId")]
        public string MatchId { get; set; } = Guid.NewGuid().ToString("N")[..8];

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

        [JsonProperty("blueScore")]
        public int BlueScore { get; set; }

        [JsonProperty("redScore")]
        public int RedScore { get; set; }

        [JsonProperty("period")]
        public int Period { get; set; }

        [JsonProperty("players")]
        public Dictionary<string, PlayerMatchStats> Players { get; set; } = new();

        [JsonProperty("goals")]
        public List<GoalEvent> Goals { get; set; } = new();

        [JsonProperty("playByPlay")]
        public List<PlayByPlayEvent> PlayByPlay { get; set; } = new();

        [JsonProperty("blueTeam")]
        public TeamMatchStats BlueTeam { get; set; } = new();

        [JsonProperty("redTeam")]
        public TeamMatchStats RedTeam { get; set; } = new();
    }

    // ─── Full stats database (persisted as JSON) ─────────────────────────────
    public class StatsDatabase
    {
        [JsonProperty("matches")]
        public List<MatchRecord> Matches { get; set; } = new();
    }

    // ─── Live match state (pushed to API every goal/period) ──────────────────
    public class LiveMatchState
    {
        [JsonProperty("matchId")]
        public string MatchId { get; set; } = "";

        [JsonProperty("serverName")]
        public string ServerName { get; set; } = "";

        [JsonProperty("period")]
        public int Period { get; set; }

        [JsonProperty("blueScore")]
        public int BlueScore { get; set; }

        [JsonProperty("redScore")]
        public int RedScore { get; set; }

        [JsonProperty("active")]
        public bool Active { get; set; }

        [JsonProperty("timeRemaining")]
        public int TimeRemaining { get; set; }

        [JsonProperty("players")]
        public List<LivePlayerEntry> Players { get; set; } = new();

        [JsonProperty("goals")]
        public List<GoalEvent> Goals { get; set; } = new();

        [JsonProperty("playByPlay")]
        public List<PlayByPlayEvent> PlayByPlay { get; set; } = new();
    }

    public class LivePlayerEntry
    {
        [JsonProperty("steamId")]
        public string SteamId { get; set; } = "";

        [JsonProperty("username")]
        public string Username { get; set; } = "";

        [JsonProperty("number")]
        public int Number { get; set; }

        [JsonProperty("team")]
        public string Team { get; set; } = ""; // "blue" or "red"

        [JsonProperty("role")]
        public string Role { get; set; } = ""; // "attacker" or "goalie"

        [JsonProperty("goals")]
        public int Goals { get; set; }

        [JsonProperty("assists")]
        public int Assists { get; set; }

        [JsonProperty("shots")]
        public int Shots { get; set; }

        [JsonProperty("saves")]
        public int Saves { get; set; }

        [JsonProperty("shotsAgainst")]
        public int ShotsAgainst { get; set; }

        [JsonProperty("goalsAgainst")]
        public int GoalsAgainst { get; set; }

        [JsonProperty("plusMinus")]
        public int PlusMinus { get; set; }

        [JsonProperty("shotAttempts")]
        public int ShotAttempts { get; set; }

        [JsonProperty("hits")]
        public int Hits { get; set; }

        [JsonProperty("shotsBlocked")]
        public int ShotsBlocked { get; set; }

        [JsonProperty("faceoffWins")]
        public int FaceoffWins { get; set; }

        [JsonProperty("faceoffLosses")]
        public int FaceoffLosses { get; set; }

        [JsonProperty("timeOnIceSeconds")]
        public int TimeOnIceSeconds { get; set; }
    }
}
