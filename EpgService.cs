using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;

namespace PlutoForChannels
{
    public class EpgService : BackgroundService
    {
        private readonly PlutoClient _plutoClient;
        private static CancellationTokenSource _delayTokenSource = new CancellationTokenSource();

        public static void ForceRun()
        {
            var cts = _delayTokenSource;
            if (cts != null && !cts.IsCancellationRequested)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* Safely ignore */ }
            }
        }

        public EpgService(PlutoClient plutoClient)
        {
            _plutoClient = plutoClient;
        }

        private List<string> GetActiveRegions()
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(settingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null && settings.SelectedRegions != null)
                    {
                        return settings.SelectedRegions;
                    }
                }
                catch { }
            }
            return new List<string> { "all", "local" };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[INFO] Initializing EPG Scheduler");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var activeRegions = GetActiveRegions();
                    var specificRegions = activeRegions.Where(r => r != "all").ToList();

                    if (activeRegions.Any())
                    {
                        Console.WriteLine($"[INFO] Running EPG Cycle for: {string.Join(", ", activeRegions)}");

                        foreach (var country in specificRegions)
                        {
                            await GenerateXmlFileAsync(country, stoppingToken);
                        }

                        if (activeRegions.Contains("all"))
                        {
                            await GenerateXmlFileAsync("all", stoppingToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] EPG Scheduler: {ex.Message}");
                }

                try
                {
                    using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _delayTokenSource.Token);
                    await Task.Delay(TimeSpan.FromHours(2), linkedToken.Token);
                }
                catch (TaskCanceledException)
                {
                    // The delay was interrupted by ForceRun!
                }

                if (_delayTokenSource.IsCancellationRequested)
                {
                    _delayTokenSource.Dispose();
                    _delayTokenSource = new CancellationTokenSource();
                }
            }
        }

        private async Task GenerateXmlFileAsync(string countryCode, CancellationToken stoppingToken)
        {
            string xmlFilePath = Path.Combine(AppContext.BaseDirectory, $"epg-{countryCode}.xml");
            string gzFilePath = Path.Combine(AppContext.BaseDirectory, $"epg-{countryCode}.xml.gz");

            var channels = await _plutoClient.GetChannelsAsync(countryCode);
            if (channels == null || !channels.Any()) return;

            var tvElement = new XElement("tv",
                new XAttribute("generator-info-name", "PlutoForChannels.NET"),
                new XAttribute("generated-ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
            );

            foreach (var channel in channels)
            {
                var channelElement = new XElement("channel", new XAttribute("id", channel.Id ?? "unknown"));
                channelElement.Add(new XElement("display-name", StripIllegalCharacters(channel.Name ?? "")));
                if (!string.IsNullOrEmpty(channel.Logo))
                {
                    channelElement.Add(new XElement("icon", new XAttribute("src", channel.Logo)));
                }
                tvElement.Add(channelElement);
            }

            var channelIds = channels.Select(c => c.Id).Where(id => id != null).ToList();
            var groupedIds = channelIds.Select((id, index) => new { id, index })
                                       .GroupBy(x => x.index / 100)
                                       .Select(g => string.Join(",", g.Select(x => x.id)));

            DateTime startTime = DateTime.UtcNow;
            string startString = startTime.ToString("yyyy-MM-ddTHH:00:00.000Z");

            for (int i = 0; i < 3; i++)
            {
                foreach (var group in groupedIds)
                {
                    if (stoppingToken.IsCancellationRequested) return;

                    var timelinesResponse = await _plutoClient.GetTimelinesAsync(countryCode == "all" ? "local" : countryCode, group, startString);
                    var dataArray = timelinesResponse?["data"]?.AsArray();

                    if (dataArray != null)
                    {
                        foreach (var entry in dataArray)
                        {
                            var timelines = entry?["timelines"]?.AsArray();
                            if (timelines == null) continue;

                            string chanId = entry?["channelId"]?.ToString() ?? "";

                            foreach (var timeline in timelines)
                            {
                                var episode = timeline?["episode"];
                                var series = episode?["series"];

                                string title = StripIllegalCharacters(timeline?["title"]?.ToString() ?? "");
                                string desc = StripIllegalCharacters(episode?["description"]?.ToString() ?? "");
                                string start = ParsePlutoTime(timeline?["start"]?.ToString());
                                string stop = ParsePlutoTime(timeline?["stop"]?.ToString());
                                string airDateRaw = episode?["clip"]?["originalReleaseDate"]?.ToString() ?? "";

                                var programme = new XElement("programme",
                                    new XAttribute("channel", chanId),
                                    new XAttribute("start", start),
                                    new XAttribute("stop", stop)
                                );

                                programme.Add(new XElement("title", title));

                                if (!string.IsNullOrEmpty(desc))
                                {
                                    programme.Add(new XElement("desc", desc));
                                }

                                string progIcon = series?["tile"]?["path"]?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(progIcon))
                                {
                                    programme.Add(new XElement("icon", new XAttribute("src", progIcon)));
                                }

                                string progType = series?["type"]?.ToString() ?? "";
                                int season = episode?["season"]?.GetValue<int>() ?? 0;
                                int number = episode?["number"]?.GetValue<int>() ?? 0;

                                if (progType == "live")
                                {
                                    programme.Add(new XElement("live"));
                                    programme.Add(new XElement("new"));

                                    if (season > 0 && number > 0 && !(season == 1 && number <= 1))
                                    {
                                        programme.Add(new XElement("episode-num",
                                            new XAttribute("system", "onscreen"), $"S{season:D2}E{number:D2}"));
                                    }
                                }
                                else if (progType != "film" && (season > 0 || number > 0))
                                {
                                    programme.Add(new XElement("episode-num",
                                        new XAttribute("system", "onscreen"), $"S{season:D2}E{number:D2}"));
                                }

                                string episodeId = episode?["_id"]?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(episodeId))
                                {
                                    if (progType == "live" && !string.IsNullOrEmpty(start))
                                    {
                                        string timeStamp = start.Split(' ')[0];
                                        episodeId = $"{episodeId}_{timeStamp}";
                                    }
                                    programme.Add(new XElement("episode-num", new XAttribute("system", "pluto"), episodeId));
                                }

                                if (progType == "live")
                                {
                                    airDateRaw = timeline?["start"]?.ToString() ?? "";
                                }

                                if (!string.IsNullOrEmpty(airDateRaw))
                                {
                                    if (DateTime.TryParse(airDateRaw, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime airDt))
                                    {
                                        if (airDt.Year > 1970)
                                        {
                                            programme.Add(new XElement("episode-num", new XAttribute("system", "original-air-date"), airDt.ToString("yyyy-MM-dd HH:mm:ss")));
                                            programme.Add(new XElement("date", airDt.ToString("yyyyMMdd")));
                                        }
                                    }
                                }

                                string seriesId = series?["_id"]?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(seriesId))
                                {
                                    programme.Add(new XElement("series-id", new XAttribute("system", "pluto"), seriesId));
                                }

                                string epName = episode?["name"]?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(epName) && !epName.Equals(title, StringComparison.OrdinalIgnoreCase))
                                {
                                    programme.Add(new XElement("sub-title", StripIllegalCharacters(epName)));
                                }

                                var categories = GetMappedCategories(episode?["genre"]?.ToString(), episode?["subGenre"]?.ToString(), series?["type"]?.ToString());
                                foreach (var cat in categories)
                                {
                                    programme.Add(new XElement("category", cat));
                                }

                                tvElement.Add(programme);
                            }
                        }
                    }
                }

                startTime = startTime.AddHours(12);
                startString = startTime.ToString("yyyy-MM-ddTHH:00:00.000Z");
            }

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), new XDocumentType("tv", null, "xmltv.dtd", null), tvElement);

            string tempXmlPath = xmlFilePath + ".tmp";
            using (var fileStream = new FileStream(tempXmlPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                doc.Save(fileStream);
            }
            File.Move(tempXmlPath, xmlFilePath, overwrite: true);

            using (var originalFileStream = new FileStream(xmlFilePath, FileMode.Open, FileAccess.Read))
            using (var compressedFileStream = new FileStream(gzFilePath, FileMode.Create, FileAccess.Write))
            using (var compressionStream = new GZipStream(compressedFileStream, CompressionMode.Compress))
            {
                await originalFileStream.CopyToAsync(compressionStream, stoppingToken);
            }
        }

        private string StripIllegalCharacters(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return Regex.Replace(input, @"[\x00-\x08\x0b\x0c\x0e-\x1f]", "");
        }

        private string ParsePlutoTime(string? timeString)
        {
            if (string.IsNullOrEmpty(timeString)) return "";
            if (DateTime.TryParse(timeString, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime dt))
            {
                return dt.ToString("yyyyMMddHHmmss +0000");
            }
            return "";
        }

        private List<string> GetMappedCategories(string? genre, string? subGenre, string? type)
        {
            var categories = new HashSet<string>();

            var genreMap = new Dictionary<string[], string[]>
            {
                { new[] { "Animated" }, new[] { "Family Animation", "Cartoons" } },
                { new[] { "Educational" }, new[] { "Education & Guidance", "Instructional & Educational" } },
                { new[] { "News" }, new[] { "News and Information", "General News", "News + Opinion" } },
                { new[] { "Action" }, new[] { "Action & Adventure", "Martial Arts", "Crime Action", "Action Thrillers" } },
                { new[] { "Reality" }, new[] { "Reality", "Reality Drama", "Courtroom Reality" } },
                { new[] { "Documentary" }, new[] { "Documentaries", "Science and Nature Documentaries", "Crime Documentaries" } },
                { new[] { "Comedy" }, new[] { "Cult Comedies", "Stand-Up", "Family Comedies", "Sketch Comedies" } },
                { new[] { "Drama" }, new[] { "Classic Dramas", "Family Drama", "Romantic Drama", "Crime Drama" } },
                { new[] { "Children" }, new[] { "Kids", "Children & Family", "Cartoons" } }
            };

            void Map(string? input)
            {
                if (string.IsNullOrEmpty(input)) return;
                bool matched = false;
                foreach (var entry in genreMap)
                {
                    if (entry.Value.Contains(input, StringComparer.OrdinalIgnoreCase))
                    {
                        foreach (var cat in entry.Key) categories.Add(cat);
                        matched = true;
                    }
                }
                if (!matched) categories.Add(input);
            }

            Map(genre);
            Map(subGenre);

            if (type == "tv") categories.Add("Series");
            if (type == "film") categories.Add("Movie");

            return categories.ToList();
        }
    }
}
