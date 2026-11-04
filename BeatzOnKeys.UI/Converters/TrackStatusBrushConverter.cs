using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.UI.Converters;

public sealed class TrackStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is TrackStatus status
            ? status switch
            {
                TrackStatus.Ready => "SuccessBrush",
                TrackStatus.Processing => "AccentBrush",
                TrackStatus.Error => "ErrorBrush",
                _ => "TextTertiaryBrush"
            }
            : "TextTertiaryBrush";

        return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
