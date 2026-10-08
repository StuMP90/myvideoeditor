using MyVideoEditor.Core.Model;

namespace MyVideoEditor.Core.Editing;

/// <summary>
/// The open project, with undo/redo. Every change goes through <see cref="Apply"/>, which keeps
/// a snapshot of the project from before it. Undo and redo replace <see cref="Project"/> with a
/// fresh object, so callers hold on to clip and track ids, never to model objects.
/// </summary>
public sealed class EditSession
{
    private const int MaxUndo = 200;
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1.5);

    private readonly LinkedList<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();
    private string? _lastMergeKey;
    private DateTime _lastChangeUtc;
    private long _version;
    private long _savedVersion;

    public EditSession(Project project, string? filePath = null)
    {
        Project = project;
        FilePath = filePath;
        Editor = new TimelineEditor(this);
    }

    public Project Project { get; private set; }
    public string? FilePath { get; set; }
    public TimelineEditor Editor { get; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Last?.Value.Description;
    public string? RedoDescription => _redo.TryPeek(out var e) ? e.Description : null;
    public bool IsDirty => _version != _savedVersion;

    /// <summary>Raised after every change, undo and redo.</summary>
    public event Action? Changed;

    /// <summary>
    /// Changes the project. Changes with the same <paramref name="mergeKey"/> in quick
    /// succession (dragging a slider) undo as one step.
    /// </summary>
    public void Apply(string description, Action<Project> change, string? mergeKey = null)
    {
        var now = DateTime.UtcNow;
        var merge = mergeKey != null && mergeKey == _lastMergeKey && now - _lastChangeUtc < MergeWindow && _undo.Count > 0;
        var before = merge ? null : ProjectSerializer.Serialize(Project);
        try
        {
            change(Project);
        }
        catch
        {
            // A failed edit must not leave a half-changed project.
            if (before != null)
                Project = ProjectSerializer.Deserialize(before);
            throw;
        }
        if (before != null)
        {
            _undo.AddLast(new Entry(description, before, _version));
            if (_undo.Count > MaxUndo)
                _undo.RemoveFirst();
        }
        _redo.Clear();
        _lastMergeKey = mergeKey;
        _lastChangeUtc = now;
        _version = NextVersion();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Last is not { } node)
            return;
        _undo.RemoveLast();
        _redo.Push(new Entry(node.Value.Description, ProjectSerializer.Serialize(Project), _version));
        Restore(node.Value);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var entry))
            return;
        _undo.AddLast(new Entry(entry.Description, ProjectSerializer.Serialize(Project), _version));
        Restore(entry);
    }

    public void MarkSaved(string path)
    {
        FilePath = path;
        _savedVersion = _version;
        Changed?.Invoke();
    }

    private void Restore(Entry entry)
    {
        Project = ProjectSerializer.Deserialize(entry.Json);
        _version = entry.Version;
        _lastMergeKey = null;
        Changed?.Invoke();
    }

    private long _counter;
    private long NextVersion() => Interlocked.Increment(ref _counter) + 1_000_000;

    private sealed record Entry(string Description, string Json, long Version);
}
