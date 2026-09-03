using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiveDrive.Models;
using LiveDrive.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LiveDrive.Helpers
{
    public static class OneDriveFolderPicker
    {
        public static async Task<DriveItem> PickAsync(IGraphClient graph, string title, string instructions, string primaryButtonText)
        {
            var root = new DriveItem { Name = "OneDrive", IsFolder = true };
            var currentFolder = root;
            var history = new Stack<DriveItem>();
            var folderList = new ListView { SelectionMode = ListViewSelectionMode.None, Height = 320 };
            var folderPath = new TextBlock { Text = root.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var backButton = new Button { Content = "Back", IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
            var navigation = new StackPanel { Orientation = Orientation.Horizontal };
            navigation.Children.Add(backButton);
            navigation.Children.Add(folderPath);
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = instructions,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            content.Children.Add(navigation);
            content.Children.Add(folderList);
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = "Cancel"
            };

            Func<Task> loadFolder = null;
            loadFolder = async () =>
            {
                folderList.Items.Clear();
                folderPath.Text = currentFolder.Name;
                backButton.IsEnabled = history.Count > 0;
                var folders = (await graph.GetChildrenAsync(currentFolder.Id))
                    .Where(item => item.IsFolder)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (folders.Count == 0)
                {
                    folderList.Items.Add(new TextBlock
                    {
                        Text = "No subfolders here.",
                        Opacity = 0.65,
                        Margin = new Thickness(12)
                    });
                    return;
                }

                foreach (var folder in folders)
                {
                    var openButton = new Button
                    {
                        Content = folder.Name,
                        Tag = folder,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    openButton.Click += async (sender, args) =>
                    {
                        history.Push(currentFolder);
                        currentFolder = (DriveItem)((FrameworkElement)sender).Tag;
                        await loadFolder();
                    };
                    folderList.Items.Add(openButton);
                }
            };

            backButton.Click += async (sender, args) =>
            {
                if (history.Count > 0)
                {
                    currentFolder = history.Pop();
                    await loadFolder();
                }
            };

            await loadFolder();
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? currentFolder : null;
        }
    }
}
