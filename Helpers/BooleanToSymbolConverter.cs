using System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;

namespace LiveDrive.Helpers
{
    public sealed class BooleanToSymbolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool && (bool)value ? Symbol.Folder : Symbol.Document;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
