using OsuMemoryDataProvider;
using OsuMemoryDataProvider.OsuMemoryModels;
using OsuScout;
using OsuScoutNew.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;
using System.ComponentModel;
using System.Windows.Controls;
using MahApps.Metro.Controls;

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

        // DataGrid wipes its sort every time ItemsSource is replaced, which UpdateGrid does on every
        // filter change, so the sort lives here and is reapplied after each refresh.
        private List<SortDescription> _sort = new List<SortDescription>();
        private bool _restoringSettings;

        // The model that produced the library's stored tags (AppSettings.TaggedWithModel).
        private string _taggedWithModel;

        // Above this are gimmick maps (Aspire and the like) that would otherwise fill the top of a
        // stars-descending list. They only show up when the user searches for one by name.
        private const double GimmickStarThreshold = 15;

        public MainWindow()
        {
            InitializeComponent();
            var settings = SettingsService.Load();
            _taggedWithModel = settings.TaggedWithModel;

            // A folder picked with ⚙ DIR wins; auto-detection is only the fallback.
            _osuSongsPath = System.IO.Directory.Exists(settings.SongsFolder)
                ? settings.SongsFolder
                : OsuLocationService.FindOsuSongsFolder();

            _classifier = new OsuClassifier();
            _classifier.Initialize();

            _libraryService = new OsuLibraryService(_classifier);
            _liveTrackerService = new OsuLiveTrackerService(_libraryService);
            _liveTrackerService.MapProcessed += () => Dispatcher.Invoke(UpdateGrid);
            _liveTrackerService.StartTracking(_osuSongsPath);

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;

            // Scan on every launch, not just the first: the folder watcher only sees maps added
            // while the app is open. ScanLibraryAsync skips files already in the DB, so this only
            // processes maps that are new since last time.
            // The exception is a library that came from a different folder. Older versions didn't
            // save a folder picked with ⚙ DIR, so auto-detection can land somewhere else, and
            // scanning that would mix two libraries (or report osu! missing on every launch).
            // The library always comes from one folder (changing folder wipes it), so one map is
            // enough to tell.
            string anyMap;
            using (var db = new OsuDbContext())
            {
                db.Database.EnsureCreated();
                anyMap = db.Beatmaps.Select(b => b.FilePath).FirstOrDefault();
            }
            bool libraryIsFromThisFolder = anyMap == null
                || (_osuSongsPath != null && anyMap.StartsWith(_osuSongsPath, StringComparison.OrdinalIgnoreCase));
            ReportPreviousScanCrash();
            if (libraryIsFromThisFolder) RunBackgroundScan();

            TagSearchBox.ItemsSource = _classifier.Config.tags;
            RestoreSettings(settings);
            UpdateFilterLabels();
            UpdateGrid();

            // --- MEMORY SERVICE WIRING ---
            _memoryService = new OsuMemoryService();
            _memoryService.GameStateChanged += HandleGameStateChange;
            _memoryService.StartPolling();
        }

        // A scan that took the whole app down leaves a log with no ending (see ScanLog). Say so
        // once, skip the maps it was reading, and point at the log so the crash can be reported.
        private static void ReportPreviousScanCrash()
        {
            var maps = ScanLog.RecoverFromCrash();
            if (maps.Count == 0) return;

            string list = string.Join("\n", maps.Take(12).Select(m => "• " + m));
            if (maps.Count > 12) list += $"\n…and {maps.Count - 12} more";
            var answer = MessageBox.Show(
                "Scoutsu closed unexpectedly the last time it scanned your maps. It was reading these when it stopped:\n\n" +
                list + "\n\n" +
                "They'll be skipped from now on so the scan can finish.\n\n" +
                "If you can, please report this at github.com/AliKhairy/OsuScout/issues and attach the file " +
                "scan-previous.log. Open the folder with that file now?",
                "Scoutsu", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{ScanLog.PreviousLogPath}\"");
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

                // A model update (new app version, new model files) makes every stored
                // tag stale, and the scan below only tags new maps. Re-tag first.
                if (_taggedWithModel != _classifier.ModelId)
                {
                    var retagProgress = new Progress<int>(percent =>
                    {
                        ScanProgressBar.Value = percent;
                        ScanProgressText.Text = $"Updating tags for the new model... {percent}%";
                    });
                    await _libraryService.RetagLibraryAsync(_osuSongsPath, retagProgress);
                    _taggedWithModel = _classifier.ModelId;
                    SaveSettings();
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
            UpdateGrid();
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
            if (e.ChangedButton != MouseButton.Left) return;

            // The title bar and the window both route here. Without this, a click on the title
            // bar dragged twice: the first DragMove holds until the button is released, then the
            // click bubbled up and the second one ran with the button already up.
            e.Handled = true;

            // DragMove throws when the button is no longer down (that second call, or a quick
            // click that lands while the app is busy), and unhandled it closed the whole app.
            // Missing one drag is harmless.
            if (Mouse.LeftButton != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { }
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

        private void Slider_ValueChanged(object sender, RoutedEventArgs e)
        {
            UpdateFilterLabels();
            if (SearchBox != null) UpdateGrid();
        }

        private void ResetSlider_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is RangeSlider slider)
            {
                slider.UpperValue = slider.Maximum;
                slider.LowerValue = slider.Minimum;
            }
        }

        private void UpdateFilterLabels()
        {
            // Slider events fire during InitializeComponent, before every control exists yet.
            if (LengthSlider == null) return;

            ShowRange(StarSlider, StarValueText, StarResetButton, v => $"{v:0.#}★", "");
            ShowRange(BpmSlider, BpmValueText, BpmResetButton, v => $"{v:0}", "");
            ShowRange(LengthSlider, LengthValueText, LengthResetButton, v => $"{v:0}", " MIN");
        }

        // Describes a range the way UpperBound/LowerBound filter it: a handle at the end of the
        // track is "no limit", so both open reads "ANY" and one open end reads "UP TO x" or "x+".
        private void ShowRange(RangeSlider slider, TextBlock label, Button reset, Func<double, string> format, string unit)
        {
            bool openLow = slider.LowerValue <= slider.Minimum;
            bool openHigh = slider.UpperValue >= slider.Maximum;
            bool active = !(openLow && openHigh);

            if (!active) label.Text = "ANY";
            else if (openLow) label.Text = $"UP TO {format(slider.UpperValue)}{unit}";
            else if (openHigh) label.Text = $"{format(slider.LowerValue)}+{unit}";
            else label.Text = $"{format(slider.LowerValue)} – {format(slider.UpperValue)}{unit}";

            label.Foreground = (Brush)FindResource(active ? "AccentBrush" : "TextMutedBrush");
            reset.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        }

        private async void UpdateGrid()
        {
            if (SearchBox == null || TagSearchBox == null || StarSlider == null || BpmSlider == null || LengthSlider == null || _libraryService == null)
                return;
            // Each restored value fires its own change event; one refresh at the end is enough.
            if (_restoringSettings) return;

            string searchText = SearchBox.Text.ToLower().Trim();
            string tagText = TagSearchBox.Text.ToLower().Trim();
            double minStars = LowerBound(StarSlider);
            double maxStars = UpperBound(StarSlider);
            if (double.IsPositiveInfinity(maxStars) && searchText.Length == 0) maxStars = GimmickStarThreshold;
            double minBpm = LowerBound(BpmSlider);
            double maxBpm = UpperBound(BpmSlider);
            double minLength = LowerBound(LengthSlider);
            double maxLength = UpperBound(LengthSlider);

            var tagQueries = tagText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(t => t.Trim())
                                    .ToList();

            var requiredTags = tagQueries.Where(t => !t.StartsWith("-")).ToList();
            var excludedTags = tagQueries.Where(t => t.StartsWith("-") && t.Length > 1)
                                         .Select(t => t.Substring(1).Trim())
                                         .ToList();

            // Needs to be updated in OsuLibraryService to accept min and max for all properties
            var results = await _libraryService.SearchBeatmapsAsync(searchText, requiredTags, excludedTags, minStars, maxStars, minBpm, maxBpm, minLength, maxLength);
            BeatmapGrid.ItemsSource = results;
            ApplySort();
        }

        private void ApplySort()
        {
            BeatmapGrid.Items.SortDescriptions.Clear();
            foreach (var column in BeatmapGrid.Columns)
                column.SortDirection = null;

            foreach (var sort in _sort)
            {
                var column = BeatmapGrid.Columns.FirstOrDefault(c => c.SortMemberPath == sort.PropertyName);
                if (column == null) continue;

                column.SortDirection = sort.Direction;
                BeatmapGrid.Items.SortDescriptions.Add(sort);
            }
        }

        private void RestoreSettings(AppSettings settings)
        {
            _restoringSettings = true;
            SearchBox.Text = settings.SearchText ?? "";
            TagSearchBox.Text = settings.TagText ?? "";
            SetRange(StarSlider, settings.MinStars, settings.MaxStars);
            SetRange(BpmSlider, settings.MinBpm, settings.MaxBpm);
            SetRange(LengthSlider, settings.MinLength, settings.MaxLength);
            _sort = (settings.Sort ?? new List<SortSetting>())
                .Select(s => new SortDescription(s.Column, s.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending))
                .ToList();
            _restoringSettings = false;
        }

        private void SaveSettings()
        {
            SettingsService.Save(new AppSettings
            {
                SongsFolder = _osuSongsPath,
                SearchText = SearchBox.Text,
                TagText = TagSearchBox.Text,
                MinStars = Finite(LowerBound(StarSlider)),
                MaxStars = Finite(UpperBound(StarSlider)),
                MinBpm = Finite(LowerBound(BpmSlider)),
                MaxBpm = Finite(UpperBound(BpmSlider)),
                MinLength = Finite(LowerBound(LengthSlider)),
                MaxLength = Finite(UpperBound(LengthSlider)),
                Sort = _sort.Select(s => new SortSetting { Column = s.PropertyName, Descending = s.Direction == ListSortDirection.Descending }).ToList(),
                TaggedWithModel = _taggedWithModel
            });
        }

        // null (open end) puts the handle at the end of the track, i.e. back to "no limit".
        private static void SetRange(RangeSlider slider, double? lower, double? upper)
        {
            slider.UpperValue = upper ?? slider.Maximum;
            slider.LowerValue = lower ?? slider.Minimum;
        }

        private static double? Finite(double bound) => double.IsInfinity(bound) ? null : bound;

        // A handle parked at the end of its track means "no limit". Otherwise anything outside the
        // track's range (under 1 minute, over 300 BPM, over 10 stars) could never be shown at all.
        private static double LowerBound(RangeSlider s) => s.LowerValue <= s.Minimum ? double.NegativeInfinity : s.LowerValue;
        private static double UpperBound(RangeSlider s) => s.UpperValue >= s.Maximum ? double.PositiveInfinity : s.UpperValue;

        private void PlayButton_Click(object sender, RoutedEventArgs e) => LaunchSelectedMap();

        private void BeatmapGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LaunchSelectedMap();

        private void DonateButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://paypal.me/metacis67",
                UseShellExecute = true
            });
        }

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
                var mgr = new UpdateManager(new GithubSource("https://github.com/AliKhairy/OsuScout", null, false));
                
                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion != null)
                {
                    var result = MessageBox.Show(
                        $"A new update ({newVersion.TargetFullRelease.Version}) is available!\n\nWould you like to download and restart the app now?\nIf you click No, it will silently download and update automatically after you close the app.", 
                        "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)
                    {
                        await mgr.DownloadUpdatesAsync(newVersion);
                        SaveSettings(); // the restart exits without closing the window normally
                        mgr.ApplyUpdatesAndRestart(newVersion);
                    }
                    else
                    {
                        await mgr.DownloadUpdatesAsync(newVersion);
                        mgr.WaitExitThenApplyUpdates(newVersion);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update failed: {ex.Message}");
            }
        }

        private void BeatmapGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;

            var column = e.Column;
            ListSortDirection? next;

            // 3-state sort: Ascending -> Descending -> None
            if (column.SortDirection == null)
                next = ListSortDirection.Ascending;
            else if (column.SortDirection == ListSortDirection.Ascending)
                next = ListSortDirection.Descending;
            else
                next = null;

            // Shift for multi-sort, but since they asked to sort multiple columns,
            // if shift is NOT down, we clear the others
            var shiftDown = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            if (!shiftDown)
                _sort.Clear();
            else
                _sort.RemoveAll(sd => sd.PropertyName == column.SortMemberPath);

            if (next != null)
                _sort.Add(new SortDescription(column.SortMemberPath, next.Value));

            ApplySort();
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
            ScanLog.MarkAppClosed();
            SaveSettings();
            _memoryService?.Dispose();
            _liveTrackerService?.Dispose();
            // The classifier is deliberately not disposed: a scan or re-tag may still be running
            // on worker threads, and freeing the ONNX sessions under them crashed the app with an
            // access violation on close. The process is exiting; Windows reclaims the memory.
            base.OnClosed(e);
        }
    }
}