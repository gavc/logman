using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LogMan
{
    public class HighlightIndexToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush[] HighlightBrushes =
        {
            new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xCC)),
            new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0xCC)),
            new SolidColorBrush(Color.FromRgb(0xCC, 0xE5, 0xFF)),
            new SolidColorBrush(Color.FromRgb(0xCC, 0xF2, 0xFF)),
            new SolidColorBrush(Color.FromRgb(0xD6, 0xF5, 0xCC)),
            new SolidColorBrush(Color.FromRgb(0xE6, 0xFF, 0xCC)),
            new SolidColorBrush(Color.FromRgb(0xFF, 0xE6, 0xCC)),
            new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0xE5)),
            new SolidColorBrush(Color.FromRgb(0xCC, 0xFF, 0xF2)),
            new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))
        };

        static HighlightIndexToBrushConverter()
        {
            foreach (var brush in HighlightBrushes)
            {
                brush.Freeze();
            }
        }

        public static HighlightIndexToBrushConverter Instance { get; } = new HighlightIndexToBrushConverter();

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int index && index >= 0 && index < HighlightBrushes.Length)
            {
                return HighlightBrushes[index];
            }
            return Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
