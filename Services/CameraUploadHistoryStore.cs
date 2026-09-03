using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace LiveDrive.Services
{
    public sealed class CameraUploadHistoryStore
    {
        private const string SettingName = "CameraUploadHistory";
        private const int MaximumItems = 50;

        public Task<IReadOnlyList<string>> LoadAsync()
        {
            object storedValue;
            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingName, out storedValue))
            {
                return Task.FromResult((IReadOnlyList<string>)new List<string>());
            }

            var values = JsonArray.Parse(storedValue as string);
            var names = new List<string>();
            foreach (var value in values)
            {
                names.Add(value.GetString());
            }
            return Task.FromResult((IReadOnlyList<string>)names);
        }

        public async Task AddAsync(string name)
        {
            var names = (await LoadAsync()).ToList();
            names.Insert(0, name);
            if (names.Count > MaximumItems)
            {
                names.RemoveRange(MaximumItems, names.Count - MaximumItems);
            }
            var values = new JsonArray();
            foreach (var item in names)
            {
                values.Add(JsonValue.CreateStringValue(item));
            }
            ApplicationData.Current.LocalSettings.Values[SettingName] = values.Stringify();
        }

        public void Clear()
        {
            ApplicationData.Current.LocalSettings.Values.Remove(SettingName);
        }
    }
}
