namespace rhDock.Core;

/// <summary>Callbacks run on worker threads; hosts must marshal them before touching UI.</summary>
public sealed class ProjectWatchService : IDisposable
{
    private readonly ScriptCompilerService compiler;
    private readonly Dictionary<string, WatchRegistration> watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private bool disposed;
    public ProjectWatchService(ScriptCompilerService compiler) => this.compiler = compiler;
    public void Register(string projectFolder, IReloadable target)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            projectFolder = Path.GetFullPath(projectFolder);
            if (!watchers.TryGetValue(projectFolder, out var registration))
                watchers[projectFolder] = registration = new(projectFolder, compiler);
            registration.Add(target);
        }
    }
    public void Unregister(string projectFolder)
    {
        lock (gate) if (watchers.Remove(Path.GetFullPath(projectFolder), out var registration)) registration.Dispose();
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var registration in watchers.Values) registration.Dispose();
            watchers.Clear();
        }
    }

    private sealed class WatchRegistration : IDisposable
    {
        private readonly string folder;
        private readonly ScriptCompilerService compiler;
        private readonly FileSystemWatcher watcher;
        private readonly object gate = new();
        private readonly List<WeakReference<IReloadable>> targets = new();
        private readonly List<(string Old, string New)> renames = new();
        private readonly Timer timer;
        private long due;
        private bool disposed;
        public WatchRegistration(string folder, ScriptCompilerService compiler)
        {
            this.folder = folder; this.compiler = compiler;
            timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Renamed;
            watcher.Error += (_, _) => Schedule();
            watcher.EnableRaisingEvents = true;
        }
        public void Add(IReloadable target)
        {
            lock (gate)
            {
                targets.RemoveAll(w => !w.TryGetTarget(out _));
                if (!targets.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, target))) targets.Add(new(target));
            }
        }
        private static bool Relevant(string path) => !ScriptCompilerService.IsBuildOutput(path) && Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csproj" or ".dll";
        private void Changed(object sender, FileSystemEventArgs e) { if (Relevant(e.FullPath)) Schedule(); }
        private void Renamed(object sender, RenamedEventArgs e)
        {
            if (!Relevant(e.FullPath) && !Relevant(e.OldFullPath)) return;
            lock (gate)
            {
                if (disposed) return;
                renames.Add((Path.GetRelativePath(folder, e.OldFullPath), Path.GetRelativePath(folder, e.FullPath)));
                Schedule();
            }
        }
        private void Schedule()
        {
            lock (gate)
            {
                if (disposed) return;
                due = Environment.TickCount64 + 200;
                timer.Change(200, Timeout.Infinite);
            }
        }
        private void Flush()
        {
            IReloadable[] live;
            (string Old, string New)[] changes;
            lock (gate)
            {
                if (disposed) return;
                var remaining = due - Environment.TickCount64;
                if (remaining > 0) { timer.Change((int)remaining, Timeout.Infinite); return; }
                targets.RemoveAll(w => !w.TryGetTarget(out _));
                live = targets.Select(w => w.TryGetTarget(out var t) ? t : null).OfType<IReloadable>().ToArray();
                changes = renames.ToArray(); renames.Clear();
            }
            compiler.Invalidate(folder);
            foreach (var target in live)
            {
                lock (gate) if (disposed) return;
                try
                {
                    foreach (var rename in changes) target.OnEntryRenamed(rename.Old, rename.New);
                    target.OnSourceChanged();
                }
                catch (Exception ex) { System.Diagnostics.Trace.TraceError("rhDock watcher callback: {0}", ex); }
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; timer.Dispose(); watcher.Dispose(); targets.Clear(); renames.Clear();
            }
        }
    }
}
