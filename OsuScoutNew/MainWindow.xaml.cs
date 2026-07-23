using OsuMemoryDataProvider;
using OsuMemoryDataProvider.OsuMemoryModels;
using OsuScout;
using OsuScoutNew.Services;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Threading.Tasks;
using Velopack;

namespace OsuScoutNew
{
    public partial class MainWindow : Window
    {
        private bool _userManuallyHidden = false;
        private HwndSource _hwndSource;
        private OsuClassifier _classifier;
        private string _osuSongsPath;

        private OsuMemoryService _memoryService;
        private OsuLibraryService _libraryService;
        private OsuLiveTrackerService _liveTrackerService;

        public MainWindow()
        {
            InitializeComponent();
            _osuSongsPath = OsuLocationService.FindOsuSongsFolder();

            _classifier = new OsuClassifier();
            _classifier.Initialize();

            _libraryService = new OsuLibraryService(_classifier);
            _liveTrackerService = new OsuLiveTrackerService(_libraryService);
            _liveTrackerService.MapProcessed += () => Dispatcher.Invoke(UpdateGrid);
            _liveTrackerService.StartTracking(_osuSongsPath);

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;

            using (var db = new OsuDbContext())
            {
                db.Database.EnsureCreated();
                if (!db.Beatmaps.Any())
                {
                    RunBackgroundScan();
                }
            }

            TagSearchBox.ItemsSource = _classifier.Config.tags;
            UpdateGrid();

            // --- MEMORY SERVICE WIRING ---
            _memoryService = new OsuMemoryService();
            _memoryService.GameStateChanged += HandleGameStateChange;
            _memoryService.StartPolling();
        }

        private async void RunBackgroundScan()
        {
            HotkeyPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = false;

            var progress = new Progress<int>(percent =>
            {
                ScanProgressBar.Value = percent;
                ScanProgressText.Text = $"Scanning... {percent}%";
            });

            try
            {
                if (!System.IO.Directory.Exists(_osuSongsPath))
                {
                    MessageBox.Show($"FATAL: Could not find osu! at {_osuSongsPath}. Did you install it somewhere else?");
                    return;
                }

                await _libraryService.ScanLibraryAsync(_osuSongsPath, progress);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"CRASH LOG:\n\n{ex.Message}\n\n{ex.StackTrace}");
            }

            ProgressPanel.Visibility = Visibility.Collapsed;
            HotkeyPanel.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = true;
        }

        private void HandleGameStateChange(OsuMemoryStatus status)
        {
            Dispatcher.Invoke(() =>
            {
                if (status == OsuMemoryStatus.Playing)
                {
                    this.Visibility = Visibility.Collapsed;
                }
                else if (status == OsuMemoryStatus.SongSelect || status == OsuMemoryStatus.MainMenu)
                {
                    if (!_userManuallyHidden)
                    {
                        this.Visibility = Visibility.Visible;
                    }
                }
            });
        }

        // --- UI UTILITY HANDLERS ---
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            _userManuallyHidden = true;
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void SearchInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateGrid();

        private void TagSearchBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateGrid();

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SearchBox != null) UpdateGrid();
        }

        private async void UpdateGrid()
        {
            string searchText = SearchBox.Text.ToLower().Trim();
            string tagText = TagSearchBox.Text.ToLower().Trim();
            double minStars = StarSlider.Value;
            double minBpm = BpmSlider.Value;
            double maxLength = LengthSlider.Value;

            var tagQueries = tagText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(t => t.Trim())
                                    .ToList();

            var requiredTags = tagQueries.Where(t => !t.StartsWith("-")).ToList();
            var excludedTags = tagQueries.Where(t => t.StartsWith("-") && t.Length > 1)
                                         .Select(t => t.Substring(1).Trim())
                                         .ToList();

            var results = await _libraryService.SearchBeatmapsAsync(searchText, requiredTags, excludedTags, minStars, minBpm, maxLength);
            BeatmapGrid.ItemsSource = results;
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e) => LaunchSelectedMap();

        private void BeatmapGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LaunchSelectedMap();

        private void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            dialog.Title = "Select your new osu! Songs Folder";

            if (dialog.ShowDialog() == true)
            {
                string newPath = dialog.FolderName;
                if (newPath.Equals(_osuSongsPath, StringComparison.OrdinalIgnoreCase)) return;

                _osuSongsPath = newPath;
                _liveTrackerService.StartTracking(_osuSongsPath);

                using (var db = new OsuDbContext())
                {
                    db.Beatmaps.RemoveRange(db.Beatmaps);
                    db.SaveChanges();
                }

                BeatmapGrid.ItemsSource = null;
                RunBackgroundScan();
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            _hwndSource = HwndSource.FromHwnd(helper.Handle);

            if (_hwndSource != null)
            {
                _hwndSource.AddHook(HwndHook);
                SystemInteropService.RegisterHotKey(helper.Handle, SystemInteropService.HOTKEY_ID, SystemInteropService.MOD_ALT, SystemInteropService.VK_S);
            }

            _ = UpdateAppAsync();
        }

        private async Task UpdateAppAsync()
        {
            try
            {
                // This URL should point to your new GitHub repository once you create it
                var mgr = new UpdateManager("https://github.com/AliKhairy/OsuScout");
                
                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion != null)
                {
                    await mgr.DownloadUpdatesAsync(newVersion);
                    mgr.ApplyUpdatesAndRestart(newVersion);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update failed: {ex.Message}");
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            SystemInteropService.UnregisterHotKey(helper.Handle, SystemInteropService.HOTKEY_ID);

            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(HwndHook);
                _hwndSource.Dispose();
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (msg == SystemInteropService.WM_HOTKEY && wp.ToInt32() == SystemInteropService.HOTKEY_ID)
            {
                ToggleOverlayVisibility();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void LaunchSelectedMap()
        {
            if (BeatmapGrid.SelectedItem is BeatmapRecord selectedMap)
            {
                try
                {
                    string searchQuery = $"{selectedMap.Artist} {selectedMap.Title} {selectedMap.Version}";
                    Clipboard.SetText(searchQuery);

                    bool focused = SystemInteropService.FocusOsuProcess();

                    if (!focused)
                    {
                        MessageBox.Show("osu! is not currently running. The search query has been copied to your clipboard.");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not execute focus hook: {ex.Message}");
                }
            }
        }

        private void ToggleOverlayVisibility()
        {
            if (this.Visibility == Visibility.Visible)
            {
                _userManuallyHidden = true;
                this.Visibility = Visibility.Collapsed;
            }
            else
            {
                _userManuallyHidden = false;
                this.Visibility = Visibility.Visible;
                this.WindowState = WindowState.Normal;
                this.Activate();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _memoryService?.Dispose();
            _liveTrackerService?.Dispose();
            _classifier?.Dispose();
            base.OnClosed(e);
        }
    }
}