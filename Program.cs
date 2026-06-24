using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PlutoForChannels
{
    public class AppSettings
    {
        public int Port { get; set; } = 7777;
        public bool StartHidden { get; set; } = true;
        public List<string> SelectedRegions { get; set; } = new();
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string Username2 { get; set; } = "";
        public string Password2 { get; set; } = "";
        public string Username3 { get; set; } = "";
        public string Password3 { get; set; } = "";
        public string Username4 { get; set; } = "";
        public string Password4 { get; set; } = "";
    }

    public class Program
    {
        public static string AppDir => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        public static async Task Main(string[] args)
        {
            int targetPort = GetConfiguredPort();
            int activePort = GetAvailablePort(targetPort);
            string serverIp = GetLocalIPAddress();

            if (activePort != targetPort)
            {
                SaveConfiguredPort(activePort);
            }

            if (args.Contains("--install"))
            {
                InstallSystemdService(serverIp, activePort);
                return;
            }

            var builder = WebApplication.CreateBuilder(args);
            builder.Host.UseSystemd();

            builder.Services.AddMemoryCache();
            builder.Services.AddHttpClient<PlutoClient>();
            builder.Services.AddHostedService<EpgService>();

            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.ListenAnyIP(activePort);
            });

            var app = builder.Build();

            app.MapGet("/", async (HttpContext context) =>
            {
                var assembly = typeof(Program).Assembly;
                var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("index.html"));
                
                if (resourceName != null)
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using StreamReader reader = new StreamReader(stream);
                        string html = await reader.ReadToEndAsync();
                        
                        html = html.Replace(
                            "await fetch('/api/settings', {", 
                            "const response = await fetch('/api/settings', {");
                        
                        html = html.Replace(
                            "body: JSON.stringify(newSettings)\n            });", 
                            "body: JSON.stringify(newSettings)\n            });\n            if (!response.ok) throw new Error('Server returned ' + response.status);");

                        context.Response.ContentType = "text/html";
                        await context.Response.WriteAsync(html);
                        return;
                    }
                }
                await context.Response.WriteAsync("Error: index.html was not embedded in the executable.");
            });

            app.MapGet("/api/settings", () =>
            {
                var settingsPath = Path.Combine(AppDir, "settings.json");
                AppSettings settings = new AppSettings { SelectedRegions = new List<string> { "local" } };
                
                if (File.Exists(settingsPath))
                {
                    try { settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath)) ?? settings; }
                    catch { }
                }
                
                return Results.Json(new { 
                    Settings = settings, 
                    ServerIp = serverIp, 
                    ActivePort = activePort 
                });
            });

            app.MapPost("/api/settings", async (AppSettings newSettings, PlutoClient plutoClient) =>
            {
                var settingsPath = Path.Combine(AppDir, "settings.json");
                try
                {
                    var json = JsonSerializer.Serialize(newSettings, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(settingsPath, json);
                    plutoClient.ClearCache(); 
                    EpgService.ForceRun(); 
                    return Results.Ok(new { success = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to save settings to {settingsPath}: {ex.Message}");
                    return Results.StatusCode(500);
                }
            });

            app.MapGet("/{provider}/{countryCode}/playlist.m3u", async (string provider, string countryCode, HttpContext context, PlutoClient plutoClient) =>
            {
                var channelIdFormat = context.Request.Query["channel_id_format"].ToString().ToLower();
                var stations = await plutoClient.GetChannelsAsync(countryCode);
                if (stations == null || stations.Count == 0) return Results.Text("Error loading channels", statusCode: 500);

                var sb = new StringBuilder();
                sb.AppendLine("#EXTM3U\r\n");

                foreach (var s in stations)
                {
                    var host = context.Request.Host.Value;
                    var url = $"http://{host}/{provider}/{countryCode}/watch/{s.Id}\n";
                    string channelId = channelIdFormat == "id" ? $"{provider}-{s.Id}" : (channelIdFormat == "slug_only" ? $"{s.Slug}" : $"{provider}-{s.Slug}");
                    string desc = string.IsNullOrEmpty(s.Summary) ? "" : new string(s.Summary.Where(c => !char.IsControl(c)).ToArray()).Replace(",", " ");

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

            app.MapGet("/{provider}/{countryCode}/watch/{id}", async (string provider, string countryCode, string id, HttpContext context, PlutoClient plutoClient) =>
            {
                int streamIndex = id.GetHashCode() & int.MaxValue;
                var bootData = await plutoClient.GetBootDataAsync(countryCode, streamIndex, streamIndex);
                if (bootData == null) return Results.StatusCode(500);

                var token = bootData["sessionToken"]?.ToString() ?? "";
                var stitcherParams = bootData["stitcherParams"]?.ToString() ?? "";
                var stitcher = "https://cfd-v4-service-channel-stitcher-use1-1.prd.pluto.tv";
                var basePath = $"/stitch/hls/channel/{id}/master.m3u8";

                var query = System.Web.HttpUtility.ParseQueryString(stitcherParams);
                query["deviceId"] = plutoClient.GetDeviceId(streamIndex);
                query["sid"] = Guid.NewGuid().ToString(); 

                if (!string.IsNullOrEmpty(token)) query["jwt"] = token;
                query["masterJWTPassthrough"] = "true";
                query["includeExtendedEvents"] = "true";

                string videoUrl = $"{stitcher}/v2{basePath}?{query}";
                return Results.Redirect(videoUrl, permanent: false);
            });

            app.MapGet("/{provider}/epg/{countryCode}/{filename}", (string provider, string countryCode, string filename) =>
            {
                var filePath = Path.Combine(AppDir, filename);
                if (!File.Exists(filePath)) return Results.NotFound("EPG file not found.");
                return Results.File(filePath, contentType: filename.EndsWith(".gz") ? "application/gzip" : "application/xml");
            });

            await app.RunAsync();
        }

        private static string GetLocalIPAddress()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up)
                    .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .Where(i => !i.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
                    .Where(i => !i.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase))
                    .Where(i => !i.Name.Contains("docker", StringComparison.OrdinalIgnoreCase))
                    .Where(i => !i.Name.Contains("veth", StringComparison.OrdinalIgnoreCase))
                    .Where(i => !i.Name.Contains("br-", StringComparison.OrdinalIgnoreCase)); 

                foreach (var iface in interfaces)
                {
                    var props = iface.GetIPProperties();
                    var ipv4Addresses = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

                    foreach (var ipInfo in ipv4Addresses)
                    {
                        string ip = ipInfo.Address.ToString();
                        
                        if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || (ip.StartsWith("172.") && IsPrivateClassB(ipInfo.Address.GetAddressBytes())))
                        {
                            return ip;
                        }
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private static bool IsPrivateClassB(byte[] ipBytes)
        {
            return ipBytes[0] == 172 && ipBytes[1] >= 16 && ipBytes[1] <= 31;
        }

        private static int GetAvailablePort(int startingPort)
        {
            var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            var tcpListeners = ipGlobalProperties.GetActiveTcpListeners();

            for (int port = startingPort; port < startingPort + 100; port++)
            {
                if (!tcpListeners.Any(endpoint => endpoint.Port == port))
                {
                    return port;
                }
            }
            return startingPort;
        }

        private static int GetConfiguredPort()
        {
            int targetPort = 7777;
            var settingsPath = Path.Combine(AppDir, "settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                    if (doc.RootElement.TryGetProperty("Port", out var p) && p.TryGetInt32(out int savedPort))
                        targetPort = savedPort;
                }
                catch { }
            }
            return targetPort;
        }

        private static void SaveConfiguredPort(int port)
        {
            var settingsPath = Path.Combine(AppDir, "settings.json");
            AppSettings settings = new AppSettings { Port = port };
            if (File.Exists(settingsPath))
            {
                try { settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath)) ?? settings; }
                catch { }
            }
            settings.Port = port;
            try { File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }

        private static void InstallSystemdService(string serverIp, int activePort)
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? throw new Exception("Could not determine executable path.");
                string workingDir = AppDir;
                string serviceName = "plutoforchannels.service";
                string servicePath = $"/etc/systemd/system/{serviceName}";

                string serviceDefinition = $@"
[Unit]
Description=PlutoForChannels Background Proxy
After=network.target

[Service]
Type=notify
WorkingDirectory={workingDir}
ExecStart={exePath}
SyslogIdentifier=PlutoForChannels
Restart=always
RestartSec=10
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
";
                Console.WriteLine("Creating systemd service file...");
                File.WriteAllText(servicePath, serviceDefinition.Trim());
                Console.WriteLine("Reloading systemd daemon...");
                Process.Start("systemctl", "daemon-reload")?.WaitForExit();
                Console.WriteLine("Enabling and starting service...");
                Process.Start("systemctl", $"enable --now {serviceName}")?.WaitForExit();

                // --- NEW SHORTCUT GENERATION LOGIC ---
                string iconPath = Path.Combine(AppDir, "icon.ico");
                try
                {
                    var assembly = typeof(Program).Assembly;
                    var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("icon.ico"));
                    if (resourceName != null)
                    {
                        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                        if (stream != null)
                        {
                            using FileStream fileStream = new FileStream(iconPath, FileMode.Create, FileAccess.Write);
                            stream.CopyTo(fileStream);
                        }
                    }
                }
                catch { }

                string targetUrl = $"http://{serverIp}:{activePort}";
                string desktopFilePath = Path.Combine(AppDir, "Pluto Dashboard.desktop");
                
                string desktopShortcut = $@"
[Desktop Entry]
Version=1.0
Name=Pluto Dashboard
Comment=Manage PlutoForChannels Proxy
Exec=xdg-open {targetUrl}
Icon={iconPath}
Terminal=false
Type=Application
Categories=Network;
";
                File.WriteAllText(desktopFilePath, desktopShortcut.Trim());

                // Attempt to mark the shortcut as executable
                try 
                { 
                    Process.Start("chmod", $"+x \"{desktopFilePath}\"")?.WaitForExit(); 
                } 
                catch { }
                // -------------------------------------

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n==================================================");
                Console.WriteLine(" INSTALLATION SUCCESSFUL! ");
                Console.WriteLine("==================================================");
                Console.WriteLine($"\nNetwork IP Discovered : {serverIp}");
                Console.WriteLine($"Service Port Assigned : {activePort}");
                Console.WriteLine($"\nA clickable shortcut 'Pluto Dashboard.desktop' has been created in this folder.");
                Console.WriteLine("Double-click it to open the management interface in your browser.");
                Console.WriteLine("\n==================================================\n");
                Console.ResetColor();
            }
            catch (UnauthorizedAccessException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: You must run the install command with sudo: sudo ./PlutoForChannels --install");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Setup encountered an issue: {ex.Message}");
            }
        }
    }
}