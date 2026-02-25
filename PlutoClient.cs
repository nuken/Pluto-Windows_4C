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
        private readonly Guid _device;
		public string DeviceId => _device.ToString();
        
        // Thread-safe caching for the session tokens (valid for 4 hours)
        private readonly ConcurrentDictionary<string, JsonNode> _responseList = new();
        private readonly ConcurrentDictionary<string, DateTime> _sessionAt = new();

        // Dictionary to spoof geolocation based on country code
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
            _device = Guid.NewGuid(); // Generates a unique UUID on boot            
            
        }
		public void ClearCache()
        {
            _responseList.Clear();
            _sessionAt.Clear();
        }

        private (string username, string password) GetCredentials()
        {
            var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (System.IO.File.Exists(settingsPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(settingsPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    string u = root.TryGetProperty("Username", out var ue) ? ue.GetString() ?? "" : "";
                    string p = root.TryGetProperty("Password", out var pe) ? pe.GetString() ?? "" : "";
                    return (u, p);
                }
                catch { }
            }
            return ("", "");
        }

        /// <summary>
        /// Fetches the session token required for downloading channels and EPG data.
        /// </summary>
        public async Task<JsonNode?> GetBootDataAsync(string countryCode)
        {
            // 1. Check if we have a valid cached token (under 4 hours old)
            if (_responseList.TryGetValue(countryCode, out var cachedResponse) && 
                _sessionAt.TryGetValue(countryCode, out var sessionTime))
            {
                if ((DateTime.UtcNow - sessionTime).TotalHours < 4)
                {
                    return cachedResponse;
                }
            }

            // 2. Build the query parameters
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

            var (username, password) = GetCredentials();
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                query["username"] = username;
                query["password"] = password;
            }

            var requestUri = $"https://boot.pluto.tv/v4/start?{query}";

            // 3. Construct the HTTP Request with necessary headers
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("authority", "boot.pluto.tv");
            request.Headers.Add("origin", "https://pluto.tv");
            request.Headers.Add("referer", "https://pluto.tv/");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

            // Apply IP Spoofing if the country code exists in our dictionary
            if (_xForward.TryGetValue(countryCode, out var ip) && !string.IsNullOrEmpty(ip))
            {
                request.Headers.Add("X-Forwarded-For", ip);
            }

            try
            {
                // 4. Execute the request
                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    App.LogToConsole($"[ERROR] HTTP failure {response.StatusCode} for {countryCode}");
                    return null;
                }

                // Parse the JSON response
                var jsonResponse = await response.Content.ReadFromJsonAsync<JsonNode>();
                
                if (jsonResponse != null)
                {
                    // Cache the response
                    _responseList[countryCode] = jsonResponse;
                    _sessionAt[countryCode] = DateTime.UtcNow;
                    App.LogToConsole($"New token for {countryCode} generated at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    return jsonResponse;
                }
            }
            catch (Exception ex)
            {
                App.LogToConsole($"[ERROR] Exception fetching boot data: {ex.Message}");
            }

            return null;
        }
		
		/// <summary>
        /// Translates the 'update_epg' method from pluto.py.
        /// Fetches the EPG timelines for a specific set of channels.
        /// </summary>
        public async Task<JsonNode?> GetTimelinesAsync(string countryCode, string channelIds, string startTime)
        {
            var bootData = await GetBootDataAsync(countryCode);
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

        /// <summary>
        /// Fetches channels and categories, then merges them.
        /// </summary>
        public async Task<List<Channel>> GetChannelsAsync(string countryCode)
        {
            if (countryCode.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return await GetAllChannelsAsync();
            }

            var bootData = await GetBootDataAsync(countryCode);
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
                // 1. Fetch Channels
                var channelsRequest = new HttpRequestMessage(HttpMethod.Get, "https://service-channels.clusters.pluto.tv/v2/guide/channels?channelIds=&offset=0&limit=1000&sort=number:asc");
                foreach (var header in headers) channelsRequest.Headers.Add(header.Key, header.Value);
                var channelsResponse = await _httpClient.SendAsync(channelsRequest);
                var channelsJson = await channelsResponse.Content.ReadFromJsonAsync<JsonNode>();
                var channelData = channelsJson?["data"]?.AsArray();

                // 2. Fetch Categories (for the Group tags)
                var categoriesRequest = new HttpRequestMessage(HttpMethod.Get, "https://service-channels.clusters.pluto.tv/v2/guide/categories");
                foreach (var header in headers) categoriesRequest.Headers.Add(header.Key, header.Value);
                var categoriesResponse = await _httpClient.SendAsync(categoriesRequest);
                var categoriesJson = await categoriesResponse.Content.ReadFromJsonAsync<JsonNode>();
                
                // Map Category Names to Channel IDs
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

                // 3. Build the Channel List
                var stations = new List<Channel>();
                var existingNumbers = new HashSet<int>();

                if (channelData != null)
                {
                    foreach (var elem in channelData)
                    {
                        var id = elem?["id"]?.ToString();
                        var number = elem?["number"]?.GetValue<int>() ?? 0;

                        // Ensure unique channel numbers
                        while (existingNumbers.Contains(number)) number++;
                        existingNumbers.Add(number);

                        // Find the colorLogoPNG
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

        /// <summary>
        /// Aggregates all supported countries, offsets numbers, and removes duplicates.
        /// </summary>
        public async Task<List<Channel>> GetAllChannelsAsync()
        {
            var allChannels = new List<Channel>();
            string[] supportedCountries = { "local", "us_east", "us_west", "ca", "uk", "fr", "de" };

            // Fetch all countries
            foreach (var country in supportedCountries)
            {
                var channels = await GetChannelsAsync(country);
                allChannels.AddRange(channels);
            }

            // Deduplicate by Channel ID
            var uniqueChannels = allChannels
                .GroupBy(c => c.Id)
                .Select(g => g.First())
                .ToList();

            var seenNumbers = new HashSet<int>();

            foreach (var channel in uniqueChannels)
            {
                int number = channel.Number;
                
                // Apply offsets based on country code
                int offset = channel.CountryCode?.ToLower() switch
                {
                    "ca" => 6000,
                    "uk" => 7000,
                    "fr" => 8000,
                    "de" => 9000,
                    _ => 0
                };

                if (number < offset) number += offset;

                // Ensure no collisions across regions
                while (seenNumbers.Contains(number)) number++;
                seenNumbers.Add(number);

                channel.Number = number;
            }

            return uniqueChannels.OrderBy(c => c.Number).ToList();
        }
    }

    /// <summary>
    /// Represents a Pluto TV Channel mapped for our application.
    /// </summary>
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