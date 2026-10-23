using rhDock.Core;

namespace rhDock.Rhino;

internal sealed partial class SingleScriptHost
{
    private bool replayingInput;
    private int inputHistoryRevision = -1;
    private readonly List<InputUndo> inputUndo = [], inputRedo = [];
    private sealed record InputUndo(string Name, SavedScalar Before, SavedScalar After);

    private void ClearInputHistory()
    {
        inputUndo.Clear();
        inputRedo.Clear();
        inputHistoryRevision = Revision;
    }

    private void RecordInputUndo(string name, SavedScalar before, SavedScalar after)
    {
        if (replayingInput || disposed) return;
        if (inputHistoryRevision != Revision) ClearInputHistory();
        if (before.Kind == after.Kind && before.Value == after.Value
            && (before.Items ?? []).SequenceEqual(after.Items ?? [])) return;
        inputRedo.Clear();
        inputUndo.Add(new(name, before, after));
        if (inputUndo.Count > 100) inputUndo.RemoveAt(0);
    }

    // Script values have their own history. Never read, record, or invoke Rhino document Undo here.
    public void UndoInput(bool redo)
    {
        if (disposed || running || activePick is not null) return;
        if (inputHistoryRevision != Revision) { ClearInputHistory(); return; }
        var source = redo ? inputRedo : inputUndo;
        var target = redo ? inputUndo : inputRedo;
        if (source.Count == 0) return;
        var edit = source[^1];
        replayingInput = true;
        try { SetInput(edit.Name, (redo ? edit.After : edit.Before).Restore()); }
        finally { replayingInput = false; }
        source.RemoveAt(source.Count - 1);
        target.Add(edit);
        Refresh();
    }
}
