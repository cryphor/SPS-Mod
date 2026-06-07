using System;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;

namespace SPSMod
{
    public static class StatsApi
    {
        private static readonly HttpClient _client = new();

        // ── CHANGE THIS to your Vercel deployment URL ──
        private const string ApiUrl = "https://spsdashboard.vercel.app/api/stats";

        public static StatsDatabase Load()
        {
            try
            {
                var response = _client.GetAsync(ApiUrl).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return JsonConvert.DeserializeObject<StatsDatabase>(json) ?? new StatsDatabase();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log($"StatsApi.Load failed: {ex.Message}");
            }

            return new StatsDatabase();
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
