using System.Globalization;
using System.Windows.Data;

namespace VibeCode.UI;

public sealed class JarvisReadyConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is false;
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter as string == "any") return values.Any(v => v is true);
        if (parameter as string == "listen") return values.Length >= 2 && (values[1] is true || values[0] is false);
        return values.Length >= 3 && values[0] is string input && !string.IsNullOrWhiteSpace(input)
            && values[1] is false && values[2] is false;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public sealed class JarvisRoleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "user" => "You", "assistant" => "Jarvis", "system" => "Status", var role => role ?? "Jarvis",
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class JarvisStateLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "listening" => "Listening", "thinking" => "Thinking", "speaking" => "Speaking", "error" => "Needs attention", _ => "Ready",
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
