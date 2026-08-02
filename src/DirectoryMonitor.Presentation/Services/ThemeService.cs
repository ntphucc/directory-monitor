using System.Windows;
using DirectoryMonitor.Application;
using DirectoryMonitor.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DirectoryMonitor.Presentation.Services;

public sealed class ThemeService : IThemeService
{
    private readonly ILogger<ThemeService> _logger;
    private bool _disposed;

    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Apply(AppThemeMode.System);
    }

    public AppThemeMode Mode { get; private set; } = AppThemeMode.System;
    public AppThemeMode EffectiveMode { get; private set; } = AppThemeMode.Light;
    public event EventHandler? ThemeChanged;

    public void Apply(AppThemeMode mode)
    {
        Mode = mode;
        var effective = mode == AppThemeMode.System ? ReadSystemTheme() : mode;
        ApplyPalette(effective);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Mode != AppThemeMode.System || System.Windows.Application.Current is null) return;
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ApplyPalette(ReadSystemTheme()));
    }

    private AppThemeMode ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? AppThemeMode.Dark : AppThemeMode.Light;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read the Windows application theme; Light theme will be used");
            return AppThemeMode.Light;
        }
    }

    private void ApplyPalette(AppThemeMode effective)
    {
        if (System.Windows.Application.Current is null) return;
        try
        {
            var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
            var existing = dictionaries.FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains("Resources/Themes/Palette.", StringComparison.OrdinalIgnoreCase) == true);
            var replacement = new ResourceDictionary { Source = new Uri($"Resources/Themes/Palette.{effective}.xaml", UriKind.Relative) };
            if (existing is null) dictionaries.Insert(0, replacement);
            else dictionaries[dictionaries.IndexOf(existing)] = replacement;
            EffectiveMode = effective;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("Applied {ThemeMode} theme (configured mode: {ConfiguredMode})", effective, Mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply {ThemeMode} theme", effective);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
