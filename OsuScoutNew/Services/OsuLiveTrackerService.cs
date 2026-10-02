using OsuScoutNew.Core;
using System;
using System.IO;
using System.Threading.Tasks;

namespace OsuScoutNew.Services
{
    public class OsuLiveTrackerService : IDisposable
    {
        private IDisposable _watcher;
        private readonly OsuLibraryService _libraryService;

        // Event to tell the UI to refresh the grid
        public event Action MapProcessed;

        public OsuLiveTrackerService(OsuLibraryService libraryService)
        {
            _libraryService = libraryService;
        }

        public void StartTracking(IBeatmapSource source)
        {
            if (source == null || !Directory.Exists(source.Root)) return;

            // Clean up existing watcher if path changes
            StopTracking();

            // The source only reports a map once it is fully written.
            _watcher = source.CreateWatcher(async filePath =>
            {
                await Task.Run(() =>
                {
                    _libraryService.ProcessAndSaveSingleMap(source, filePath);
                });

                // Fire event so UI knows to update
                MapProcessed?.Invoke();
            });
        }

        public void StopTracking()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        public void Dispose()
        {
            StopTracking();
        }
    }
}
