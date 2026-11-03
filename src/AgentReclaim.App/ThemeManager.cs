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
        // Warm surfaces in steps: window, sidebar, cards. Green, amber and red only ever mean a state; orange is the brand and actions.
        ["AppBackground"] = (Color.FromRgb(0xF6, 0xF2, 0xEC), Color.FromRgb(0x1A, 0x19, 0x18)),
        ["Sidebar"] = (Color.FromRgb(0xEE, 0xE8, 0xDF), Color.FromRgb(0x21, 0x1F, 0x1E)),
        ["Card"] = (Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x2A, 0x28, 0x26)),
        ["CardBorder"] = (Color.FromRgb(0xE6, 0xDF, 0xD4), Color.FromRgb(0x3E, 0x3B, 0x37)),
        ["Muted"] = (Color.FromRgb(0x65, 0x5F, 0x56), Color.FromRgb(0xAD, 0xA7, 0x9F)),
        ["Accent"] = (Color.FromRgb(0xA3, 0x48, 0x08), Color.FromRgb(0xF5, 0x99, 0x4F)),
        ["OnAccent"] = (Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x1A, 0x19, 0x18)),
        ["SelectionFill"] = (Color.FromRgb(0xFB, 0xE6, 0xD3), Color.FromRgb(0x4A, 0x33, 0x22)),
        ["HoverFill"] = (Color.FromRgb(0xF3, 0xED, 0xE4), Color.FromRgb(0x33, 0x30, 0x2D)),
        ["Good"] = (Color.FromRgb(0x15, 0x6E, 0x40), Color.FromRgb(0x4C, 0xC3, 0x8A)),
        ["Warn"] = (Color.FromRgb(0x83, 0x52, 0x00), Color.FromRgb(0xF0, 0xB4, 0x29)),
        ["Danger"] = (Color.FromRgb(0xC2, 0x34, 0x2B), Color.FromRgb(0xF7, 0x79, 0x6F)),
        ["AgentClaude"] = (Color.FromRgb(0xD9, 0x77, 0x57), Color.FromRgb(0xD9, 0x77, 0x57)),
        ["AgentCodex"] = (Color.FromRgb(0x10, 0xA3, 0x7F), Color.FromRgb(0x10, 0xA3, 0x7F)),
        ["AgentGemini"] = (Color.FromRgb(0x42, 0x85, 0xF4), Color.FromRgb(0x42, 0x85, 0xF4)),
        // History chart: categorical slots 1-4 of the validated reference palette (light / dark steps), plus ink for the total.
        ["Series1"] = (Color.FromRgb(0x2A, 0x78, 0xD6), Color.FromRgb(0x39, 0x87, 0xE5)),
        ["Series2"] = (Color.FromRgb(0xEB, 0x68, 0x34), Color.FromRgb(0xD9, 0x59, 0x26)),
        ["Series3"] = (Color.FromRgb(0x1B, 0xAF, 0x7A), Color.FromRgb(0x19, 0x9E, 0x70)),
        ["Series4"] = (Color.FromRgb(0xED, 0xA1, 0x00), Color.FromRgb(0xC9, 0x85, 0x00)),
        ["ChartInk"] = (Color.FromRgb(0x0B, 0x0B, 0x0B), Color.FromRgb(0xF0, 0xEF, 0xEC)),
        ["ChartGrid"] = (Color.FromRgb(0xE4, 0xE1, 0xDC), Color.FromRgb(0x38, 0x38, 0x35)),
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
        Set(System.Windows.Controls.DataGrid.BackgroundProperty, new DynamicResourceExtension("Card"));
        Set(System.Windows.Controls.DataGrid.RowBackgroundProperty, Brushes.Transparent);
        Set(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0));
        Set(System.Windows.Controls.DataGrid.ColumnHeaderStyleProperty, Application.Current.FindResource("GridHeader"));
        Set(System.Windows.Controls.DataGrid.RowStyleProperty, Application.Current.FindResource("GridRow"));
        Set(System.Windows.Controls.DataGrid.CellStyleProperty, Application.Current.FindResource("GridCell"));
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
