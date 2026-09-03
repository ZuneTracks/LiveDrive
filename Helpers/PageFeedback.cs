using System;
using System.Threading.Tasks;
using Windows.UI.Popups;

namespace LiveDrive.Helpers
{
    public static class PageFeedback
    {
        public static async Task ShowErrorAsync(Exception exception)
        {
            await new MessageDialog(exception.Message, "LiveDrive").ShowAsync();
        }

        public static async Task ShowInfoAsync(string message)
        {
            await new MessageDialog(message, "LiveDrive").ShowAsync();
        }
    }
}
