using Windows.UI.Xaml.Navigation;

namespace LiveDrive.Pages
{
    public sealed class VideosPage : PhotosPage
    {
        public VideosPage()
        {
            NavigationCacheMode = NavigationCacheMode.Required;
        }

        protected override bool IsVideoLibraryPage => true;
    }
}
