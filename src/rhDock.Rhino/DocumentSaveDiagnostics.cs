using System.Text.Json;
using Rhino;

namespace rhDock.Rhino;

/// <summary>Observe save and host-operation boundaries without opening the model file or changing save/lock settings.</summary>
internal static class DocumentSaveDiagnostics
{
    internal static void Start()
    {
        RhinoDoc.BeginSaveDocument += OnBeginSave;
        RhinoDoc.EndSaveDocument += OnEndSave;
    }

    internal static void Stop()
    {
        RhinoDoc.BeginSaveDocument -= OnBeginSave;
        RhinoDoc.EndSaveDocument -= OnEndSave;
    }

    private static void OnBeginSave(object? sender, DocumentSaveEventArgs e) => Write("save-begin", e.Document, e.FileName);
    private static void OnEndSave(object? sender, DocumentSaveEventArgs e) => Write("save-end-event", e.Document, e.FileName);

    // Call only on Rhino's UI thread. No File.Exists/GetAttributes on UNC/OneDrive: even a metadata probe can block a save.
    internal static void Write(string phase, RhinoDoc? doc, string? destination = null)
    {
        try
        {
            if (doc is null) return;
            HostLog.Write("doc-save " + JsonSerializer.Serialize(new
            {
                pid = System.Environment.ProcessId,
                phase,
                document = doc.RuntimeSerialNumber,
                path = doc.Path,
                destination,
                readOnly = doc.IsReadOnly,
                modified = doc.Modified,
                fileLocking = global::Rhino.ApplicationSettings.FileSettings.FileLockingEnabled,
                createBackups = global::Rhino.ApplicationSettings.FileSettings.CreateBackupFiles,
                workingDirectory = System.Environment.CurrentDirectory,
                rhinoVersion = RhinoApp.Version.ToString()
            }));
        }
        catch (Exception) { /* Diagnostics must not interrupt a native save or host operation. */ }
    }
}
