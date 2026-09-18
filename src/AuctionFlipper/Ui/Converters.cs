using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AuctionFlipper.Ui;

/// <summary>
/// Two-way match between an enum property and a constant, so a group of radio buttons can drive a
/// single enum without a bool property per option.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter is string name && targetType.IsEnum)
            return Enum.Parse(targetType, name);

        // Unchecking a radio button is the other one being checked; ignore it.
        return Binding.DoNothing;
    }
}

/// <summary>
/// Visibility from a bool, a string or a count. Accepting all three keeps the XAML free of
/// near-identical converters for "is true", "has text" and "has any".
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            null => false,
            _ => true,
        };

        if (parameter as string == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Formats a 0-1 fraction as a percentage for progress-style displays.</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? $"{d * 100:0}%" : "0%";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Binds a double directly to a text box through invariant parsing.</summary>
public sealed class DoubleTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? d.ToString("0.####", CultureInfo.InvariantCulture) : "0";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => double.TryParse(value as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : Binding.DoNothing;
}

/// <summary>Same as <see cref="DoubleTextConverter"/> but for a percentage shown as 0-100.</summary>
public sealed class PercentTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? (d * 100).ToString("0.##", CultureInfo.InvariantCulture) : "0";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => double.TryParse(value as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed / 100.0
            : Binding.DoNothing;
}

/// <summary>Two-way match between an int property and a constant, for radio-button groups.</summary>
public sealed class IntMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i
           && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)
           && i == p;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool isChecked && isChecked
           && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)
            ? p
            : Binding.DoNothing;
}

public sealed class IntTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? i.ToString(CultureInfo.InvariantCulture) : "0";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => int.TryParse(value as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : Binding.DoNothing;
}
