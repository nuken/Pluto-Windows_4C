using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

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
            // We use a default Windows icon here, so we don't need to load external image files
            _notifyIcon.Icon = System.Drawing.SystemIcons.Information;
            _notifyIcon.Text = "Pluto for Channels .NET";
            _notifyIcon.Visible = true;

            // Double-click the tray icon to bring the window back
            _notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            };

            // Right-click context menu
            var contextMenu = new System.Windows.Forms.ContextMenuStrip();

            var showMenuItem = new System.Windows.Forms.ToolStripMenuItem("Show Dashboard");
            showMenuItem.Click += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            };

            var quitMenuItem = new System.Windows.Forms.ToolStripMenuItem("Quit Server");
            quitMenuItem.Click += (s, e) =>
            {
                _isActualExit = true; // Set the flag so we bypass the hide logic
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                System.Windows.Application.Current.Shutdown(); // Actually shut down the app
            };

            contextMenu.Items.Add(showMenuItem);
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
            var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "regions.json");

            System.Collections.Generic.List<string>? savedRegions = null;

            if (System.IO.File.Exists(settingsPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(settingsPath);
                    savedRegions = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(json);
                }
                catch { /* If JSON is corrupted, it will just fall back to defaults */ }
            }

            foreach (var code in codes)
            {
                bool isSelected = false;
                if (savedRegions != null)
                {
                    isSelected = savedRegions.Contains(code);
                }
                else
                {
                    // Default fallback if no settings file exists yet
                    isSelected = (code == "all" || code == "local");
                }

                Regions.Add(new RegionOption { Name = code, IsSelected = isSelected });
            }
        }

		private void SaveSettings()
        {
            try
            {
                var settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "regions.json");
                var selected = new System.Collections.Generic.List<string>();

                foreach (var r in Regions)
                {
                    if (r.IsSelected) selected.Add(r.Name);
                }

                var json = System.Text.Json.JsonSerializer.Serialize(selected);
                System.IO.File.WriteAllText(settingsPath, json);
            }
            catch (Exception ex)
            {
                App.LogToConsole($"[WARNING] Could not save regions.json: {ex.Message}");
            }
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
            SaveSettings();
            App.UpdateActiveRegions();
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
}
