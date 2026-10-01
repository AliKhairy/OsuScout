namespace OsuScoutNew.Core
{
    public static class FileReadiness
    {
        // A watcher reports a file as soon as it is created, usually while the game is
        // still writing it. Opening it exclusively only succeeds once the writer is done.
        public static async Task<bool> WaitUntilReadableAsync(string filePath, int maxRetries = 20, int delayMs = 500)
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
    }
}
