using System.IO;

namespace LiveCaptionsTranslator.utils
{
    // Lightweight diagnostic logger used while tuning the audio/ASR pipeline.
    public static class DiagLog
    {
        private static readonly object _lock = new();
        private static readonly string _path =
            Path.Combine(Directory.GetCurrentDirectory(), "asr_debug.log");

        public static bool Enabled { get; set; } = true;

        public static void Write(string message)
        {
            if (!Enabled)
                return;
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
                }
            }
            catch { }
        }

        public static void Reset()
        {
            try
            {
                lock (_lock)
                {
                    if (File.Exists(_path))
                        File.Delete(_path);
                }
            }
            catch { }
        }
    }
}
