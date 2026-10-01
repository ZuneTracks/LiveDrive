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
        private CancellationTokenSource _cancellation = new CancellationTokenSource();

        public ThumbnailCacheService(PhotoIndexStore photoIndex, IGraphClient graph)
        {
            _photoIndex = photoIndex;
            _graph = graph;
        }

        public async Task CacheAsync(DriveItem item, bool preferLarge)
        {
            var key = item.Id + (preferLarge ? "|large" : "|medium");
            Task queuedTask;
            lock (_queueLock)
            {
                if (!_queuedItems.TryGetValue(key, out queuedTask))
                {
                    queuedTask = DownloadAsync(item, preferLarge, key);
                    _queuedItems.Add(key, queuedTask);
                }
            }

            await queuedTask;
            await _photoIndex.RestoreCachedThumbnailUrisAsync(
                new[] { item }, preferLarge, _cancellation.Token);
        }

        private async Task DownloadAsync(DriveItem item, bool preferLarge, string key)
        {
            try
            {
                var cancellationToken = _cancellation.Token;
                await _slots.WaitAsync(cancellationToken);
                try
                {
                    await _photoIndex.CacheThumbnailAsync(item, _graph, preferLarge, cancellationToken);
                }
                finally
                {
                    _slots.Release();
                }
            }
            finally
            {
                lock (_queueLock)
                {
                    _queuedItems.Remove(key);
                }
            }
        }

        public void Suspend()
        {
            _cancellation.Cancel();
        }

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
