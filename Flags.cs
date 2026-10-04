using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemnawaveMonitor;

// Код страны ("NL") -> картинка флага из Flags/nl.png (null, если флага нет)
public class FlagConverter : IValueConverter
{
    static readonly Dictionary<string, ImageSource?> Cache = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var code = (value as string ?? "").Trim().ToLowerInvariant();
        if (code.Length != 2) return null;
        if (Cache.TryGetValue(code, out var img)) return img;
        try
        {
            var bi = new BitmapImage(new Uri($"pack://application:,,,/Flags/{code}.png", UriKind.Absolute));
            bi.Freeze();
            img = bi;
        }
        catch { img = null; }
        Cache[code] = img;
        return img;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
