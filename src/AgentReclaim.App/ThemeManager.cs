using System.Windows;
using System.Windows.Media;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Settings;
using Microsoft.Win32;

namespace AgentReclaim.App;

/// <summary>
/// Light, dark or follow Windows. Standard controls use WPF's Fluent theme (Application.ThemeMode, which
/// also darkens the title bar); the app's own colours are brushes swapped here and used via DynamicResource.
/// </summary>
internal static class ThemeManager
{
    private static ThemePreference _preference = ThemePreference.System;
    private static bool _listening;

    private static readonly Dictionary<string, (Color Light, Color Dark)> Palette = new()
    {
        ["Accent"] = (Color.FromRgb(0xD9, 0x77, 0x2B), Color.FromRgb(0xF0, 0x95, 0x4E)),
        ["Muted"] = (Color.FromRgb(0x6B, 0x6B, 0x6B), Color.FromRgb(0xA8, 0xA8, 0xA8)),
        ["Card"] = (Color.FromRgb(0xF6, 0xF5, 0xF3), Color.FromRgb(0x2B, 0x2B, 0x2B)),
        ["CardBorder"] = (Color.FromRgb(0xE4, 0xE1, 0xDC), Color.FromRgb(0x3E, 0x3E, 0x3E)),
    };

    public static bool IsDark { get; private set; }

    public static void Apply(ThemePreference preference)
    {
        _preference = preference;
        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }

#pragma warning disable WPF0001 // Fluent ThemeMode is marked experimental in .NET 9
        var mode = preference switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (Application.Current.ThemeMode != mode) Application.Current.ThemeMode = mode;
#pragma warning restore WPF0001

        ApplyPalette();
        Application.Current.Resources["Grid"] = GridStyle();
    }

    /// <summary>The app's grid settings on top of the active theme's DataGrid style (rebuilt on every switch).</summary>
    private static Style GridStyle()
    {
        var style = new Style(typeof(System.Windows.Controls.DataGrid),
            Application.Current.TryFindResource(typeof(System.Windows.Controls.DataGrid)) as Style);
        void Set(DependencyProperty property, object value) => style.Setters.Add(new Setter(property, value));
        Set(System.Windows.Controls.DataGrid.AutoGenerateColumnsProperty, false);
        Set(System.Windows.Controls.DataGrid.IsReadOnlyProperty, true);
        Set(System.Windows.Controls.DataGrid.HeadersVisibilityProperty, System.Windows.Controls.DataGridHeadersVisibility.Column);
        Set(System.Windows.Controls.DataGrid.GridLinesVisibilityProperty, System.Windows.Controls.DataGridGridLinesVisibility.Horizontal);
        Set(System.Windows.Controls.DataGrid.HorizontalGridLinesBrushProperty, new DynamicResourceExtension("CardBorder"));
        Set(System.Windows.Controls.DataGrid.RowHeaderWidthProperty, 0.0);
        Set(System.Windows.Controls.DataGrid.SelectionModeProperty, System.Windows.Controls.DataGridSelectionMode.Single);
        Set(System.Windows.Controls.DataGrid.EnableRowVirtualizationProperty, true);
        return style;
    }

    private static void ApplyPalette()
    {
        IsDark = _preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.Dark => true,
            _ => WindowsAppsUseDarkMode(),
        };
        foreach (var (key, colors) in Palette)
        {
            var brush = new SolidColorBrush(IsDark ? colors.Dark : colors.Light);
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
        Log.Debug($"Theme: {_preference} ({(IsDark ? "dark" : "light")}).");
    }

    /// <summary>Settings → Personalization → Colors → "Choose your default app mode".</summary>
    public static bool WindowsAppsUseDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _preference != ThemePreference.System) return;
        Application.Current?.Dispatcher.BeginInvoke(ApplyPalette);
    }
}
