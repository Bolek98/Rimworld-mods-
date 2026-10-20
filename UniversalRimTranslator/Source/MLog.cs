namespace UniversalRimTranslator
{
    // own log file: Logs/URT.log next to the game's Player.log (the previous run is kept as URT-prev.log).
    // The game's log stays clean; real errors still go to the game's log as well.
    public static class MLog
    {
        private static readonly object gate = new object();
        private static System.IO.StreamWriter w;
        private static bool failed;
        private static readonly System.Collections.Generic.HashSet<int> once = new System.Collections.Generic.HashSet<int>();

        public static string Path
        {
            get
            {
                string dir = System.IO.Path.Combine(Verse.GenFilePaths.SaveDataFolderPath, "Logs");
                if (!System.IO.Directory.Exists(dir))
                    dir = Verse.GenFilePaths.SaveDataFolderPath;
                return System.IO.Path.Combine(dir, "URT.log");
            }
        }

        private static void Write(string level, string text)
        {
            lock (gate)
            {
                try
                {
                    if (w == null && !failed)
                    {
                        string p = Path;
                        string prev = p.Substring(0, p.Length - 4) + "-prev.log";
                        try { if (System.IO.File.Exists(p)) { System.IO.File.Copy(p, prev, true); } } catch { }
                        w = new System.IO.StreamWriter(new System.IO.FileStream(p, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite)) { AutoFlush = true };
                        w.WriteLine("=== Universal RimTranslator log " + System.DateTime.Now.ToString("s") + " ===");
                    }
                    w?.WriteLine(System.DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + text);
                }
                catch
                {
                    failed = true;
                }
            }
            if (failed)
                Verse.Log.Message(text);
        }

        public static void Info(string text) => Write("INFO", text);
        public static void Warn(string text) => Write("WARN", text);
        public static void WarnOnce(string text, int key)
        {
            lock (gate)
                if (!once.Add(key))
                    return;
            Write("WARN", text);
        }
        // shown in the game's log too (version line, real problems)
        public static void Game(string text)
        {
            Write("INFO", text);
            Verse.Log.Message(text);
        }
    }
}
