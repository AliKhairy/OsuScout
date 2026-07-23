using System;
using System.Threading;
using System.Threading.Tasks;
using OsuMemoryDataProvider;
using OsuMemoryDataProvider.OsuMemoryModels;

namespace OsuScoutNew.Services
{
    public class OsuMemoryService : IDisposable
    {
        private OsuMemoryStatus _lastStatus = OsuMemoryStatus.Unknown;
        private CancellationTokenSource _memoryCts;

        // The event that external classes (like your UI) can subscribe to
        public event Action<OsuMemoryStatus> GameStateChanged;

        public void StartPolling()
        {
            _memoryCts = new CancellationTokenSource();
            var memoryReader = StructuredOsuMemoryReader.Instance;
            var baseAddresses = new OsuBaseAddresses();

            Task.Run(async () =>
            {
                while (!_memoryCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        memoryReader.TryRead(baseAddresses.GeneralData);
                        OsuMemoryStatus currentStatus = baseAddresses.GeneralData.OsuStatus;

                        // Only fire the event if the state actually changes
                        if (currentStatus != _lastStatus && currentStatus != OsuMemoryStatus.Unknown)
                        {
                            _lastStatus = currentStatus;

                            // Broadcast the change to anyone listening
                            GameStateChanged?.Invoke(currentStatus);
                        }
                    }
                    catch
                    {
                        // Silently fail if osu! is closed or memory is locked
                    }

                    // Pass the token to Task.Delay so it can be cleanly interrupted
                    await Task.Delay(500, _memoryCts.Token);
                }
            }, _memoryCts.Token);
        }

        public void Dispose()
        {
            if (_memoryCts != null)
            {
                _memoryCts.Cancel();
                _memoryCts.Dispose();
                _memoryCts = null;
            }
        }
    }
}