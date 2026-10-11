using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiveDrive.Models;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
#if BACKGROUND_TASK
using CameraUploadSessionState = LiveDrive.BackgroundTasks.CameraUploadSessionState;
#endif

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
        private const string UploadSessionFileName = "camera-backup-upload-session.json";
        private const string DestinationFolderIdSettingName = "CameraBackupDestinationFolderId";
        private const string ScheduledBackupEnabledSettingName = "CameraBackupScheduledEnabled";
        private const string PendingScanOffsetSettingName = "CameraBackupPendingScanOffset";
        private const string LastResultSettingName = "CameraBackupLastResult";
        private const string LastScanStartedSettingName = "CameraBackupLastScanStarted";
        private const string LastBackgroundActivationSettingName = "CameraBackupLastBackgroundActivation";
        private const string ToastDiagnosticSettingName = "CameraBackupToastDiagnostic";
        private const int MaximumUploadedPaths = 1000;
        private const int MaximumPendingPaths = 100;

        public bool IsScheduledBackupEnabled
        {
            get
            {
                object value;
                return ApplicationData.Current.LocalSettings.Values.TryGetValue(
                    ScheduledBackupEnabledSettingName, out value) && value is bool && (bool)value;
            }
        }

        public void SetScheduledBackupEnabled(bool enabled)
        {
            if (enabled)
            {
                ApplicationData.Current.LocalSettings.Values[ScheduledBackupEnabledSettingName] = true;
            }
            else
            {
                ApplicationData.Current.LocalSettings.Values.Remove(ScheduledBackupEnabledSettingName);
            }
        }

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

        public uint GetPendingScanOffset()
        {
            object value;
            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(PendingScanOffsetSettingName, out value))
            {
                return 0;
            }

            return value is uint ? (uint)value : 0;
        }

        public void SavePendingScanOffset(uint offset)
        {
            ApplicationData.Current.LocalSettings.Values[PendingScanOffsetSettingName] = offset;
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
                    if (DateTimeOffset.Now - properties.DateModified > TimeSpan.FromMinutes(30))
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

        public async Task<CameraUploadSessionState> LoadUploadSessionAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(UploadSessionFileName);
                var json = JsonObject.Parse(await FileIO.ReadTextAsync(file));
                return new CameraUploadSessionState
                {
                    FilePath = json.GetNamedString("filePath", string.Empty),
                    DestinationFolderId = json.GetNamedString("destinationFolderId", string.Empty),
                    FileSize = (ulong)json.GetNamedNumber("fileSize", 0),
                    FileModifiedUtc = json.GetNamedString("fileModifiedUtc", string.Empty),
                    UploadUrl = json.GetNamedString("uploadUrl", string.Empty),
                    NextOffset = (long)json.GetNamedNumber("nextOffset", 0)
                };
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public async Task SaveUploadSessionAsync(CameraUploadSessionState session)
        {
            var json = new JsonObject();
            json.SetNamedValue("filePath", JsonValue.CreateStringValue(session.FilePath ?? string.Empty));
            json.SetNamedValue("destinationFolderId", JsonValue.CreateStringValue(session.DestinationFolderId ?? string.Empty));
            json.SetNamedValue("fileSize", JsonValue.CreateNumberValue(session.FileSize));
            json.SetNamedValue("fileModifiedUtc", JsonValue.CreateStringValue(session.FileModifiedUtc ?? string.Empty));
            json.SetNamedValue("uploadUrl", JsonValue.CreateStringValue(session.UploadUrl ?? string.Empty));
            json.SetNamedValue("nextOffset", JsonValue.CreateNumberValue(session.NextOffset));
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                UploadSessionFileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, json.Stringify());
        }

        public async Task ClearUploadSessionAsync()
        {
            try
            {
                await (await ApplicationData.Current.LocalFolder.GetFileAsync(UploadSessionFileName)).DeleteAsync();
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

        public void SaveLastBackgroundActivation(DateTimeOffset timestamp)
        {
            ApplicationData.Current.LocalSettings.Values[LastBackgroundActivationSettingName] = timestamp.ToString("o");
        }

        public DateTimeOffset? GetLastBackgroundActivation()
        {
            object value;
            DateTimeOffset timestamp;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(LastBackgroundActivationSettingName, out value) &&
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
            ApplicationData.Current.LocalSettings.Values.Remove(ScheduledBackupEnabledSettingName);
            ApplicationData.Current.LocalSettings.Values.Remove(LastBackgroundActivationSettingName);
            ApplicationData.Current.LocalSettings.Values.Remove(PendingScanOffsetSettingName);
            try
            {
                await (await ApplicationData.Current.LocalFolder.GetFileAsync(UploadedPathsFileName)).DeleteAsync();
            }
            catch (FileNotFoundException)
            {
            }
            await ClearUploadSessionAsync();
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
