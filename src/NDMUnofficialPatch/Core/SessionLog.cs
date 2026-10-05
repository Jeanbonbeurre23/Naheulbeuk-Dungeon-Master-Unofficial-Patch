using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace NDMUnofficialPatch.Core
{
    // BepInEx rewrites LogOutput.log at every launch. This listener copies the patch's own messages into a new file per
    // launch, BepInEx\NDMUnofficialPatch-logs\<yyyyMMdd-HHmmss>.log, so that a play session's evidence survives the next
    // launch. Only the newest 30 files are kept.
    internal sealed class SessionLog : ILogListener
    {
        private const int Kept = 30;
        private readonly ManualLogSource _source;
        private readonly StreamWriter _writer;

        private SessionLog(ManualLogSource source, StreamWriter writer)
        {
            _source = source;
            _writer = writer;
        }

        internal static void Start(ManualLogSource source)
        {
            try
            {
                string dir = Path.Combine(Paths.BepInExRootPath, "NDMUnofficialPatch-logs");
                Directory.CreateDirectory(dir);
                var old = new DirectoryInfo(dir).GetFiles("*.log");
                Array.Sort(old, (a, b) => string.CompareOrdinal(b.Name, a.Name));
                for (int i = Kept - 1; i < old.Length; i++) old[i].Delete();

                string path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                Logger.Listeners.Add(new SessionLog(source, writer));
            }
            catch (Exception e)
            {
                source.LogWarning($"Session log not started: {e.Message}");
            }
        }

        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (eventArgs.Source != _source) return;
            try
            {
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss} [{eventArgs.Level}] {eventArgs.Data}");
            }
            catch
            {
                // A logging failure must never reach the game.
            }
        }

        public void Dispose() => _writer.Dispose();
    }
}
