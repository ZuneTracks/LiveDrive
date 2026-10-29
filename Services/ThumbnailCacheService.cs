using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiveDrive.Models;

namespace LiveDrive.Services
{
    public sealed class ThumbnailCacheService
    {
        private readonly PhotoIndexStore _photoIndex;
        private readonly IGraphClient _graph;
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(2);
        private readonly object _queueLock = new object();
        private readonly Dictionary<string, Task> _queuedItems =
            new Dictionary<string, Task>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _requestedLargeThumbnails =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private CancellationTokenSource _cancellation = new CancellationTokenSource();

        public ThumbnailCacheService(PhotoIndexStore photoIndex, IGraphClient graph)
        {
            _photoIndex = photoIndex;
            _graph = graph;
        }

        public async Task CacheAsync(DriveItem item, bool preferLarge)
        {
            var key = item.Id;
            Task queuedTask;
            lock (_queueLock)
            {
                if (!_queuedItems.TryGetValue(key, out queuedTask))
                {
                    _requestedLargeThumbnails[key] = preferLarge;
                    queuedTask = DownloadAsync(item, key);
                    _queuedItems.Add(key, queuedTask);
                }
                else if (preferLarge)
                {
                    _requestedLargeThumbnails[key] = true;
                }
            }

            await queuedTask;
            await _photoIndex.RestoreCachedThumbnailUrisAsync(
                new[] { item }, true, _cancellation.Token);
        }

        private async Task DownloadAsync(DriveItem item, string key)
        {
            try
            {
                var cancellationToken = _cancellation.Token;
                while (true)
                {
                    var preferLarge = GetRequestedLargeThumbnail(key);
                    await _slots.WaitAsync(cancellationToken);
                    try
                    {
                        await _photoIndex.CacheThumbnailAsync(item, _graph, preferLarge, cancellationToken);
                    }
                    finally
                    {
                        _slots.Release();
                    }

                    if (preferLarge || !GetRequestedLargeThumbnail(key))
                    {
                        return;
                    }
                }
            }
            finally
            {
                lock (_queueLock)
                {
                    _queuedItems.Remove(key);
                    _requestedLargeThumbnails.Remove(key);
                }
            }
        }

        private bool GetRequestedLargeThumbnail(string key)
        {
            lock (_queueLock)
            {
                bool preferLarge;
                return _requestedLargeThumbnails.TryGetValue(key, out preferLarge) && preferLarge;
            }
        }

        public void Suspend()
        {
            _cancellation.Cancel();
        }

        public bool IsSuspended => _cancellation.IsCancellationRequested;

        public void Resume()
        {
            if (_cancellation.IsCancellationRequested)
            {
                _cancellation.Dispose();
                _cancellation = new CancellationTokenSource();
            }
        }
    }
}
