using System;
using System.Windows;
using Microsoft.Win32;

namespace ZapretGui.Core
{
    /// <summary>Переключение тёмной/светлой темы во время работы приложения.</summary>
    public static class ThemeService
    {
        private const string DarkPath = "Themes/Dark.xaml";
        private const string LightPath = "Themes/Light.xaml";

        public static event Action? Changed;

        public static ThemeMode Current { get; private set; } = ThemeMode.System;

        public static bool IsLightActive { get; private set; }

        public static void Apply(ThemeMode mode)
        {
            Current = mode;
            var light = mode == ThemeMode.Light || (mode == ThemeMode.System && IsSystemLight());
            IsLightActive = light;

            var app = Application.Current;
            if (app == null) return;

            var url = new Uri(light ? LightPath : DarkPath, UriKind.Relative);
            var dictionary = new ResourceDictionary { Source = url };

            var merged = app.Resources.MergedDictionaries;
            if (merged.Count == 0)
            {
                merged.Add(dictionary);
            }
            else
            {
                // Первый словарь — всегда тема, второй — стили компонентов
                merged[0] = dictionary;
            }

            AppLog.Debug("Тема применена: " + (light ? "светлая" : "тёмная"));
            try { Changed?.Invoke(); } catch { }
        }

        public static bool IsSystemLight()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int i) return i != 0;
            }
            catch { }
            return false;
        }

        public static string ModeText(ThemeMode mode) => mode switch
        {
            ThemeMode.Dark => "Тёмная",
            ThemeMode.Light => "Светлая",
            _ => "Как в системе"
        };
    }
}
