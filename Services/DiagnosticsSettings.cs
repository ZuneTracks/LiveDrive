using Windows.Storage;

namespace LiveDrive.Services
{
    public static class DiagnosticsSettings
    {
        private const string ShowToolsSettingName = "ShowDiagnosticsTools";

        public static bool ShowTools
        {
            get
            {
                object value;
                return ApplicationData.Current.LocalSettings.Values.TryGetValue(
                    ShowToolsSettingName, out value) && value is bool && (bool)value;
            }
            set
            {
                ApplicationData.Current.LocalSettings.Values[ShowToolsSettingName] = value;
            }
        }
    }
}
