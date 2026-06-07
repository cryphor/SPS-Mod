using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Unity.Netcode;
using UnityEngine;

namespace SPSMod
{
    public static class ChatTracker
    {
        private const string ChatApiUrl = "https://spsdashboard.vercel.app/api/admin/chat-logs";
        private const string EventsApiUrl = "https://spsdashboard.vercel.app/api/admin/events";

        private static readonly HttpClient _client = new();
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            EventManager.AddEventListener("Event_OnChatMessageAdded", OnChatMessage);
            Plugin.Log("ChatTracker initialized");
        }

        public static void Shutdown()
        {
            if (!_initialized) return;
            _initialized = false;

            try { EventManager.RemoveEventListener("Event_OnChatMessageAdded", OnChatMessage); }
            catch { /* ignore */ }
        }

        private static void OnChatMessage(Dictionary<string, object> message)
        {
            try
            {
                if (!NetworkManager.Singleton.IsServer) return;

                // Extract ChatMessage from event
                var chatMsg = message["chatMessage"] as ChatMessage;
                if (chatMsg == null) return;
                if (chatMsg.IsSystem) return; // skip system messages

                var serverName = GetServerName();

                var payload = new Dictionary<string, object>
                {
                    ["steamId"] = chatMsg.SteamID?.ToString() ?? "",
                    ["username"] = chatMsg.Username?.ToString() ?? "???",
                    ["content"] = chatMsg.Content.ToString() ?? "",
                    ["teamChat"] = chatMsg.IsTeamChat,
                    ["serverName"] = serverName,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };

                var json = JsonConvert.SerializeObject(payload, Formatting.None);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Fire-and-forget POST
                _ = _client.PostAsync(ChatApiUrl, content).ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Plugin.LogWarning($"ChatTracker POST failed: {t.Exception.InnerException?.Message}");
                });
            }
            catch (Exception ex)
            {
                Plugin.LogWarning($"ChatTracker.OnChatMessage error: {ex.Message}");
            }
        }

        public static void PostEvent(string type, string targetSteamId, string targetUsername,
            string issuerSteamId, string issuerUsername, string reason = "")
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["type"] = type,
                    ["targetSteamId"] = targetSteamId ?? "",
                    ["targetUsername"] = targetUsername ?? "",
                    ["issuerSteamId"] = issuerSteamId ?? "",
                    ["issuerUsername"] = issuerUsername ?? "",
                    ["reason"] = reason ?? "",
                    ["serverName"] = GetServerName(),
                };

                var json = JsonConvert.SerializeObject(payload, Formatting.None);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _ = _client.PostAsync(EventsApiUrl, content).ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Plugin.LogWarning($"ChatTracker.PostEvent failed: {t.Exception.InnerException?.Message}");
                });
            }
            catch (Exception ex)
            {
                Plugin.LogWarning($"ChatTracker.PostEvent error: {ex.Message}");
            }
        }

        private static string GetServerName()
        {
            try
            {
                var sm = NetworkBehaviourSingleton<ServerManager>.Instance;
                return sm?.ServerConfig?.name ?? "Unknown";
            }
            catch { return "Unknown"; }
        }
    }
}
