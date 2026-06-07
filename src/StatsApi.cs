using System;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;

namespace SPSMod
{
    public static class StatsApi
    {
        internal static readonly HttpClient _client = new();

        public static HttpClient Client => _client;

        // ── CHANGE THIS to your Vercel deployment URL ──
        private const string ApiUrl = "https://spsdashboard.vercel.app/api/stats";

        public static StatsDatabase Load()
        {
            // Return fresh DB immediately — non-blocking
            // Real data is fetched via LoadAsync which replaces _database in StatsTracker
            ScheduleAsyncLoad();
            return new StatsDatabase();
        }

        private static void ScheduleAsyncLoad()
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var response = await _client.GetAsync(ApiUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var db = JsonConvert.DeserializeObject<StatsDatabase>(json);
                        if (db != null)
                        {
                            StatsTracker.ReplaceDatabase(db);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log($"StatsApi.Load failed: {ex.Message}");
                }
            });
        }

        public static void Save(StatsDatabase db)
        {
            try
            {
                var json = JsonConvert.SerializeObject(db, Formatting.None);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _ = _client.PostAsync(ApiUrl, content).ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Plugin.LogWarning($"StatsApi.Save failed: {t.Exception.InnerException?.Message}");
                });
            }
            catch (Exception ex)
            {
                Plugin.Log($"StatsApi.Save failed: {ex.Message}");
            }
        }

        private const string LiveApiUrl = "https://spsdashboard.vercel.app/api/live";

        public static void PushLiveState(LiveMatchState state)
        {
            try
            {
                var json = JsonConvert.SerializeObject(state, Formatting.None);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _ = _client.PostAsync(LiveApiUrl, content).ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Plugin.LogWarning($"PushLiveState failed: {t.Exception.InnerException?.Message}");
                });
            }
            catch (Exception ex)
            {
                Plugin.LogWarning($"PushLiveState error: {ex.Message}");
            }
        }
    }
}
