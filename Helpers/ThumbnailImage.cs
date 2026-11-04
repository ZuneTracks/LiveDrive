using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace LiveDrive.Helpers
{
    // Loads thumbnails so XAML can decode them at the tile's rendered size instead of full resolution.
    public static class ThumbnailImage
    {
        public static readonly DependencyProperty UriProperty = DependencyProperty.RegisterAttached(
            "Uri", typeof(string), typeof(ThumbnailImage), new PropertyMetadata(null, OnUriChanged));

        private static readonly DependencyProperty HasLoadedProperty = DependencyProperty.RegisterAttached(
            "HasLoaded", typeof(bool), typeof(ThumbnailImage), new PropertyMetadata(false));

        private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
            "IsHooked", typeof(bool), typeof(ThumbnailImage), new PropertyMetadata(false));

        public static string GetUri(DependencyObject element)
        {
            return (string)element.GetValue(UriProperty);
        }

        public static void SetUri(DependencyObject element, string value)
        {
            element.SetValue(UriProperty, value);
        }

        private static void OnUriChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var image = sender as Image;
            if (image == null)
            {
                return;
            }

            if (!(bool)image.GetValue(IsHookedProperty))
            {
                image.SetValue(IsHookedProperty, true);
                image.Loaded += Image_Loaded;
            }

            Uri uri;
            if (!Uri.TryCreate(args.NewValue as string, UriKind.Absolute, out uri))
            {
                image.Source = null;
                return;
            }

            // Automatic decode-to-render-size only applies when the image is in the live tree
            // before UriSource is set, so wait for Loaded on first realization.
            if ((bool)image.GetValue(HasLoadedProperty))
            {
                Apply(image, uri);
            }
        }

        private static void Image_Loaded(object sender, RoutedEventArgs e)
        {
            var image = (Image)sender;
            image.SetValue(HasLoadedProperty, true);
            Uri uri;
            if (Uri.TryCreate(GetUri(image), UriKind.Absolute, out uri))
            {
                Apply(image, uri);
            }
        }

        private static void Apply(Image image, Uri uri)
        {
            var current = image.Source as BitmapImage;
            if (current != null && uri.Equals(current.UriSource))
            {
                return;
            }

            var bitmap = new BitmapImage();
            image.Source = bitmap;
            bitmap.UriSource = uri;
        }
    }
}
