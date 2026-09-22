using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace LiveDrive.Services
{
#if BACKGROUND_TASK
    internal sealed class CameraBackupStateStore
#else
    public sealed class CameraBackupStateStore
#endif
    {
        private const string UploadedPathsSettingName = "CameraBackupUploadedPaths";
        private const string UploadedPathsFileName = "camera-backup-uploaded-paths.json";
        private const string PendingPathsFileName = "camera-backup-pending-paths.json";
        private const string BackgroundRunLockFileName = "camera-backup-background.lock";
        private const string DestinationFolderIdSettingName = "CameraBackupDestinationFolderId";
        private const string LastResultSettingName = "CameraBackupLastResult";
        private const string LastScanStartedSettingName = "CameraBackupLastScanStarted";
        private const string ToastDiagnosticSettingName = "CameraBackupToastDiagnostic";
        private const int MaximumUploadedPaths = 1000;
        private const int MaximumPendingPaths = 100;

        public async Task<IReadOnlyList<string>> LoadUploadedPathsAsync()
        {
            return await LoadPathsAsync(UploadedPathsSettingName, UploadedPathsFileName);
        }

        public async Task<bool> HasUploadedAsync(string path)
        {
            return (await LoadUploadedPathsAsync()).Contains(path);
        }

        public async Task MarkUploadedAsync(string path)
        {
            var paths = (await LoadUploadedPathsAsync()).ToList();
            paths.Remove(path);
            paths.Insert(0, path);
            if (paths.Count > MaximumUploadedPaths)
            {
                paths.RemoveRange(MaximumUploadedPaths, paths.Count - MaximumUploadedPaths);
            }

            var values = new JsonArray();
            foreach (var uploadedPath in paths)
            {
                values.Add(JsonValue.CreateStringValue(uploadedPath));
            }
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                UploadedPathsFileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, values.Stringify());
            ApplicationData.Current.LocalSettings.Values.Remove(UploadedPathsSettingName);
        }

        public Task<IReadOnlyList<string>> LoadPendingPathsAsync()
        {
            return LoadPathsAsync(null, PendingPathsFileName);
        }

        public async Task SavePendingPathsAsync(IEnumerable<string> paths)
        {
            var distinctPaths = paths
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumPendingPaths)
                .ToList();
            var values = new JsonArray();
            foreach (var path in distinctPaths)
            {
                values.Add(JsonValue.CreateStringValue(path));
            }
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                PendingPathsFileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, values.Stringify());
        }

        public string GetDestinationFolderId()
        {
            object value;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(
                DestinationFolderIdSettingName, out value) ? value as string : string.Empty;
        }

        public void SaveDestinationFolderId(string folderId)
        {
            ApplicationData.Current.LocalSettings.Values[DestinationFolderIdSettingName] = folderId;
        }

        public async Task<bool> TryAcquireBackgroundRunLockAsync()
        {
            try
            {
                await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    BackgroundRunLockFileName, CreationCollisionOption.FailIfExists);
                return true;
            }
            catch (IOException)
            {
                try
                {
                    var existingLock = await ApplicationData.Current.LocalFolder.GetFileAsync(BackgroundRunLockFileName);
                    var properties = await existingLock.GetBasicPropertiesAsync();
                    if (DateTimeOffset.Now - properties.DateModified > TimeSpan.FromMinutes(2))
                    {
                        await existingLock.DeleteAsync();
                        await ApplicationData.Current.LocalFolder.CreateFileAsync(
                            BackgroundRunLockFileName, CreationCollisionOption.FailIfExists);
                        return true;
                    }
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException)
                {
                }
                return false;
            }
        }

        public async Task ReleaseBackgroundRunLockAsync()
        {
            try
            {
                await (await ApplicationData.Current.LocalFolder.GetFileAsync(BackgroundRunLockFileName)).DeleteAsync();
            }
            catch (FileNotFoundException)
            {
            }
        }

        public void SaveLastResult(string result)
        {
            ApplicationData.Current.LocalSettings.Values[LastResultSettingName] = result;
        }

        public string GetLastResult()
        {
            object value;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(LastResultSettingName, out value)
                ? value as string
                : string.Empty;
        }

        public void SaveLastScanStarted(DateTimeOffset timestamp)
        {
            ApplicationData.Current.LocalSettings.Values[LastScanStartedSettingName] = timestamp.ToString("o");
        }

        public DateTimeOffset? GetLastScanStarted()
        {
            object value;
            DateTimeOffset timestamp;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(LastScanStartedSettingName, out value) &&
                DateTimeOffset.TryParse(value as string, out timestamp)
                ? timestamp
                : (DateTimeOffset?)null;
        }

        public void SaveToastDiagnostic(string result)
        {
            ApplicationData.Current.LocalSettings.Values[ToastDiagnosticSettingName] = result;
        }

        public string GetToastDiagnostic()
        {
            object value;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(ToastDiagnosticSettingName, out value)
                ? value as string
                : string.Empty;
        }

        public async Task ClearAsync()
        {
            ApplicationData.Current.LocalSettings.Values.Remove(UploadedPathsSettingName);
            ApplicationData.Current.LocalSettings.Values.Remove(LastResultSettingName);
            ApplicationData.Current.LocalSettings.Values.Remove(LastScanStartedSettingName);
            ApplicationData.Current.LocalSettings.Values.Remove(ToastDiagnosticSettingName);
            ApplicationData.Current.LocalSettings.Values.Remove(DestinationFolderIdSettingName);
            try
            {
                await (await ApplicationData.Current.LocalFolder.GetFileAsync(UploadedPathsFileName)).DeleteAsync();
            }
            catch (FileNotFoundException)
            {
            }
            try
            {
                await (await ApplicationData.Current.LocalFolder.GetFileAsync(PendingPathsFileName)).DeleteAsync();
            }
            catch (FileNotFoundException)
            {
            }
            try
            {
                await (await ApplicationData.Current.LocalFolder.GetFileAsync(BackgroundRunLockFileName)).DeleteAsync();
            }
            catch (FileNotFoundException)
            {
            }
        }

        private static async Task<IReadOnlyList<string>> LoadPathsAsync(
            string settingName, string fileName)
        {
            object value;
            if (!string.IsNullOrEmpty(settingName) &&
                ApplicationData.Current.LocalSettings.Values.TryGetValue(settingName, out value) &&
                !string.IsNullOrEmpty(value as string))
            {
                return JsonArray.Parse(value as string).Select(entry => entry.GetString()).ToList();
            }

            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(fileName);
                return JsonArray.Parse(await FileIO.ReadTextAsync(file)).Select(entry => entry.GetString()).ToList();
            }
            catch (FileNotFoundException)
            {
                return new List<string>();
            }
        }
    }
}
