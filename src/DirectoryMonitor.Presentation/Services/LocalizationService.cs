using System.Globalization;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace DirectoryMonitor.Presentation.Services;

public static class LocalizationService
{
    private const string LocalizationPath = "Resources/Localization/Strings.";
    private static readonly string AssemblyName = typeof(LocalizationService).Assembly.GetName().Name!;
    private static ResourceDictionary? _activeDictionary;

    public static bool IsVietnamese { get; private set; }

    public static string DefaultLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("vi", StringComparison.OrdinalIgnoreCase) ? "vi" : "en";

    public static void Apply(string language)
    {
        var vietnamese = language.Equals("vi", StringComparison.OrdinalIgnoreCase);
        IsVietnamese = vietnamese;
        if (WpfApplication.Current is null) return;

        var dictionaries = WpfApplication.Current.Resources.MergedDictionaries;
        var current = _activeDictionary ?? dictionaries.FirstOrDefault(IsLocalizationDictionary);
        var resourcePath = $"{LocalizationPath}{(vietnamese ? "vi" : "en")}.xaml";
        if (current?.Source?.OriginalString.EndsWith(resourcePath, StringComparison.OrdinalIgnoreCase) == true)
        {
            _activeDictionary = current;
            return;
        }

        var replacement = new ResourceDictionary { Source = new Uri($"/{AssemblyName};component/{resourcePath}", UriKind.Relative) };
        var index = current is null ? -1 : dictionaries.IndexOf(current);
        if (index >= 0) dictionaries[index] = replacement;
        else dictionaries.Insert(0, replacement);
        _activeDictionary = replacement;
    }

    private static bool IsLocalizationDictionary(ResourceDictionary dictionary)
        => dictionary.Source?.OriginalString.Contains(LocalizationPath, StringComparison.OrdinalIgnoreCase) == true;
}
