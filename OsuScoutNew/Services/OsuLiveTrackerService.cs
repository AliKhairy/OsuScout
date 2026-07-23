using System;
using System.IO;
using System.Threading.Tasks;

namespace OsuScoutNew.Services
{
    public class OsuLiveTrackerService : IDisposable
    {
        private FileSystemWatcher _songWatcher;
        private readonly OsuLibraryService _libraryService;

        // Event to tell the UI to refresh the grid
        public event Action MapProcessed;

        public OsuLiveTrackerService(OsuLibraryService libraryService)
        {
            _libraryService = libraryService;
        }

        public void StartTracking(string osuSongsPath)
        {
            if (string.IsNullOrEmpty(osuSongsPath) || !Directory.Exists(osuSongsPath)) return;

            // Clean up existing watcher if path changes
            StopTracking();

            _songWatcher = new FileSystemWatcher(osuSongsPath)
            {
                Filter = "*.osu",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _songWatcher.Created += OnNewMapDownloaded;
        }

        public void StopTracking()
        {
            if (_songWatcher != null)
            {
                _songWatcher.EnableRaisingEvents = false;
                _songWatcher.Created -= OnNewMapDownloaded;
                _songWatcher.Dispose();
                _songWatcher = null;
            }
        }

        private async void OnNewMapDownloaded(object sender, FileSystemEventArgs e)
        {
            bool isReady = await IsFileReadyAsync(e.FullPath);
            if (!isReady) return;

            await Task.Run(() =>
            {
                _libraryService.ProcessAndSaveSingleMap(e.FullPath);
            });

            // Fire event so UI knows to update
            MapProcessed?.Invoke();
        }

        private async Task<bool> IsFileReadyAsync(string filePath, int maxRetries = 20, int delayMs = 500)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                        return true;
                }
                catch (IOException)
                {
                    await Task.Delay(delayMs);
                }
            }
            return false;
        }

        public void Dispose()
        {
            StopTracking();
        }
    }
}