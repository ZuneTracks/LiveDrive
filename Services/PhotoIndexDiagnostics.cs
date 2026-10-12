using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LiveDrive.Services
{
    public sealed class PhotoIndexDiagnostics
    {
        private const string LogFileName = "photo-index-diagnostics.log";
        private const int MaximumEntries = 150;
        private const int MaximumDetailLength = 240;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        public async Task<IReadOnlyList<string>> LoadAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(LogFileName);
                return (await FileIO.ReadLinesAsync(file)).ToList();
            }
            catch (System.IO.FileNotFoundException)
            {
                return new List<string>();
            }
        }

        public async Task AppendAsync(string eventName, string detail = null)
        {
            await _writeLock.WaitAsync();
            try
            {
                var entries = (await LoadAsync()).ToList();
                var entry = DateTimeOffset.Now.ToString("o") + " | " + eventName;
                if (!string.IsNullOrEmpty(detail))
                {
                    entry += " | " + SanitizeDetail(detail);
                }

                entries.Add(entry);
                if (entries.Count > MaximumEntries)
                {
                    entries.RemoveRange(0, entries.Count - MaximumEntries);
                }

                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    LogFileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteLinesAsync(file, entries);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task ClearAsync()
        {
            try
            {
                await (await ApplicationData.Current.LocalFolder.GetFileAsync(LogFileName)).DeleteAsync();
            }
            catch (System.IO.FileNotFoundException)
            {
            }
        }

        private static string SanitizeDetail(string detail)
        {
            var sanitized = detail.Replace("\r", " ").Replace("\n", " ").Trim();
            return sanitized.Length <= MaximumDetailLength
                ? sanitized
                : sanitized.Substring(0, MaximumDetailLength) + "…";
        }
    }
}
