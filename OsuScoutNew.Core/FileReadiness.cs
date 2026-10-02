namespace OsuScoutNew.Core
{
    public static class FileReadiness
    {
        // A watcher reports a file as soon as it is created, usually while the game is
        // still writing it. Opening it exclusively (the default) only succeeds once the
        // writer is done. A source whose files are complete when they appear passes a
        // permissive share instead, so the check never gets in the game's way.
        public static async Task<bool> WaitUntilReadableAsync(string filePath, FileShare share = FileShare.None, int maxRetries = 20, int delayMs = 500)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, share))
                        return true;
                }
                catch (IOException)
                {
                    await Task.Delay(delayMs);
                }
            }
            return false;
        }
    }
}
