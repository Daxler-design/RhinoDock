namespace rhDock.Rhino;

/// <summary>
/// rhDock's single log file, shared by the plug-in, Extensions, the panel page, and agent harnesses. A <c>start</c> line
/// without a later <c>shutdown clean</c> marks an abnormal Rhino exit; the lines before it show what rhDock was doing.
/// </summary>
internal static class HostLog
{
    // The file name predates the shared log; keeping it keeps existing instructions and acceptance notes valid.
    public static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rhdock-agent.log");
    private const int MaxLine = 4000;
    private static readonly object Gate = new();

    // Pasted images and attached PDFs travel as base64 lines of several MB; the log keeps the start of each line.
    public static void Write(string text)
    {
        if (text.Length > MaxLine) text = text[..MaxLine] + $"… [{text.Length} chars]";
        // Logging runs in exception handlers and must never throw.
        try { lock (Gate) File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {text}\n"); }
        catch (Exception) { }
    }

    /// <summary>Records an exception that is about to end Rhino, which otherwise leaves no trace.</summary>
    public static void Fatal(object error) => Write("fatal " + error);
}
