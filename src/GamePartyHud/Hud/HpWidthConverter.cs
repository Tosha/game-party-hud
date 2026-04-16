using System;
using System.Globalization;
using System.Windows.Data;

namespace GamePartyHud.Hud;

/// <summary>Converts a float HP% [0..1] to a pixel width; ConverterParameter is the max width.</summary>
public sealed class HpWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        float pct = value is float f ? f : 0f;
        double max = parameter is string s &&
                     double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v : 180.0;
        return Math.Clamp(pct, 0f, 1f) * max;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
