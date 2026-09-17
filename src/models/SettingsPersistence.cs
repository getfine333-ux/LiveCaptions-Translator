namespace LiveCaptionsTranslator.models;

// Only the active object graph can autosave. Deserialized settings and editor drafts
// must never initialize Translator or write its live configuration.
public static class SettingsPersistence
{
    public static readonly object SyncRoot = new();
    public static Setting? Active { get; set; }
    private static int batchDepth;

    public static void SaveIfActive(object sender)
    {
        lock (SyncRoot)
        {
            var active = Active;
            if (active == null || batchDepth != 0) return;
            if (ReferenceEquals(sender, active) || ReferenceEquals(sender, active.MainWindow) ||
                ReferenceEquals(sender, active.OverlayWindow) ||
                active.Configs.Values.Any(list => list.Any(config => ReferenceEquals(config, sender))))
                active.Save();
        }
    }

    public static void Batch(Action apply)
    {
        lock (SyncRoot)
        {
            batchDepth++;
            try { apply(); }
            finally { batchDepth--; }
        }
    }
}
