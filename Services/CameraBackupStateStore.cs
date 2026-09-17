using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace LiveDrive.Services
{
    public sealed class CameraBackupStateStore
    {
        private const string UploadedPathsSettingName = "CameraBackupUploadedPaths";
        private const string UploadedPathsFileName = "camera-backup-uploaded-paths.json";
        private const string LastResultSettingName = "CameraBackupLastResult";
        private const int MaximumUploadedPaths = 1000;

        public async Task<bool> HasUploadedAsync(string path)
        {
            return (await LoadUploadedPathsAsync()).Contains(path);
        }

        public async Task MarkUploadedAsync(string path)
        {
            var paths = await LoadUploadedPathsAsync();
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

        public async Task ClearAsync()
        {
            ApplicationData.Current.LocalSettings.Values.Remove(UploadedPathsSettingName);
            ApplicationData.Current.LocalSettings.Values.Remove(LastResultSettingName);
            try
            {
                await (await ApplicationData.Current.LocalFolder.GetFileAsync(UploadedPathsFileName)).DeleteAsync();
            }
            catch (FileNotFoundException)
            {
            }
        }

        private static async Task<List<string>> LoadUploadedPathsAsync()
        {
            object value;
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(UploadedPathsSettingName, out value) &&
                !string.IsNullOrEmpty(value as string))
            {
                return JsonArray.Parse(value as string).Select(entry => entry.GetString()).ToList();
            }

            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(UploadedPathsFileName);
                return JsonArray.Parse(await FileIO.ReadTextAsync(file)).Select(entry => entry.GetString()).ToList();
            }
            catch (FileNotFoundException)
            {
                return new List<string>();
            }
        }
    }
}
