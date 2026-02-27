using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web;

namespace PlutoForChannels
{
    public class PlutoClient
    {
        private readonly HttpClient _httpClient;
        
        // Thread-safe caching for the session tokens (valid for 4 hours)
        private readonly ConcurrentDictionary<string, JsonNode> _responseList = new();
        private readonly ConcurrentDictionary<string, DateTime> _sessionAt = new();

        private readonly Dictionary<string, string> _xForward = new()
        {
            { "local", "" },
            { "uk", "178.238.11.6" },
            { "ca", "192.206.151.131" },
            { "fr", "193.169.64.141" },
            { "de", "81.173.176.155" },
            { "us_east", "108.82.206.181" },
            { "us_west", "76.81.9.69" }
        };

        public PlutoClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public void ClearCache()
        {
            _responseList.Clear();
            _sessionAt.Clear();
        }

        private System.Collections.Generic.List<(string username, string password)> GetValidAccounts()
        {
            var accounts = new System.Collections.Generic.List<(string, string)>();
            var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (System.IO.File.Exists(settingsPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(settingsPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    
                    string u1 = root.TryGetProperty("Username", out var ue) ? ue.GetString() ?? "" : "";
                    string p1 = root.TryGetProperty("Password", out var pe) ? pe.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(u1) && !string.IsNullOrEmpty(p1)) accounts.Add((u1, p1));

                    string u2 = root.TryGetProperty("Username2", out var ue2) ? ue2.GetString() ?? "" : "";
                    string p2 = root.TryGetProperty("Password2", out var pe2) ? pe2.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(u2) && !string.IsNullOrEmpty(p2)) accounts.Add((u2, p2));

                    string u3 = root.TryGetProperty("Username3", out var ue3) ? ue3.GetString() ?? "" : "";
                    string p3 = root.TryGetProperty("Password3", out var pe3) ? pe3.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(u3) && !string.IsNullOrEmpty(p3)) accounts.Add((u3, p3));

                    string u4 = root.TryGetProperty("Username4", out var ue4) ? ue4.GetString() ?? "" : "";
                    string p4 = root.TryGetProperty("Password4", out var pe4) ? pe4.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(u4) && !string.IsNullOrEmpty(p4)) accounts.Add((u4, p4));
                }
                catch { }
            }
            if (accounts.Count == 0) accounts.Add(("", "")); // Fallback
            return accounts;
        }

        public async Task<JsonNode?> GetBootDataAsync(string countryCode, int accountIndex = 0)
        {
            string cacheKey = $"{countryCode}_{accountIndex}";
            
            if (_responseList.TryGetValue(cacheKey, out var cachedResponse) && 
                _sessionAt.TryGetValue(cacheKey, out var sessionTime))
            {
                if ((DateTime.UtcNow - sessionTime).TotalHours < 4)
                {
                    return cachedResponse;
                }
            }

            var query = HttpUtility.ParseQueryString(string.Empty);
            query["appName"] = "web";
            query["appVersion"] = "8.0.0-111b2b9dc00bd0bea9030b30662159ed9e7c8bc6";
            query["deviceVersion"] = "122.0.0";
            query["deviceModel"] = "web";
            query["deviceMake"] = "chrome";
            query["deviceType"] = "web";
            query["clientID"] = "c63f9fbf-47f5-40dc-941c-5628558aec87";
            query["clientModelNumber"] = "1.0.0";
            query["serverSideAds"] = "false";
            query["drmCapabilities"] = "widevine:L3";

            var accounts = GetValidAccounts();
            var (username, password) = accounts[accountIndex % accounts.Count];
            
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                query["username"] = username;
                query["password"] = password;
            }

            var requestUri = $"https://boot.pluto.tv/v4/start?{query}";

            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("authority", "boot.pluto.tv");
            request.Headers.Add("origin", "https://pluto.tv");
            request.Headers.Add("referer", "https://pluto.tv/");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

            if (_xForward.TryGetValue(countryCode, out var ip) && !string.IsNullOrEmpty(ip))
            {
                request.Headers.Add("X-Forwarded-For", ip);
            }

            try
            {
                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    App.LogToConsole($"[ERROR] HTTP failure {response.StatusCode} for {countryCode} (Account {accountIndex % accounts.Count + 1})");
                    return null;
                }

                var jsonResponse = await response.Content.ReadFromJsonAsync<JsonNode>();
                
                if (jsonResponse != null)
                {
                    _responseList[cacheKey] = jsonResponse;
                    _sessionAt[cacheKey] = DateTime.UtcNow;
                    App.LogToConsole($"New token for {countryCode} (Account {accountIndex % accounts.Count + 1}) generated at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    return jsonResponse;
                }
            }
            catch (Exception ex)
            {
                App.LogToConsole($"[ERROR] Exception fetching boot data: {ex.Message}");
            }

            return null;
        }
		
        public async Task<JsonNode?> GetTimelinesAsync(string countryCode, string channelIds, string startTime)
        {
            var bootData = await GetBootDataAsync(countryCode, 0);
            var token = bootData?["sessionToken"]?.ToString();

            if (string.IsNullOrEmpty(token)) return null;

            var requestUri = $"https://service-channels.clusters.pluto.tv/v2/guide/timelines?start={startTime}&channelIds={channelIds}&duration=720";

            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("authority", "service-channels.clusters.pluto.tv");
            request.Headers.Add("authorization", $"Bearer {token}");
            request.Headers.Add("origin", "https://pluto.tv");
            request.Headers.Add("referer", "https://pluto.tv/");

            if (_xForward.TryGetValue(countryCode, out var ip) && !string.IsNullOrEmpty(ip))
            {
                request.Headers.Add("X-Forwarded-For", ip);
            }

            try
            {
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<JsonNode>();
                }
            }
            catch (Exception ex)
            {
                App.LogToConsole($"[ERROR] Fetching timelines: {ex.Message}");
            }

            return null;
        }

        public async Task<List<Channel>> GetChannelsAsync(string countryCode)
        {
            if (countryCode.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return await GetAllChannelsAsync();
            }

            var bootData = await GetBootDataAsync(countryCode, 0);
            var token = bootData?["sessionToken"]?.ToString();

            if (string.IsNullOrEmpty(token))
            {
                App.LogToConsole($"[ERROR] Failed to retrieve session token for {countryCode}.");
                return new List<Channel>();
            }

            var headers = new Dictionary<string, string>
            {
                { "authority", "service-channels.clusters.pluto.tv" },
                { "authorization", $"Bearer {token}" },
                { "origin", "https://pluto.tv" },
                { "referer", "https://pluto.tv/" }
            };

            if (_xForward.TryGetValue(countryCode, out var ip) && !string.IsNullOrEmpty(ip))
            {
                headers.Add("X-Forwarded-For", ip);
            }

            try
            {
                var channelsRequest = new HttpRequestMessage(HttpMethod.Get, "https://service-channels.clusters.pluto.tv/v2/guide/channels?channelIds=&offset=0&limit=1000&sort=number:asc");
                foreach (var header in headers) channelsRequest.Headers.Add(header.Key, header.Value);
                var channelsResponse = await _httpClient.SendAsync(channelsRequest);
                var channelsJson = await channelsResponse.Content.ReadFromJsonAsync<JsonNode>();
                var channelData = channelsJson?["data"]?.AsArray();

                var categoriesRequest = new HttpRequestMessage(HttpMethod.Get, "https://service-channels.clusters.pluto.tv/v2/guide/categories");
                foreach (var header in headers) categoriesRequest.Headers.Add(header.Key, header.Value);
                var categoriesResponse = await _httpClient.SendAsync(categoriesRequest);
                var categoriesJson = await categoriesResponse.Content.ReadFromJsonAsync<JsonNode>();
                
                var categoryMap = new Dictionary<string, string>();
                if (categoriesJson?["data"] is JsonArray categoriesArray)
                {
                    foreach (var category in categoriesArray)
                    {
                        var catName = category?["name"]?.ToString();
                        var channelIds = category?["channelIDs"]?.AsArray();
                        if (catName != null && channelIds != null)
                        {
                            foreach (var cId in channelIds)
                            {
                                var idString = cId?.ToString();
                                if (idString != null) categoryMap[idString] = catName;
                            }
                        }
                    }
                }

                var stations = new List<Channel>();
                var existingNumbers = new HashSet<int>();

                if (channelData != null)
                {
                    foreach (var elem in channelData)
                    {
                        var id = elem?["id"]?.ToString();
                        var number = elem?["number"]?.GetValue<int>() ?? 0;

                        while (existingNumbers.Contains(number)) number++;
                        existingNumbers.Add(number);

                        var logoUrl = elem?["images"]?.AsArray()
                            .FirstOrDefault(img => img?["type"]?.ToString() == "colorLogoPNG")?["url"]?.ToString();

                        stations.Add(new Channel
                        {
                            Id = id,
                            Name = elem?["name"]?.ToString(),
                            Slug = elem?["slug"]?.ToString(),
                            TmsId = elem?["tmsid"]?.ToString(),
                            Summary = elem?["summary"]?.ToString(),
                            Group = id != null && categoryMap.TryGetValue(id, out var group) ? group : "Uncategorized",
                            CountryCode = countryCode,
                            Number = number,
                            Logo = logoUrl
                        });
                    }
                }

                return stations.OrderBy(c => c.Number).ToList();
            }
            catch (Exception ex)
            {
                App.LogToConsole($"[ERROR] Exception fetching channels for {countryCode}: {ex.Message}");
                return new List<Channel>();
            }
        }

        public async Task<List<Channel>> GetAllChannelsAsync()
        {
            var allChannels = new List<Channel>();
            string[] supportedCountries = { "local", "us_east", "us_west", "ca", "uk", "fr", "de" };

            foreach (var country in supportedCountries)
            {
                var channels = await GetChannelsAsync(country);
                allChannels.AddRange(channels);
            }

            var uniqueChannels = allChannels
                .GroupBy(c => c.Id)
                .Select(g => g.First())
                .ToList();

            var seenNumbers = new HashSet<int>();

            foreach (var channel in uniqueChannels)
            {
                int number = channel.Number;
                
                int offset = channel.CountryCode?.ToLower() switch
                {
                    "ca" => 6000,
                    "uk" => 7000,
                    "fr" => 8000,
                    "de" => 9000,
                    _ => 0
                };

                if (number < offset) number += offset;

                while (seenNumbers.Contains(number)) number++;
                seenNumbers.Add(number);

                channel.Number = number;
            }

            return uniqueChannels.OrderBy(c => c.Number).ToList();
        }
    }

    public class Channel
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Slug { get; set; }
        public string? TmsId { get; set; }
        public string? Summary { get; set; }
        public string? Group { get; set; }
        public string? CountryCode { get; set; }
        public int Number { get; set; }
        public string? Logo { get; set; }
    }
}