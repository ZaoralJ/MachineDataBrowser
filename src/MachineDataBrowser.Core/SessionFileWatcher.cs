using System.Security.Cryptography;

namespace MachineDataBrowser.Core;

/// <summary>
/// Reports when a session file is changed by someone else: another app window or instance, <c>mdbrowser session</c>,
/// the TUI, the MCP server or an editor. Saves arrive as several file events, so they are reported once, a short
/// while after the last one; content this process already has (its own saves, touches by sync clients) is not reported.
/// </summary>
public sealed class SessionFileWatcher : IDisposable
{
    private readonly string _path;
    private readonly TimeSpan _delay;
    private readonly Lock _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private byte[]? _known;
    private bool _disposed;

    public SessionFileWatcher(string path, TimeSpan? delay = null)
    {
        _path = Path.GetFullPath(path);
        _delay = delay ?? TimeSpan.FromMilliseconds(300);
        _debounce = new Timer(_ => Check());
        _known = Hash(_path);
        try
        {
            // The folder is watched, not just the file, so atomic saves that rename a new file over it are seen too.
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // Some file systems can't be watched: the file still works, it just isn't reloaded by itself.
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>Whether changes are noticed at all on this file system.</summary>
    public bool IsWatching => _watcher is not null;

    /// <summary>Raised on a thread-pool thread, once per new content of the file.</summary>
    public event Action? Changed;

    /// <summary>The file now holds what this process loaded or saved; call after every load and save.</summary>
    public void Accept()
    {
        lock (_gate)
        {
            _known = Hash(_path);
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _debounce.Change(_delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Check()
    {
        lock (_gate)
        {
            // A file that is missing or still being written is checked again on its next event.
            if (_disposed || Hash(_path) is not { } hash || (_known is not null && hash.AsSpan().SequenceEqual(_known)))
            {
                return;
            }

            _known = hash;
        }

        Changed?.Invoke();
    }

    private static byte[]? Hash(string path)
    {
        try
        {
            return SHA256.HashData(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
