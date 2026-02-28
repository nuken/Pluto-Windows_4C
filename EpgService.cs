using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;

namespace PlutoForChannels
{
    public class EpgService : BackgroundService
    {
        private readonly PlutoClient _plutoClient;
        private readonly string[] _supportedCountries = { "local", "us_east", "us_west", "ca", "uk", "fr", "de" };

        private static CancellationTokenSource _delayTokenSource = new CancellationTokenSource();

        public static void ForceRun()
        {
            if (!_delayTokenSource.IsCancellationRequested)
            {
                _delayTokenSource.Cancel();
            }
        }
		
		public EpgService(PlutoClient plutoClient)
        {
            _plutoClient = plutoClient;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            App.LogToConsole("[INFO] Initializing EPG Scheduler");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Check which regions the user has actually selected in the UI
                    var activeRegions = App.AppWindow?.Regions
                        .Where(r => r.IsSelected && r.Name != "all")
                        .Select(r => r.Name)
                        .ToList() ?? new List<string>();

                    if (activeRegions.Any())
                    {
                        App.LogToConsole($"[INFO] Running EPG Cycle for: {string.Join(", ", activeRegions)}");
                        
                        foreach (var country in activeRegions)
                        {
                            await GenerateXmlFileAsync(country, stoppingToken);
                        }

                        // Always generate 'all' if the user wants it
                        if (App.AppWindow?.Regions.Any(r => r.Name == "all" && r.IsSelected) == true)
                        {
                            await GenerateXmlFileAsync("all", stoppingToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.LogToConsole($"[ERROR] EPG Scheduler: {ex.Message}");
                }

                // Wait for 2 hours OR until ForceRun() is called
                try
                {
                    using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _delayTokenSource.Token);
                    await Task.Delay(TimeSpan.FromHours(2), linkedToken.Token);
                }
                catch (TaskCanceledException)
                {
                    // The delay was interrupted by ForceRun!
                }
                
                // Reset the wake-up token for the next nap
                if (_delayTokenSource.IsCancellationRequested)
                {
                    _delayTokenSource.Dispose();
                    _delayTokenSource = new CancellationTokenSource();
                }
            }
        }

        private async Task GenerateXmlFileAsync(string countryCode, CancellationToken stoppingToken)
        {
            // FIX: Explicitly combine the file path with the application's base directory
            string xmlFilePath = Path.Combine(AppContext.BaseDirectory, $"epg-{countryCode}.xml");
            string gzFilePath = Path.Combine(AppContext.BaseDirectory, $"epg-{countryCode}.xml.gz");

            var channels = await _plutoClient.GetChannelsAsync(countryCode);
            if (channels == null || !channels.Any()) return;

            var tvElement = new XElement("tv",
                new XAttribute("generator-info-name", "PlutoForChannels.NET"),
                new XAttribute("generated-ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
            );

            // 1. Add Channel Definitions
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

            // 2. Fetch Timelines in batches of 100 channels
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

        // 1. Add Description and Program Icon
        if (!string.IsNullOrEmpty(desc)) 
        {
            programme.Add(new XElement("desc", desc));
        }
        
        string progIcon = series?["tile"]?["path"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(progIcon))
        {
            programme.Add(new XElement("icon", new XAttribute("src", progIcon)));
        }

        // 2. Handle Live Tags and Episode Numbers (onscreen, pluto)
        string progType = series?["type"]?.ToString() ?? "";
        int season = episode?["season"]?.GetValue<int>() ?? 0;
        int number = episode?["number"]?.GetValue<int>() ?? 0;

        if (progType == "live")
        {
            // Trust Pluto's "live" flag regardless of the release date
            programme.Add(new XElement("live"));
            
            // Suppress meaningless "Season 1, Episode 0" for live news broadcasts
            if (season > 0 && number > 0)
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
            programme.Add(new XElement("episode-num", new XAttribute("system", "pluto"), episodeId));
        }

        // 3. Date and Original Air Date parsing
        if (!string.IsNullOrEmpty(airDateRaw))
        {
            if (DateTime.TryParse(airDateRaw, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime airDt))
            {
                // Suppress BOTH tags if it evaluates to the 1969/1970 Unix Epoch placeholder
                if (airDt.Year > 1970)
                {
                    programme.Add(new XElement("episode-num", new XAttribute("system", "original-air-date"), airDateRaw));
                    programme.Add(new XElement("date", airDt.ToString("yyyyMMdd")));
                }
            }
        }

        // 4. Series ID
        string seriesId = series?["_id"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(seriesId))
        {
            programme.Add(new XElement("series-id", new XAttribute("system", "pluto"), seriesId));
        }

        // 5. Sub-Title (if different from title)
        string epName = episode?["name"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(epName) && !epName.Equals(title, StringComparison.OrdinalIgnoreCase))
        {
            programme.Add(new XElement("sub-title", StripIllegalCharacters(epName)));
        }

        // 6. Category/Genre Mapping
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

            using (var fileStream = new FileStream(xmlFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                doc.Save(fileStream);
            }

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
            if (DateTime.TryParse(timeString, out DateTime dt))
            {
                return dt.ToUniversalTime().ToString("yyyyMMddHHmmss +0000");
            }
            return "";
        }
		private List<string> GetMappedCategories(string? genre, string? subGenre, string? type)
{
    var categories = new HashSet<string>();
    
    // Mapping dictionary based on pluto.py
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