using OsuScout;
using OsuScoutNew.Core;
using OsuScoutNew.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Interop;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MahApps.Metro.Controls;

namespace OsuScoutNew
{
    public partial class MainWindow : Window
    {
        private OsuClassifier _classifier;
        private IBeatmapSource _source;

        // Which client's library is shown, and the folders the user picked for each.
        private OsuClient _client;
        private string _songsFolder;
        private string _lazerDataFolder;
        private List<string> _gameProcessNames;
        // Set while OpenLibrary makes the client picker match _client, so that isn't a switch.
        private bool _showingClient;

        private OsuLibraryService _libraryService;
        private OsuLiveTrackerService _liveTrackerService;

        // DataGrid wipes its sort every time ItemsSource is replaced, which UpdateGrid does on every
        // filter change, so the sort lives here and is reapplied after each refresh.
        private List<SortDescription> _sort = new List<SortDescription>();
        private bool _restoringSettings;

        // Every range filter: its slider, value label and clear button, and how a value reads.
        private (RangeSlider Slider, TextBlock Label, Button Reset, Func<double, string> Format, string Unit)[] _ranges;

        // The map list's columns as the XAML defines them, for "Reset columns".
        private Dictionary<DataGridColumn, (DataGridLength Width, Visibility Visibility)> _defaultColumns;
        private ContextMenu _columnMenu;

        // The model that produced each library's stored tags (AppSettings.TaggedWithModel and
        // LazerTaggedWithModel).
        private string _taggedWithModel;
        private string _lazerTaggedWithModel;

        private string TaggedWithModel
        {
            get => _client == OsuClient.Lazer ? _lazerTaggedWithModel : _taggedWithModel;
            set
            {
                if (_client == OsuClient.Lazer) _lazerTaggedWithModel = value;
                else _taggedWithModel = value;
            }
        }

        // Above this are gimmick maps (Aspire and the like) that would otherwise fill the top of a
        // stars-descending list. They only show up when the user searches for one by name.
        private const double GimmickStarThreshold = 15;

        public MainWindow()
        {
            InitializeComponent();
            _ranges = new (RangeSlider, TextBlock, Button, Func<double, string>, string)[]
            {
                (StarSlider, StarValueText, StarResetButton, v => $"{v:0.#}★", ""),
                (BpmSlider, BpmValueText, BpmResetButton, v => $"{v:0}", ""),
                (LengthSlider, LengthValueText, LengthResetButton, v => $"{v:0}", " min"),
                (CsSlider, CsValueText, CsResetButton, v => $"{v:0.0}", ""),
                (ArSlider, ArValueText, ArResetButton, v => $"{v:0.0}", ""),
                (OdSlider, OdValueText, OdResetButton, v => $"{v:0.0}", ""),
                (HpSlider, HpValueText, HpResetButton, v => $"{v:0.0}", ""),
            };
            SetUpColumnMenu();
            var settings = SettingsService.Load();
            _taggedWithModel = settings.TaggedWithModel;
            _lazerTaggedWithModel = settings.LazerTaggedWithModel;
            _songsFolder = settings.SongsFolder;
            _lazerDataFolder = settings.LazerDataFolder;
            _gameProcessNames = settings.GameProcessNames;
            _client = settings.Client ?? PickClientOnFirstRun();

            _classifier = new OsuClassifier();
            _classifier.Initialize();

            _libraryService = new OsuLibraryService(_classifier);
            _liveTrackerService = new OsuLiveTrackerService(_libraryService);
            _liveTrackerService.MapProcessed += () => Dispatcher.Invoke(UpdateGrid);

            this.Loaded += MainWindow_Loaded;

            ReportPreviousScanCrash();
            OpenLibrary();

            TagSearchBox.ItemsSource = _classifier.Config.tags;
            RestoreSettings(settings);
            UpdateFilterLabels();
            UpdateGrid();
        }

        // A scan that took the whole app down leaves a log with no ending (see ScanLog). Say so
        // once, skip the maps it was reading, and point at the log so the crash can be reported.
        private static void ReportPreviousScanCrash()
        {
            var maps = ScanLog.RecoverFromCrash();
            if (maps.Count == 0) return;

            string list = string.Join("\n", maps.Take(12).Select(m => "• " + m));
            if (maps.Count > 12) list += $"\n…and {maps.Count - 12} more";
            var answer = MessageDialog.Show(null,
                "Scoutsu closed unexpectedly the last time it scanned your maps. It was reading these when it stopped:\n\n" +
                list + "\n\n" +
                "They'll be skipped from now on so the scan can finish.\n\n" +
                "If you can, please report this at github.com/FrasierGH/OsuScout/issues and attach the file " +
                "scan-previous.log. Open the folder with that file now?",
                "Scoutsu closed unexpectedly", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{ScanLog.PreviousLogPath}\"");
        }

        // Only asked once: afterwards AppSettings.Client remembers the choice.
        private static OsuClient PickClientOnFirstRun()
        {
            bool stable = OsuLocationService.FindOsuSongsFolder() != null;
            bool lazer = LazerLocationService.FindDataFolder() != null;

            var pick = GameClients.PickOnFirstRun(stable, lazer);
            if (pick != null) return pick.Value;

            var answer = MessageDialog.Show(null,
                "Scoutsu found both osu!stable and osu!lazer on this PC.\n\nShow your osu!lazer library? Choose No for osu!stable.\n\nYou can switch at any time with the Library picker at the top.",
                "Which osu!?", MessageBoxButton.YesNo, MessageBoxImage.Question);
            return answer == MessageBoxResult.Yes ? OsuClient.Lazer : OsuClient.Stable;
        }

        private IBeatmapSource CreateSource(OsuClient client)
        {
            if (client == OsuClient.Lazer)
                return new LazerFilesSource(LazerLocationService.FindDataFolder(_lazerDataFolder));

            // A folder picked with Folder… wins; auto-detection is only the fallback.
            var stable = new StableSongsSource(System.IO.Directory.Exists(_songsFolder)
                ? _songsFolder
                : OsuLocationService.FindOsuSongsFolder());
            _songsFolder = stable.Root;
            return stable;
        }

        // Shows _client's library: its own folder, watcher, database and scan. Each client
        // has its own database file, so switching never mixes the two libraries.
        private void OpenLibrary()
        {
            _liveTrackerService.StopTracking();
            _source = CreateSource(_client);
            _liveTrackerService.StartTracking(_source);

            // Scan on every launch, not just the first: the folder watcher only sees maps added
            // while the app is open. ScanLibraryAsync skips files already in the DB, so this only
            // processes maps that are new since last time.
            // The exception is a library that came from a different folder. Older versions didn't
            // save a folder picked with Folder… (once ⚙ DIR), so auto-detection can land somewhere else, and
            // scanning that would mix two libraries (or report osu! missing on every launch).
            // The library always comes from one folder (changing folder wipes it), so one map is
            // enough to tell.
            string anyMap;
            using (var db = new OsuDbContext(_source.Kind))
            {
                db.EnsureSchema();
                anyMap = db.Beatmaps.Select(b => b.FilePath).FirstOrDefault();
            }
            bool libraryIsFromThisFolder = anyMap == null
                || (_source.Root != null && anyMap.StartsWith(_source.Root, StringComparison.OrdinalIgnoreCase));
            if (libraryIsFromThisFolder) RunBackgroundScan();

            _showingClient = true;
            ClientCombo.SelectedIndex = _client == OsuClient.Lazer ? 1 : 0;
            _showingClient = false;
            FolderButton.ToolTip = _client == OsuClient.Lazer ? "Pick your osu!lazer data folder" : "Pick your osu! Songs folder";
        }

        private void ClientCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // OpenLibrary sets the selection to match _client; only a user's pick switches.
            if (_showingClient || _libraryService == null) return;
            var picked = ClientCombo.SelectedIndex == 1 ? OsuClient.Lazer : OsuClient.Stable;
            if (picked == _client) return;

            _client = picked;
            BeatmapGrid.ItemsSource = null;
            OpenLibrary();
            UpdateGrid();
            SaveSettings();
        }

        private async void RunBackgroundScan()
        {
            ProgressPanel.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = false;
            // Switching mid-scan would leave this scan's progress on the other library's screen.
            ClientCombo.IsEnabled = false;

            var progress = new Progress<int>(percent =>
            {
                ScanProgressBar.Value = percent;
                ScanProgressText.Text = $"Scanning... {percent}%";
            });

            try
            {
                if (!System.IO.Directory.Exists(_source.Root))
                {
                    MessageDialog.Show(this, _client == OsuClient.Lazer
                        ? "Could not find your osu!lazer data folder.\n\nPick it with Folder…: it's the folder holding client.realm and a folder called files."
                        : $"Could not find osu! at {_source.Root}.\n\nIf you installed it somewhere else, pick your Songs folder with Folder….",
                        "Folder not found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // A model update (new app version, new model files) makes every stored
                // tag stale, and the scan below only tags new maps. Re-tag first.
                if (TaggedWithModel != _classifier.ModelId)
                {
                    var retagProgress = new Progress<int>(percent =>
                    {
                        ScanProgressBar.Value = percent;
                        ScanProgressText.Text = $"Updating tags for the new model... {percent}%";
                    });
                    await _libraryService.RetagLibraryAsync(_source, retagProgress);
                    TaggedWithModel = _classifier.ModelId;
                    SaveSettings();
                }

                // Libraries stored before the mapper and CS/AR/OD/HP were recorded: fill those in once.
                var detailsProgress = new Progress<int>(percent =>
                {
                    ScanProgressBar.Value = percent;
                    ScanProgressText.Text = $"Reading mapper and CS/AR/OD/HP... {percent}%";
                });
                await _libraryService.FillMissingDetailsAsync(_source.Kind, detailsProgress);

                await _libraryService.ScanLibraryAsync(_source, progress);
            }
            catch (Exception ex)
            {
                MessageDialog.Show(this, $"Something went wrong while scanning. Press Ctrl+C to copy this report.\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ProgressPanel.Visibility = Visibility.Collapsed;
                PlayButton.IsEnabled = true;
                ClientCombo.IsEnabled = true;
                UpdateGrid();
            }
        }

        // --- UI UTILITY HANDLERS ---

        // Dark title bar, as on every window in the app.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            SystemInteropService.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        }

        private void BeatmapGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectionText.Text = BeatmapGrid.SelectedItem is BeatmapRecord map
                ? $"{map.Artist} - {map.Title} [{map.Version}]"
                : "Select a map, or double-click it, to find it in osu!'s song select.";
            SelectionText.Foreground = (Brush)FindResource(BeatmapGrid.SelectedItem is BeatmapRecord ? "Brush.Text" : "Brush.TextMuted");
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
            if (_ranges == null) return;

            foreach (var range in _ranges)
                ShowRange(range.Slider, range.Label, range.Reset, range.Format, range.Unit);
        }

        // Describes a range the way UpperBound/LowerBound filter it: a handle at the end of the
        // track is "no limit", so both open reads "Any" and one open end reads "Up to x" or "x+".
        private void ShowRange(RangeSlider slider, TextBlock label, Button reset, Func<double, string> format, string unit)
        {
            bool openLow = slider.LowerValue <= slider.Minimum;
            bool openHigh = slider.UpperValue >= slider.Maximum;
            bool active = !(openLow && openHigh);

            if (!active) label.Text = "Any";
            else if (openLow) label.Text = $"Up to {format(slider.UpperValue)}{unit}";
            else if (openHigh) label.Text = $"{format(slider.LowerValue)}+{unit}";
            else label.Text = $"{format(slider.LowerValue)} – {format(slider.UpperValue)}{unit}";

            label.Foreground = (Brush)FindResource(active ? "AccentBrush" : "TextMutedBrush");
            slider.Foreground = (Brush)FindResource(active ? "Brush.AccentStrong" : "Brush.BorderStrong");
            reset.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        }

        private async void UpdateGrid()
        {
            if (_ranges == null || SearchBox == null || TagSearchBox == null || _libraryService == null)
                return;
            // Each restored value fires its own change event; one refresh at the end is enough.
            if (_restoringSettings) return;

            string searchText = SearchBox.Text.ToLower().Trim();
            string tagText = TagSearchBox.Text.ToLower().Trim();
            double maxStars = UpperBound(StarSlider);
            if (double.IsPositiveInfinity(maxStars) && searchText.Length == 0) maxStars = GimmickStarThreshold;

            var tagQueries = tagText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(t => t.Trim())
                                    .ToList();

            var requiredTags = tagQueries.Where(t => !t.StartsWith("-")).ToList();
            var excludedTags = tagQueries.Where(t => t.StartsWith("-") && t.Length > 1)
                                         .Select(t => t.Substring(1).Trim())
                                         .ToList();

            var filter = new MapFilter
            {
                SearchText = searchText,
                RequiredTags = requiredTags,
                ExcludedTags = excludedTags,
                Stars = new Bounds(LowerBound(StarSlider), maxStars),
                Bpm = BoundsOf(BpmSlider),
                LengthMinutes = BoundsOf(LengthSlider),
                CS = BoundsOf(CsSlider),
                AR = BoundsOf(ArSlider),
                OD = BoundsOf(OdSlider),
                HP = BoundsOf(HpSlider),
            };

            var client = _client;
            var results = await _libraryService.SearchBeatmapsAsync(client, filter);
            // The user switched client while this ran: these rows belong to the other library.
            if (client != _client) return;
            BeatmapGrid.ItemsSource = results;
            ApplySort();

            string name = client == OsuClient.Lazer ? "osu!lazer" : "osu!stable";
            string where = System.IO.Directory.Exists(_source?.Root) ? _source.Root : "folder not found";
            LibraryStatusText.Text = $"{name}  ·  {where}  ·  {results.Count:N0} maps shown";
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
            RestorePlacement(settings.Window);
            SearchBox.Text = settings.SearchText ?? "";
            TagSearchBox.Text = settings.TagText ?? "";
            SetRange(StarSlider, settings.MinStars, settings.MaxStars);
            SetRange(BpmSlider, settings.MinBpm, settings.MaxBpm);
            SetRange(LengthSlider, settings.MinLength, settings.MaxLength);
            SetRange(CsSlider, settings.MinCS, settings.MaxCS);
            SetRange(ArSlider, settings.MinAR, settings.MaxAR);
            SetRange(OdSlider, settings.MinOD, settings.MaxOD);
            SetRange(HpSlider, settings.MinHP, settings.MaxHP);
            RestoreColumns(settings.Columns);
            _sort = (settings.Sort ?? new List<SortSetting>())
                .Select(s => new SortDescription(s.Column, s.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending))
                .ToList();
            _restoringSettings = false;
        }

        private void SaveSettings()
        {
            SettingsService.Save(new AppSettings
            {
                Client = _client,
                SongsFolder = _songsFolder,
                LazerDataFolder = _lazerDataFolder,
                GameProcessNames = _gameProcessNames,
                SearchText = SearchBox.Text,
                TagText = TagSearchBox.Text,
                MinStars = Finite(LowerBound(StarSlider)),
                MaxStars = Finite(UpperBound(StarSlider)),
                MinBpm = Finite(LowerBound(BpmSlider)),
                MaxBpm = Finite(UpperBound(BpmSlider)),
                MinLength = Finite(LowerBound(LengthSlider)),
                MaxLength = Finite(UpperBound(LengthSlider)),
                MinCS = Finite(LowerBound(CsSlider)),
                MaxCS = Finite(UpperBound(CsSlider)),
                MinAR = Finite(LowerBound(ArSlider)),
                MaxAR = Finite(UpperBound(ArSlider)),
                MinOD = Finite(LowerBound(OdSlider)),
                MaxOD = Finite(UpperBound(OdSlider)),
                MinHP = Finite(LowerBound(HpSlider)),
                MaxHP = Finite(UpperBound(HpSlider)),
                Columns = CurrentColumns(),
                Sort = _sort.Select(s => new SortSetting { Column = s.PropertyName, Descending = s.Direction == ListSortDirection.Descending }).ToList(),
                TaggedWithModel = _taggedWithModel,
                LazerTaggedWithModel = _lazerTaggedWithModel,
                Window = CurrentPlacement()
            });
        }

        private void RestorePlacement(WindowPlacement placement)
        {
            if (placement == null || placement.Width < MinWidth || placement.Height < MinHeight) return;

            // Skip a position that is no longer on any screen (e.g. a monitor was unplugged).
            var bounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                   SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!desktop.IntersectsWith(bounds)) return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placement.Left;
            Top = placement.Top;
            Width = placement.Width;
            Height = placement.Height;
            if (placement.Maximized) WindowState = WindowState.Maximized;
        }

        // The normal-state bounds, even while maximised or minimised, so un-maximising later
        // returns to the size the user chose.
        private WindowPlacement CurrentPlacement()
        {
            Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.IsEmpty || double.IsNaN(bounds.Width)) return null;
            return new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized
            };
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
        private static Bounds BoundsOf(RangeSlider s) => new Bounds(LowerBound(s), UpperBound(s));

        // --- MAP LIST COLUMNS ---
        // Right-clicking a column header opens a menu to show or hide columns and size them
        // to fit. Columns are identified by the property they show (SortMemberPath).

        private static string ColumnName(DataGridColumn column) =>
            column.Header as string == "★" ? "Stars (★)" : column.Header as string;

        private void SetUpColumnMenu()
        {
            _defaultColumns = BeatmapGrid.Columns.ToDictionary(c => c, c => (c.Width, c.Visibility));

            _columnMenu = new ContextMenu();
            _columnMenu.Opened += (_, _) => BuildColumnMenu();
            BeatmapGrid.ColumnHeaderStyle = new Style(typeof(DataGridColumnHeader), (Style)FindResource(typeof(DataGridColumnHeader)))
            {
                Setters = { new Setter(ContextMenuProperty, _columnMenu) }
            };
        }

        private void BuildColumnMenu()
        {
            _columnMenu.Items.Clear();
            var toggles = new List<MenuItem>();

            // The list always keeps at least one column: the last one shown can't be unticked.
            void UpdateToggles()
            {
                int shown = BeatmapGrid.Columns.Count(c => c.Visibility == Visibility.Visible);
                foreach (var toggle in toggles) toggle.IsEnabled = !(toggle.IsChecked && shown == 1);
            }

            foreach (var column in BeatmapGrid.Columns)
            {
                var toggle = new MenuItem
                {
                    Header = ColumnName(column),
                    IsCheckable = true,
                    IsChecked = column.Visibility == Visibility.Visible,
                    StaysOpenOnClick = true
                };
                toggle.Click += (_, _) =>
                {
                    column.Visibility = toggle.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                    UpdateToggles();
                    SaveSettings();
                };
                toggles.Add(toggle);
                _columnMenu.Items.Add(toggle);
            }
            UpdateToggles();

            _columnMenu.Items.Add(new Separator());
            // The header that was right-clicked; none for the empty area right of the last column.
            if ((_columnMenu.PlacementTarget as DataGridColumnHeader)?.Column is DataGridColumn clicked)
                AddMenuAction($"Size \u201c{ColumnName(clicked)}\u201d to fit", () => SizeColumnsToFit(new[] { clicked }, fillWindow: false));
            AddMenuAction("Size all columns to fit", () => SizeColumnsToFit(BeatmapGrid.Columns.Where(c => c.Visibility == Visibility.Visible), fillWindow: true));
            AddMenuAction("Reset columns", ResetColumns);
        }

        private void AddMenuAction(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            _columnMenu.Items.Add(item);
        }

        // Measures each column against its header and the rows currently on screen (the list
        // only creates the rows you can see), then pins the result so it doesn't keep changing
        // as you scroll. With fillWindow, the text columns (the ones that share the space by
        // default) split the width left over in proportion to how much text they hold, so
        // long names can't push the other columns out of view; otherwise every column gets
        // exactly its content width.
        private void SizeColumnsToFit(IEnumerable<DataGridColumn> columns, bool fillWindow)
        {
            var list = columns.ToList();
            foreach (var column in list) column.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                foreach (var column in list)
                {
                    bool sharesSpace = fillWindow && _defaultColumns[column].Width.IsStar;
                    column.Width = new DataGridLength(column.ActualWidth, sharesSpace ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
                }
                SaveSettings();
            }));
        }

        private void ResetColumns()
        {
            foreach (var (column, (width, visibility)) in _defaultColumns)
            {
                column.Width = width;
                column.Visibility = visibility;
            }
            SaveSettings();
        }

        private void RestoreColumns(List<ColumnSetting> saved)
        {
            if (saved == null) return;
            foreach (var setting in saved)
            {
                var column = BeatmapGrid.Columns.FirstOrDefault(c => c.SortMemberPath == setting.Key);
                if (column == null) continue;
                column.Visibility = setting.Visible ? Visibility.Visible : Visibility.Collapsed;
                if (setting.Width > 0)
                    column.Width = new DataGridLength(setting.Width, setting.Star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
            }
            // A hand-edited settings file could hide everything; an empty list helps nobody.
            if (BeatmapGrid.Columns.All(c => c.Visibility != Visibility.Visible)) ResetColumns();
        }

        private List<ColumnSetting> CurrentColumns() =>
            BeatmapGrid.Columns.Select(c => new ColumnSetting
            {
                Key = c.SortMemberPath,
                Visible = c.Visibility == Visibility.Visible,
                Star = c.Width.IsStar,
                Width = c.Width.IsStar || c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth
            }).ToList();

        private void PlayButton_Click(object sender, RoutedEventArgs e) => LaunchSelectedMap();

        private void BeatmapGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LaunchSelectedMap();

        private void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            dialog.Title = _client == OsuClient.Lazer
                ? "Select your osu!lazer data folder (the one holding client.realm and files)"
                : "Select your new osu! Songs Folder";

            if (dialog.ShowDialog() == true)
            {
                string newPath = dialog.FolderName;
                if (newPath.Equals(_source.Root, StringComparison.OrdinalIgnoreCase)) return;

                if (_client == OsuClient.Lazer)
                {
                    if (!LazerLocationService.IsDataFolder(newPath))
                    {
                        MessageDialog.Show(this, "That isn't an osu!lazer data folder. The right one holds client.realm and a folder called files.",
                            "Not a lazer folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    _lazerDataFolder = newPath;
                    _source = new LazerFilesSource(newPath);
                }
                else
                {
                    _songsFolder = newPath;
                    _source = new StableSongsSource(newPath);
                }
                _liveTrackerService.StartTracking(_source);

                using (var db = new OsuDbContext(_source.Kind))
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
                    var result = MessageDialog.Show(this,
                        $"A new update ({newVersion.TargetFullRelease.Version}) is available!\n\nWould you like to download and restart the app now?\nIf you click No, it will silently download and update automatically after you close the app.",
                        "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);

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

        private void LaunchSelectedMap()
        {
            if (BeatmapGrid.SelectedItem is BeatmapRecord selectedMap)
            {
                try
                {
                    string searchQuery = GameClients.SongSelectSearch(_client, selectedMap.BeatmapID, selectedMap.Artist, selectedMap.Title, selectedMap.Version);
                    Clipboard.SetText(searchQuery);

                    bool focused = SystemInteropService.FocusOsuProcess(_client, _gameProcessNames);

                    if (!focused)
                    {
                        MessageDialog.Show(this, $"osu! isn't running, so Scoutsu couldn't switch to it.\n\nThe search is on your clipboard ({searchQuery}): paste it into song select once osu! is open.",
                            "osu! isn't running", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageDialog.Show(this, $"Couldn't switch to osu!: {ex.Message}", "Couldn't switch to osu!", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            ScanLog.MarkAppClosed();
            SaveSettings();
            _liveTrackerService?.Dispose();
            _classifier?.Dispose();
            base.OnClosed(e);
        }
    }
}