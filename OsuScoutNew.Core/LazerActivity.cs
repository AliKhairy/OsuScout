using System.Diagnostics;

namespace OsuScoutNew.Core
{
    // Whether you're in a map on osu!lazer, read from its window title. lazer writes the
    // same activity it sends to Discord into the title (osu.Game OsuGame.updateWindowTitle):
    //   menus, song select, results      "osu!"
    //   playing, a replay, testing a map  "osu! - Artist - Title [Difficulty]"
    //   editing                           "osu! - <map file>.osu", or "osu! - new beatmap"
    // stable has no such title, which is why it is read from memory (OsuMemoryService).
    public static class LazerActivity
    {
        private const string Separator = " - ";

        public static bool IsPlaying(string windowTitle)
        {
            if (string.IsNullOrEmpty(windowTitle) || !windowTitle.StartsWith("osu!", StringComparison.Ordinal))
                return false;

            int sep = windowTitle.IndexOf(Separator, StringComparison.Ordinal);
            if (sep < 0) return false;

            string detail = windowTitle[(sep + Separator.Length)..];
            bool editing = detail.EndsWith(".osu", StringComparison.OrdinalIgnoreCase) || detail == "new beatmap";
            return detail.Length > 0 && !editing;
        }
    }

    // Polls lazer's window title and reports when you start and stop playing.
    public sealed class LazerActivityWatcher : IDisposable
    {
        private readonly IEnumerable<string> _processNames;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool? _playing;

        // Raised on a thread-pool thread, only when the state changes.
        public event Action<bool> PlayingChanged;

        public LazerActivityWatcher(IEnumerable<string> processNames)
        {
            _processNames = processNames;
        }

        public void Start(int intervalMs = 500)
        {
            var token = _cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    bool playing = false;
                    try
                    {
                        using Process game = GameClients.FindRunningGame(OsuClient.Lazer, _processNames);
                        playing = game != null && IsLazer(game) && LazerActivity.IsPlaying(game.MainWindowTitle);
                    }
                    catch
                    {
                        // The game closed between finding it and reading its title.
                    }

                    if (playing != _playing)
                    {
                        _playing = playing;
                        PlayingChanged?.Invoke(playing);
                    }

                    try { await Task.Delay(intervalMs, token); }
                    catch (TaskCanceledException) { }
                }
            }, token);
        }

        // FindRunningGame falls back to any "osu!" process when it can't tell which client
        // it is; only a confirmed lazer counts here, so stable's title is never misread.
        private static bool IsLazer(Process game)
        {
            try { return GameClients.IsLazerInstall(Path.GetDirectoryName(game.MainModule?.FileName)); }
            catch { return false; }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
