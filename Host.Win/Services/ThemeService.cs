// File: Host.Win/Services/ThemeService.cs
using System;
using System.Reflection;
using System.Windows;
using Host.Win.Models;
using Microsoft.Win32;

namespace Host.Win.Services
{
    public sealed class ThemeService
    {
        private const string LightThemePath = "Themes/LightTheme.xaml";
        private const string DarkThemePath = "Themes/DarkTheme.xaml";
        private const string GlassThemePath = "Themes/GlassTheme.xaml";

        public AppTheme GetSystemTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int value)
                {
                    return value == 0 ? AppTheme.Dark : AppTheme.Light;
                }
            }
            catch
            {
                // Default below
            }

            return AppTheme.Dark;
        }

        public void ApplyTheme(AppTheme theme)
        {
            var themePath = theme switch
            {
                AppTheme.Light => LightThemePath,
                AppTheme.Glass => GlassThemePath,
                _ => DarkThemePath
            };
            var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
            var uri = new Uri($"pack://application:,,,/{assemblyName};component/{themePath}", UriKind.Absolute);

            var resources = System.Windows.Application.Current.Resources;
            for (int i = resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                var source = resources.MergedDictionaries[i].Source?.OriginalString ?? string.Empty;
                if (source.Contains("LightTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                    source.Contains("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                    source.Contains("GlassTheme.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    resources.MergedDictionaries.RemoveAt(i);
                }
            }

            resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
        }
    }
}
