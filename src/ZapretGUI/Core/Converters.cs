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
