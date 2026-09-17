using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace LiveDrive.Services
{
    public sealed class CameraBackupStateStore
    {
        private const string UploadedPathsSettingName = "CameraBackupUploadedPaths";
        private const string LastResultSettingName = "CameraBackupLastResult";
        private const int MaximumUploadedPaths = 1000;

        public bool HasUploaded(string path)
        {
            return LoadUploadedPaths().Contains(path);
        }

        public void MarkUploaded(string path)
        {
            var paths = LoadUploadedPaths();
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
            ApplicationData.Current.LocalSettings.Values[UploadedPathsSettingName] = values.Stringify();
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

        public void Clear()
        {
            ApplicationData.Current.LocalSettings.Values.Remove(UploadedPathsSettingName);
            ApplicationData.Current.LocalSettings.Values.Remove(LastResultSettingName);
        }

        private static List<string> LoadUploadedPaths()
        {
            object value;
            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(UploadedPathsSettingName, out value) ||
                string.IsNullOrEmpty(value as string))
            {
                return new List<string>();
            }

            return JsonArray.Parse(value as string).Select(entry => entry.GetString()).ToList();
        }
    }
}
