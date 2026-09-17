using System;
using LiveDrive.Models;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LiveDrive.Helpers
{
    public static class FileInfoDialog
    {
        public static IAsyncOperation<ContentDialogResult> ShowAsync(DriveItem item)
        {
            var content = new StackPanel { Width = 320 };
            AddField(content, "Name", item.Name);
            AddField(content, "Type", item.IsFolder ? "Folder" : string.IsNullOrEmpty(item.MimeType) ? "File" : item.MimeType);
            if (!item.IsFolder)
            {
                AddField(content, "Size", item.Detail);
            }
            AddField(content, "Created", FormatDate(item.Created));
            AddField(content, "Modified", FormatDate(item.LastModified));
            if (!string.IsNullOrEmpty(item.DateTaken))
            {
                AddField(content, "Photo taken", FormatDate(item.DateTaken));
            }
            AddField(content, "OneDrive location", FormatLocation(item.OneDriveLocation));
            AddField(content, "Device location", "Not stored on this device.");
            return new ContentDialog
            {
                Title = "File info",
                Content = content,
                CloseButtonText = "Close"
            }.ShowAsync();
        }

        private static void AddField(Panel panel, string label, string value)
        {
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 8, 0, 2)
            });
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(value) ? "Unavailable" : value,
                TextWrapping = TextWrapping.Wrap
            });
        }

        private static string FormatDate(string value)
        {
            DateTimeOffset date;
            return DateTimeOffset.TryParse(value, out date) ? date.ToLocalTime().ToString("g") : value;
        }

        private static string FormatLocation(string path)
        {
            const string RootPrefix = "/drive/root:";
            return string.IsNullOrEmpty(path) ? "Unavailable" :
                path.StartsWith(RootPrefix, StringComparison.OrdinalIgnoreCase)
                    ? "OneDrive" + path.Substring(RootPrefix.Length)
                    : path;
        }
    }
}
