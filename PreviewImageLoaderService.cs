using System.IO;
using System.Windows.Media.Imaging;

namespace BoramRms.Lite;

/// <summary>Shares image decodes between selection and prefetch without blocking the UI thread.</summary>
public sealed class PreviewImageLoaderService
{
    private readonly record struct FileStamp(long Length, long LastWriteTicks);
    private sealed record CacheEntry(FileStamp Stamp, BitmapSource Source, long Bytes, LinkedListNode<string> Node);
    private sealed record PendingLoad(string Path, FileStamp Stamp, long Generation, long PathVersion,
        TaskCompletionSource<BitmapSource> Completion)
    {
        public int Waiters { get; set; }
        public bool Started { get; set; }
        public bool Abandoned { get; set; }
    }

    private readonly object _gate = new();
    private readonly SemaphoreSlim _decodeSlots;
    private readonly Func<string, int, BitmapSource> _decode;
    private readonly int _decodePixelWidth;
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingLoad> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _pathVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private long _cacheBytes;
    private long _generation;
    internal int PendingLoadCount { get { lock (_gate) return _pending.Count; } }

    public PreviewImageLoaderService(int decodePixelWidth = 3200, int maximumEntries = 8,
        long maximumBytes = 128L * 1024 * 1024, int maximumConcurrentDecodes = 2)
        : this(LoadDetachedPreview, decodePixelWidth, maximumEntries,
            maximumBytes, maximumConcurrentDecodes)
    {
    }

    internal PreviewImageLoaderService(Func<string, int, BitmapSource> decode, int decodePixelWidth = 3200,
        int maximumEntries = 8, long maximumBytes = 128L * 1024 * 1024, int maximumConcurrentDecodes = 2)
    {
        _decode = decode;
        _decodePixelWidth = decodePixelWidth;
        _maximumEntries = Math.Max(1, maximumEntries);
        _maximumBytes = Math.Max(1, maximumBytes);
        _decodeSlots = new SemaphoreSlim(Math.Max(1, maximumConcurrentDecodes));
    }

    public async Task<BitmapSource> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = Path.GetFullPath(path);
        // File metadata can also block on a network/event folder. Keep it off the dispatcher.
        var stamp = await Task.Run(() => ReadStamp(normalizedPath), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Task<BitmapSource> sharedTask;
        PendingLoad sharedLoad;
        lock (_gate)
        {
            if (_cache.TryGetValue(normalizedPath, out var cached))
            {
                if (cached.Stamp == stamp)
                {
                    _lru.Remove(cached.Node);
                    _lru.AddLast(cached.Node);
                    return cached.Source;
                }
                RemoveCached(normalizedPath);
            }

            var version = PathVersion(normalizedPath);
            if (_pending.TryGetValue(normalizedPath, out var existing) &&
                !existing.Abandoned && existing.Stamp == stamp &&
                existing.Generation == _generation && existing.PathVersion == version)
            {
                sharedLoad = existing;
                sharedTask = existing.Completion.Task;
            }
            else
            {
                var pending = new PendingLoad(normalizedPath, stamp, _generation, version,
                    new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously));
                _pending[normalizedPath] = pending;
                sharedLoad = pending;
                sharedTask = pending.Completion.Task;
                _ = Task.Run(() => DecodeAsync(pending));
            }
            sharedLoad.Waiters++;
        }

        // A navigation cancellation stops this waiter, not another caller's shared decode.
        try
        {
            return await sharedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                sharedLoad.Waiters--;
                if (sharedLoad.Waiters == 0 && !sharedLoad.Started)
                {
                    sharedLoad.Abandoned = true;
                }
            }
        }
    }

    public async Task PrefetchAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A missing/moved image or cancelled prefetch must not interrupt selection.
        }
    }

    public void Transfer(string oldPath, string newPath)
    {
        var oldKey = Path.GetFullPath(oldPath);
        var newKey = Path.GetFullPath(newPath);
        if (oldKey.Equals(newKey, StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate)
        {
            _cache.TryGetValue(oldKey, out var cached);
            InvalidateLocked(oldKey);
            InvalidateLocked(newKey);
            if (cached is not null)
            {
                AddCached(newKey, cached.Stamp, cached.Source);
            }
        }
    }

    public void Invalidate(string path)
    {
        lock (_gate) InvalidateLocked(Path.GetFullPath(path));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _cache.Clear();
            _pending.Clear();
            _pathVersions.Clear();
            _lru.Clear();
            _cacheBytes = 0;
        }
    }

    private async Task DecodeAsync(PendingLoad pending)
    {
        try
        {
            await _decodeSlots.WaitAsync().ConfigureAwait(false);
            BitmapSource source;
            FileStamp stamp;
            try
            {
                lock (_gate)
                {
                    // Superseded queued prefetches should not delay the newest selection.
                    if (pending.Abandoned || pending.Generation != _generation ||
                        pending.PathVersion != PathVersion(pending.Path))
                        throw new OperationCanceledException();
                    pending.Started = true;
                }
                source = _decode(pending.Path, _decodePixelWidth);
                source.Freeze();
                stamp = ReadStamp(pending.Path);
            }
            finally
            {
                _decodeSlots.Release();
            }

            lock (_gate)
            {
                // Rotation can preserve timestamps; explicit invalidation also carries a version.
                if (pending.Stamp == stamp && pending.Generation == _generation &&
                    pending.PathVersion == PathVersion(pending.Path))
                {
                    AddCached(pending.Path, stamp, source);
                }
            }
            pending.Completion.TrySetResult(source);
        }
        catch (OperationCanceledException)
        {
            pending.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            pending.Completion.TrySetException(ex);
            // Observe failures even if all original selection waiters were cancelled.
            _ = pending.Completion.Task.Exception;
        }
        finally
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(pending.Path, out var current) && ReferenceEquals(current, pending))
                    _pending.Remove(pending.Path);
            }
        }
    }

    private void AddCached(string path, FileStamp stamp, BitmapSource source)
    {
        RemoveCached(path);
        var bytes = (long)source.PixelWidth * source.PixelHeight * Math.Max(4, (source.Format.BitsPerPixel + 7) / 8);
        if (bytes > _maximumBytes) return;
        while (_cache.Count >= _maximumEntries || _cacheBytes + bytes > _maximumBytes)
        {
            if (_lru.First is not { } oldest) break;
            RemoveCached(oldest.Value);
        }
        var node = _lru.AddLast(path);
        _cache[path] = new CacheEntry(stamp, source, bytes, node);
        _cacheBytes += bytes;
    }

    private void RemoveCached(string path)
    {
        if (!_cache.Remove(path, out var cached)) return;
        _lru.Remove(cached.Node);
        _cacheBytes -= cached.Bytes;
    }

    private void InvalidateLocked(string path)
    {
        RemoveCached(path);
        _pathVersions[path] = PathVersion(path) + 1;
        _pending.Remove(path);
    }

    private long PathVersion(string path) => _pathVersions.GetValueOrDefault(path);

    // TransformedBitmap retains its full-resolution source. Copy only preview pixels so the
    // LRU byte limit represents retained memory rather than merely the displayed dimensions.
    internal static BitmapSource LoadDetachedPreview(string path, int maximumEdge)
    {
        var source = ImageProcessing.Load(path, maximumEdge);
        var stride = checked((source.PixelWidth * source.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        var detached = BitmapSource.Create(source.PixelWidth, source.PixelHeight,
            source.DpiX, source.DpiY, source.Format, source.Palette, pixels, stride);
        detached.Freeze();
        return detached;
    }

    private static FileStamp ReadStamp(string path)
    {
        var file = new FileInfo(path);
        return new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
    }
}
