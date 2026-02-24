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
        private static int _currentPort = 7777; // Store port for global access

        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            AppWindow = new MainWindow();
            AppWindow.Show();

            LogToConsole("Initializing ASP.NET Core Web Host...");

            int targetPort = 7777;
            if (int.TryParse(Environment.GetEnvironmentVariable("PLUTO_PORT"), out int envPort))
            {
                targetPort = envPort;
            }

            // Find an open port and store it in the static variable
            _currentPort = GetAvailablePort(targetPort);

            var builder = WebApplication.CreateBuilder(e.Args);

            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.ListenAnyIP(_currentPort);
            });

            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<PlutoClient>(); 
            builder.Services.AddHostedService<EpgService>();

            _host = builder.Build();

            // 1. Dashboard / Index Route
            _host.MapGet("/", (HttpContext context) => 
            {
                var host = context.Request.Host.Value;
                var html = $@"<!DOCTYPE html>
                <html>
                <head><title>Pluto for Channels .NET</title><style>body {{ background-color: #1a1a1a; color: #f5f5f5; font-family: sans-serif; padding: 2rem; }} a {{ color: #3273dc; }}</style></head>
                <body>
                    <h1>Pluto for Channels .NET</h1>
                    <p>API is running.</p>
                </body>
                </html>";
                return Results.Content(html, "text/html");
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

            // 3. Watch Route
            _host.MapGet("/{provider}/{countryCode}/watch/{id}", async (string provider, string countryCode, string id, HttpContext context, PlutoClient plutoClient) =>
            {
                var bootData = await plutoClient.GetBootDataAsync(countryCode);
                if (bootData == null) return Results.StatusCode(500);

                var token = bootData["sessionToken"]?.ToString() ?? "";
                var stitcherParams = bootData["stitcherParams"]?.ToString() ?? "";
                stitcherParams = stitcherParams.TrimStart('?', '&');
                
                var stitcher = "https://cfd-v4-service-channel-stitcher-use1-1.prd.pluto.tv";
                var basePath = $"/stitch/hls/channel/{id}/master.m3u8";

                // Channels known to explicitly require the v2 endpoint
                string[] jwtRequiredList = { "625f054c5dfea70007244612", "625f04253e5f6c000708f3b7", "5421f71da6af422839419cb3" };

                // 1. Use Pluto's provided parameters, or fallback to manual generation if they are missing
                string finalQuery = stitcherParams;
                if (string.IsNullOrEmpty(finalQuery))
                {
                    finalQuery = $"advertisingId=&appName=web&appVersion=unknown&appStoreUrl=&architecture=&buildVersion=&clientTime=0&deviceDNT=0&deviceId={plutoClient.DeviceId}&deviceMake=Chrome&deviceModel=web&deviceType=web&deviceVersion=unknown&includeExtendedEvents=false&sid={Guid.NewGuid()}&userId=&serverSideAds=true";
                }

                // 2. Modern Pluto TV requires the session token appended to the query safely
                if (!finalQuery.Contains("jwt=") && !string.IsNullOrEmpty(token))
                {
                    finalQuery += $"&jwt={token}";
                }

                string videoUrl;

                // 3. Route to the correct endpoint
                if (jwtRequiredList.Contains(id))
                {
                    if (!finalQuery.Contains("masterJWTPassthrough"))
                        finalQuery += "&masterJWTPassthrough=true";

                    if (!finalQuery.Contains("includeExtendedEvents"))
                        finalQuery += "&includeExtendedEvents=true";
                    else
                        finalQuery = finalQuery.Replace("includeExtendedEvents=false", "includeExtendedEvents=true");

                    videoUrl = $"{stitcher}/v2{basePath}?{finalQuery}";
                }
                else
                {
                    videoUrl = $"{stitcher}{basePath}?{finalQuery}";
                }

                LogToConsole($"[WATCH] Stream requested for channel: {id}");
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
                
                // Refresh the new dynamic links
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

        private int GetAvailablePort(int requestedPort)
        {
            try {
                var l = new TcpListener(IPAddress.Any, requestedPort);
                l.Start(); l.Stop(); return requestedPort;
            } catch {
                var l = new TcpListener(IPAddress.Any, 0);
                l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
            }
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
    var host = Dns.GetHostEntry(Dns.GetHostName());
    foreach (var ip in host.AddressList)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return ip.ToString();
        }
    }
    return "127.0.0.1"; // Fallback just in case
}
    }
}
