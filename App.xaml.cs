using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PlutoForChannels
{
    public partial class App : System.Windows.Application
    {
        private WebApplication? _host;
        public static MainWindow? AppWindow { get; private set; }
        public static PlutoClient? GlobalPlutoClient { get; private set; }
        private static int _currentPort = 7777; 
        public static int StreamCounter = 0;

        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            AppWindow = new MainWindow();
            AppWindow.Show();

            LogToConsole("Initializing ASP.NET Core Web Host...");

            int targetPort = 7777;
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
            AppSettings? appSettings = null;

            // 1. Read Target Port from settings.json
            if (File.Exists(settingsPath))
            {
                try
                {
                    appSettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath));
                    if (appSettings != null && appSettings.Port > 0)
                    {
                        targetPort = appSettings.Port;
                    }
                }
                catch { }
            }

            // 2. Allow Environment Variable to override
            if (int.TryParse(Environment.GetEnvironmentVariable("PLUTO_PORT"), out int envPort))
            {
                targetPort = envPort;
            }

            // 3. Find the best available port
            _currentPort = GetAvailablePort(targetPort);

            // 4. If the port changed from what was saved, update settings.json silently
            if (appSettings == null) appSettings = new AppSettings();
            if (appSettings.Port != _currentPort)
            {
                appSettings.Port = _currentPort;
                try
                {
                    File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(appSettings));
                }
                catch { }
            }

            var builder = WebApplication.CreateBuilder(e.Args);

            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.ListenAnyIP(_currentPort);
            });
			
            builder.Services.AddMemoryCache();
            builder.Services.AddHttpClient<PlutoClient>();
            builder.Services.AddHostedService<EpgService>();

            _host = builder.Build();
            GlobalPlutoClient = _host.Services.GetRequiredService<PlutoClient>();

            // 1. Dashboard / Index Route
            _host.MapGet("/", (HttpContext context) => 
            {
                var host = context.Request.Host.Value;
                var version = "1.1.2"; // Matches your Windows Desktop UI version
                
                var sb = new StringBuilder();
                sb.Append($@"<!DOCTYPE html>
                <html>
                <head>
                    <meta charset=""utf-8"">
                    <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
                    <title>Pluto for Channels .NET</title>
                    <style>
                        body {{ background-color: #1a1a1a; color: #f5f5f5; font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 0; padding: 2rem; }}
                        h1 {{ color: #ffffff; margin-bottom: 5px; }}
                        .tag {{ background-color: #3273dc; color: white; padding: 4px 8px; border-radius: 4px; font-size: 0.45em; vertical-align: middle; margin-left: 10px; }}
                        .container {{ max-width: 900px; margin: 0 auto; background-color: #252525; padding: 30px; border-radius: 8px; box-shadow: 0 4px 6px rgba(0,0,0,0.3); }}
                        .list-item {{ display: flex; align-items: center; justify-content: space-between; padding: 15px; border-bottom: 1px solid #363636; }}
                        .list-item:last-child {{ border-bottom: none; }}
                        a {{ color: #3273dc; text-decoration: none; word-break: break-all; margin-right: 15px; font-family: Consolas, monospace; font-size: 15px; }}
                        a:hover {{ color: #4e8ff1; text-decoration: underline; }}
                        .copy-button {{ background-color: #3273dc; color: white; border: none; padding: 8px 15px; border-radius: 4px; cursor: pointer; transition: background-color 0.2s; font-weight: bold; flex-shrink: 0; }}
                        .copy-button:hover {{ background-color: #4e8ff1; }}
                        .label {{ font-weight: bold; color: #aaaaaa; margin-right: 15px; min-width: 120px; display: inline-block; }}
                    </style>
                    <script>
                        // Uses the legacy execCommand to ensure compatibility when accessing via local network IPs without HTTPS
                        function copyToClipboard(text, btn) {{
                            const textarea = document.createElement('textarea');
                            textarea.value = text;
                            textarea.style.position = 'fixed';
                            textarea.style.left = '-9999px';
                            document.body.appendChild(textarea);
                            textarea.select();
                            try {{
                                document.execCommand('copy');
                                var originalText = btn.innerText;
                                btn.innerText = 'Copied!';
                                btn.style.backgroundColor = '#48c774'; // Green success color
                                setTimeout(function() {{
                                    btn.innerText = originalText;
                                    btn.style.backgroundColor = '#3273dc'; // Back to blue
                                }}, 2000);
                            }} catch (err) {{
                                console.error('Fallback: Oops, unable to copy', err);
                            }}
                            document.body.removeChild(textarea);
                        }}
                    </script>
                </head>
                <body>
                    <div class=""container"">
                        <h1>Pluto for Channels .NET <span class=""tag"">v{version}</span></h1>
                        <p style=""color: #888; margin-bottom: 25px;"">Background proxy server is actively running.</p>
                        <div style=""background-color: #1a1a1a; border-radius: 6px; padding: 5px;"">");

                // Get the regions the user currently has checked in the desktop app
                var activeRegions = AppWindow?.Regions?.Where(r => r.IsSelected).Select(r => r.Name).ToList() ?? new System.Collections.Generic.List<string>();

                if (activeRegions.Count == 0)
                {
                    sb.Append("<p style=\"padding: 15px; color: #ff6b6b;\">No regions selected. Please configure your regions in the desktop dashboard.</p>");
                }
                else
                {
                    // Generate a beautiful copy block for every active region
                    foreach (var region in activeRegions)
                    {
                        string m3uUrl = $"http://{host}/pluto/{region}/playlist.m3u";
                        string epgUrl = $"http://{host}/pluto/epg/{region}/epg-{region}.xml";

                        sb.Append($@"
                            <div class=""list-item"">
                                <div>
                                    <span class=""label"">{region.ToUpper()} M3U:</span>
                                    <a href=""{m3uUrl}"" target=""_blank"">{m3uUrl}</a>
                                </div>
                                <button class=""copy-button"" onclick=""copyToClipboard('{m3uUrl}', this)"">Copy</button>
                            </div>
                            <div class=""list-item"">
                                <div>
                                    <span class=""label"">{region.ToUpper()} EPG:</span>
                                    <a href=""{epgUrl}"" target=""_blank"">{epgUrl}</a>
                                </div>
                                <button class=""copy-button"" onclick=""copyToClipboard('{epgUrl}', this)"">Copy</button>
                            </div>");
                    }
                }

                sb.Append(@"
                        </div>
                    </div>
                </body>
                </html>");

                return Results.Content(sb.ToString(), "text/html");
            });

            // 2. Playlist M3U Route
            _host.MapGet("/{provider}/{countryCode}/playlist.m3u", async (string provider, string countryCode, HttpContext context, PlutoClient plutoClient) =>
            {
                var host = context.Request.Host.Value;
                var channelIdFormat = context.Request.Query["channel_id_format"].ToString().ToLower();
                
                LogToConsole($"[INFO] Playlist requested for {countryCode}");
                var stations = await plutoClient.GetChannelsAsync(countryCode);
                
                if (stations == null || stations.Count == 0) return Results.Text("Error loading channels", statusCode: 500);

                var sb = new StringBuilder();
                sb.AppendLine("#EXTM3U\r\n");

                foreach (var s in stations)
                {
                    var url = $"http://{host}/{provider}/{countryCode}/watch/{s.Id}\n";
                    string channelId = channelIdFormat == "id" ? $"{provider}-{s.Id}" : (channelIdFormat == "slug_only" ? $"{s.Slug}" : $"{provider}-{s.Slug}");
                    string desc = string.IsNullOrEmpty(s.Summary) ? "" : new string(s.Summary.Where(c => !char.IsControl(c)).ToArray());

                    sb.Append($"#EXTINF:-1 channel-id=\"{channelId}\" tvg-id=\"{s.Id}\" tvg-chno=\"{s.Number}\"");
                    if (!string.IsNullOrEmpty(s.Group)) sb.Append($" group-title=\"{s.Group}\"");
                    if (!string.IsNullOrEmpty(s.Logo)) { sb.Append($" tvg-logo=\"{s.Logo}\" tvc-guide-art=\"{s.Logo}\""); }
                    if (!string.IsNullOrEmpty(s.TmsId)) sb.Append($" tvg-name=\"{s.TmsId}\"");
                    if (!string.IsNullOrEmpty(s.Name)) sb.Append($" tvc-guide-title=\"{s.Name}\"");
                    if (!string.IsNullOrEmpty(desc)) sb.Append($" tvc-guide-description=\"{desc}\"");
                    sb.AppendLine($",{s.Name}\n{url}");
                }
                return Results.Text(sb.ToString(), "audio/x-mpegurl");
            });

            // 3. Watch Route (Multi-Stream Unlocked & Deterministically Load Balanced)
            _host.MapGet("/{provider}/{countryCode}/watch/{id}", async (string provider, string countryCode, string id, HttpContext context, PlutoClient plutoClient) =>
            {
                // FIX 1: Lock the index to the Channel ID instead of a rolling counter.
                // This guarantees reconnects during ad breaks stay on the exact same account and device!
                int streamIndex = id.GetHashCode() & int.MaxValue;
                
                var bootData = await plutoClient.GetBootDataAsync(countryCode, streamIndex, streamIndex);
                if (bootData == null) return Results.StatusCode(500);

                var token = bootData["sessionToken"]?.ToString() ?? "";
                var stitcherParams = bootData["stitcherParams"]?.ToString() ?? "";
                
                var stitcher = "https://cfd-v4-service-channel-stitcher-use1-1.prd.pluto.tv";
                var basePath = $"/stitch/hls/channel/{id}/master.m3u8";

                var query = HttpUtility.ParseQueryString(stitcherParams);

                // FIX 2: Ensure the Stitcher deviceId perfectly matches the boot clientID
                query["deviceId"] = plutoClient.GetDeviceId(streamIndex);
                query["sid"] = Guid.NewGuid().ToString(); 

                if (!string.IsNullOrEmpty(token)) query["jwt"] = token;
                query["masterJWTPassthrough"] = "true";
                query["includeExtendedEvents"] = "true";

                string videoUrl = $"{stitcher}/v2{basePath}?{query.ToString()}";

                int activeAccounts = Math.Max(1, plutoClient.GetValidAccounts().Count);
                LogToConsole($"[WATCH] ... account #{streamIndex % activeAccounts + 1} ...");
                return Results.Redirect(videoUrl, permanent: false);
            });

            // 4. EPG File Route
            _host.MapGet("/{provider}/epg/{countryCode}/{filename}", (string provider, string countryCode, string filename) =>
            {
                var filePath = Path.Combine(AppContext.BaseDirectory, filename);
                if (!File.Exists(filePath)) return Results.NotFound("EPG file not found.");
                return Results.File(filePath, contentType: filename.EndsWith(".gz") ? "application/gzip" : "application/xml");
            });

            await _host.StartAsync();
            
            LogToConsole($"⇨ http server started on [::]:{_currentPort}");
            
            if (AppWindow != null)
            {
                AppWindow.StatusText.Text = $"Server: Running on Port {_currentPort}";
                AppWindow.StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 0));
                
                AppWindow.RefreshLinks($"{GetLocalIPAddress()}:{_currentPort}");
            }
        }

        public static void UpdateActiveRegions()
        {
            if (AppWindow != null)
            {
                AppWindow.RefreshLinks($"{GetLocalIPAddress()}:{_currentPort}");
                var selected = AppWindow.Regions.Where(r => r.IsSelected).Select(r => r.Name);
                LogToConsole($"Active regions updated: {string.Join(", ", selected)}");
            }
        }

        private async void Application_Exit(object sender, ExitEventArgs e)
        {
            if (_host != null)
            {
                await _host.StopAsync();
                await _host.DisposeAsync();
            }
        }

        private int GetAvailablePort(int startingPort)
        {
            // Passively ask Windows for a list of all active TCP listeners
            var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var tcpListeners = ipGlobalProperties.GetActiveTcpListeners();

            // Try the starting port (7777). If it's taken, test 7778, 7779, etc.
            for (int port = startingPort; port < startingPort + 100; port++)
            {
                // If no active listener is currently using this port, it is safe to use!
                if (!tcpListeners.Any(endpoint => endpoint.Port == port))
                {
                    return port;
                }
            }
            
            // Extreme fallback: if 100 sequential ports are somehow taken, let the OS pick a random one
            return 0; 
        }

        public static void LogToConsole(string message)
        {
            if (AppWindow != null && AppWindow.LogBox != null)
            {
                AppWindow.Dispatcher.Invoke(() => {
                    AppWindow.LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                    AppWindow.LogBox.ScrollToEnd();
                });
            }
        }

        private static string GetLocalIPAddress()
        {
            try
            {
                using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    var endPoint = socket.LocalEndPoint as System.Net.IPEndPoint;
                    if (endPoint != null) return endPoint.Address.ToString();
                }
            }
            catch { }

            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    string ipStr = ip.ToString();
                    if (ipStr.StartsWith("192.168.") || ipStr.StartsWith("10.") || ipStr.StartsWith("172.")) return ipStr;
                }
            }

            return "127.0.0.1";
        }
    }
}