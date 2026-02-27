using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Diagnostics;

namespace PlutoForChannels
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<RegionOption> Regions { get; set; } = new();
        public ObservableCollection<FeedLink> VisibleLinks { get; set; } = new();

        // The system tray icon
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isActualExit = false;

        public MainWindow()
        {
            InitializeComponent();
            InitializeRegions();
            CountryList.ItemsSource = Regions;
            LinksContainer.ItemsSource = VisibleLinks;

            SetupSystemTrayIcon();
        }

        private void SetupSystemTrayIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Icon = System.Drawing.SystemIcons.Information;
            _notifyIcon.Text = "Pluto for Channels .NET";
            _notifyIcon.Visible = true;

            _notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();

            var showMenuItem = new System.Windows.Forms.ToolStripMenuItem("Show Dashboard");
            showMenuItem.Click += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            };

            // --- NEW: Run at Startup Toggle ---
            var runAtStartupMenuItem = new System.Windows.Forms.ToolStripMenuItem("Run at Startup");
            runAtStartupMenuItem.CheckOnClick = true;
            
            // Check the Windows Registry to see if it's already set to run at startup
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key?.GetValue("PlutoForChannels") != null)
                {
                    runAtStartupMenuItem.Checked = true;
                }
            }

            // When the user clicks the toggle, add or remove the Registry Key
            runAtStartupMenuItem.CheckedChanged += (s, e) =>
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (runAtStartupMenuItem.Checked)
                    {
                        // Add to startup using the exact path of where the .exe is currently running
                        key?.SetValue("PlutoForChannels", Environment.ProcessPath!);
                    }
                    else
                    {
                        // Remove from startup
                        key?.DeleteValue("PlutoForChannels", false);
                    }
                }
            };

            // --- NEW: Restart Server ---
            var restartMenuItem = new System.Windows.Forms.ToolStripMenuItem("Restart Server");
            restartMenuItem.Click += (s, e) =>
            {
                _isActualExit = true; 
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                
                // Start a brand new instance of this exact .exe
                Process.Start(Environment.ProcessPath!);
                
                // Immediately shut down this old instance
                System.Windows.Application.Current.Shutdown();
            };

            var quitMenuItem = new System.Windows.Forms.ToolStripMenuItem("Quit Server");
            quitMenuItem.Click += (s, e) =>
            {
                _isActualExit = true; 
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                System.Windows.Application.Current.Shutdown(); 
            };

            // Build the menu
            contextMenu.Items.Add(showMenuItem);
            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            contextMenu.Items.Add(runAtStartupMenuItem);
            contextMenu.Items.Add(restartMenuItem);
            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            contextMenu.Items.Add(quitMenuItem);

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        // Intercept the 'X' button
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isActualExit)
            {
                e.Cancel = true; // Stop the window from closing
                this.Hide();     // Hide it from the taskbar

                // Show a little Windows notification balloon
                _notifyIcon?.ShowBalloonTip(2000, "Pluto for Channels", "Server is still running in the background.", System.Windows.Forms.ToolTipIcon.Info);
            }
            else
            {
                base.OnClosing(e);
            }
        }

        private void InitializeRegions()
        {
            string[] codes = { "all", "local", "us_east", "us_west", "ca", "uk", "fr", "de" };
            var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");

            AppSettings? settings = null;

            if (System.IO.File.Exists(settingsPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(settingsPath);
                    settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                    
                    // Populate UI with saved credentials
                    UsernameBox.Text = settings?.Username ?? "";
                    PasswordBox.Password = settings?.Password ?? "";
                    UsernameBox2.Text = settings?.Username2 ?? "";
                    PasswordBox2.Password = settings?.Password2 ?? "";
                    UsernameBox3.Text = settings?.Username3 ?? "";
                    PasswordBox3.Password = settings?.Password3 ?? "";
                    UsernameBox4.Text = settings?.Username4 ?? "";
                    PasswordBox4.Password = settings?.Password4 ?? "";
                }
                catch { /* Fallback to default if corrupted */ }
            }

            foreach (var code in codes)
            {
                bool isSelected = settings?.SelectedRegions?.Contains(code) ?? (code == "all" || code == "local");
                Regions.Add(new RegionOption { Name = code, IsSelected = isSelected });
            }
        }

        private void SaveSettings()
        {
            try
            {
                var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
                var settings = new AppSettings
                {
                    Username = UsernameBox.Text.Trim(),
                    Password = PasswordBox.Password,
                    Username2 = UsernameBox2.Text.Trim(),
                    Password2 = PasswordBox2.Password,
                    Username3 = UsernameBox3.Text.Trim(),
                    Password3 = PasswordBox3.Password,
                    Username4 = UsernameBox4.Text.Trim(),
                    Password4 = PasswordBox4.Password
                };

                foreach (var r in Regions)
                {
                    if (r.IsSelected) settings.SelectedRegions.Add(r.Name);
                }

                var json = System.Text.Json.JsonSerializer.Serialize(settings);
                System.IO.File.WriteAllText(settingsPath, json);
            }
            catch (Exception ex)
            {
                App.LogToConsole($"[WARNING] Could not save settings.json: {ex.Message}");
            }
        }

        private void SaveLogin_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            App.GlobalPlutoClient?.ClearCache(); // Force the app to get a new session token
            App.LogToConsole("[INFO] Credentials saved. Session cache cleared.");
            System.Windows.MessageBox.Show("Credentials saved successfully!", "Saved", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        public void RefreshLinks(string host)
        {
            Dispatcher.Invoke(() => {
                VisibleLinks.Clear();
                foreach (var region in Regions)
                {
                    if (region.IsSelected)
                    {
                        VisibleLinks.Add(new FeedLink { Title = $"{region.Name.ToUpper()} M3U", Url = $"http://{host}/pluto/{region.Name}/playlist.m3u" });
                        VisibleLinks.Add(new FeedLink { Title = $"{region.Name.ToUpper()} EPG", Url = $"http://{host}/pluto/epg/{region.Name}/epg-{region.Name}.xml" });
                    }
                }
            });
        }

        private void Country_Changed(object sender, RoutedEventArgs e)
        {
            // Ignore events while the window is initially building/loading
            if (!this.IsLoaded) return; 

            SaveSettings();
            App.UpdateActiveRegions();
            
            // Wake up the background service to generate the new XML files instantly
            EpgService.ForceRun();
            App.LogToConsole("[INFO] Regions changed. Forcing immediate EPG generation...");
        }

        private void CopyLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string url)
            {
                bool success = false;

                // Attempt to copy up to 5 times to bypass temporary COM locks
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        System.Windows.Clipboard.SetText(url);
                        success = true;
                        break; // Break out of the loop if successful
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        // Wait 50 milliseconds before trying again
                        System.Threading.Thread.Sleep(50);
                    }
                    catch (Exception ex)
                    {
                        App.LogToConsole($"[ERROR] Clipboard error: {ex.Message}");
                        break;
                    }
                }

                if (success)
                {
                    System.Windows.MessageBox.Show("URL copied!", "Copied", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show("Could not access the clipboard. Another application might be locking it.", "Clipboard Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
        }
    }

    public class RegionOption { public string Name { get; set; } = ""; public bool IsSelected { get; set; } }
    public class FeedLink { public string Title { get; set; } = ""; public string Url { get; set; } = ""; }
	
	public class AppSettings
    {
        public System.Collections.Generic.List<string> SelectedRegions { get; set; } = new();
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string Username2 { get; set; } = "";
        public string Password2 { get; set; } = "";
        public string Username3 { get; set; } = "";
        public string Password3 { get; set; } = "";
        public string Username4 { get; set; } = "";
        public string Password4 { get; set; } = "";
    }
}
