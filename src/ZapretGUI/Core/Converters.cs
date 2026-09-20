using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ZapretGui.Core
{
    /// <summary>Инвертирует bool (для галочек вида «выключено»).</summary>
    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : true;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : false;
    }

    /// <summary>Строка → Visibility (пусто = Collapsed; параметр "invert" — наоборот).</summary>
    public sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var empty = string.IsNullOrWhiteSpace(value as string);
            if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase)) empty = !empty;
            return empty ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Ключ цвета ("Success"/"Warning"/"Danger"/"Info"/"Muted") → кисть из ресурсов темы.</summary>
    public sealed class SeverityBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = value as string ?? "Info";
            var name = key + "Brush";
            var brush = Application.Current?.TryFindResource(name) as Brush;
            return brush ?? (Application.Current?.TryFindResource("TextBrush") as Brush ?? Brushes.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Ключ цвета ("Success"/"Warning"...) → приглушённая кисть для фона баннеров.</summary>
    public sealed class SoftSeverityBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = value as string ?? "Info";
            var brush = Application.Current?.TryFindResource(key + "SoftBrush") as Brush;
            return brush ?? (Application.Current?.TryFindResource("ElevatedBrush") as Brush ?? Brushes.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>true → Visible, false → Collapsed (с параметром "invert").</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var flag = value is bool b && b;
            if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Переводит значение прогресса в долю ширины индикатора (0.0..1.0).</summary>
    public sealed class ProgressScaleConverter : IValueConverter, IMultiValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var current = value is double d ? d : value is int i ? i : 0;
            var maximum = 100d;
            if (parameter is double p && p > 0) maximum = p;
            return maximum <= 0 ? 0d : Math.Clamp(current / maximum, 0d, 1d);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values != null && values.Length >= 2)
            {
                var current = values[0] is double d ? d : values[0] is int i ? i : 0d;
                var maximum = values[1] is double max ? max : values[1] is int mi ? (double)mi : 100d;
                var minimum = 0d;
                if (values.Length >= 3)
                {
                    if (values[2] is double min) minimum = min;
                    else if (values[2] is int minI) minimum = minI;
                }

                var range = maximum - minimum;
                if (range <= 0) return 0d;
                return Math.Clamp((current - minimum) / range, 0d, 1d);
            }
            return 0d;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => Array.Empty<object>();
    }

    /// <summary>Подсвечивает карточку шага мастера, если её номер совпадает с текущим.</summary>
    public sealed class WizardStepBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var current = value is int number ? number : -1;
            var step = int.TryParse(parameter?.ToString(), out var parsed) ? parsed : -2;
            var resource = current == step ? "AccentSoftBrush" : "ElevatedBrush";
            return Application.Current?.TryFindResource(resource) as Brush ?? Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Делает неактивные шаги мастера менее контрастными.</summary>
    public sealed class WizardStepOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var current = value is int number ? number : -1;
            var step = int.TryParse(parameter?.ToString(), out var parsed) ? parsed : -2;
            return current == step ? 1d : 0.62d;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Возвращает размер до масштабирования так, чтобы содержимое заняло ровно ширину окна.</summary>
    public sealed class ZoomedViewportConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var viewport = values.Length > 0 && values[0] is double width ? width : 0d;
            var zoom = values.Length > 1 && values[1] is double scale ? scale : 1d;
            return viewport > 0 && zoom > 0 ? viewport / zoom : viewport;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => Array.Empty<object>();
    }

    /// <summary>Число 0 → Collapsed, иначе Visible (для счётчиков).</summary>
    public sealed class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var count = value is int i ? i : 0;
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Цвет статуса обхода → кисть (зелёный при работе, серый при остановке).</summary>
    public sealed class BypassStateBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var running = value is BypassState state && state is BypassState.RunningStandalone or BypassState.RunningService;
            var busy = value is BypassState.Starting or BypassState.Stopping;
            var key = running ? "SuccessBrush" : busy ? "WarningBrush" : "MutedBrush";
            return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
