namespace DirectoryMonitor.Tests;

using System.Text.RegularExpressions;
using System.Xml.Linq;
using DirectoryMonitor.Presentation.Services;

public sealed class PresentationMarkupTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Directory_grid_uses_compact_accessible_status_icons_and_output_grid_keeps_text_status()
    {
        var markup = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation", "MainWindow.xaml"));

        Assert.Equal(2, Count(markup, "Header=\"{DynamicResource Status}\""));
        Assert.Contains("Header=\"{DynamicResource Status}\" Width=\"80\"", markup);
        Assert.Contains("DirectoryMonitorStatus.Monitoring", markup);
        Assert.Contains("DirectoryMonitorStatus.Disabled", markup);
        Assert.Contains("DirectoryMonitorStatus.Unavailable", markup);
        Assert.Contains("DirectoryMonitorStatus.Reconnecting", markup);
        Assert.Contains("DirectoryMonitorStatus.Error", markup);
        Assert.Contains("Property=\"Text\" Value=\"✔️\"", markup);
        Assert.Contains("Property=\"Text\" Value=\"🚫\"", markup);
        Assert.Contains("Property=\"Text\" Value=\"⚠️\"", markup);
        Assert.Contains("Property=\"Text\" Value=\"❌\"", markup);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource NewAccentBrush}\"", markup);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource PropertiesAccentBrush}\"", markup);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource DeleteAccentBrush}\"", markup);
        Assert.Contains("AutomationProperties.Name", markup);
        Assert.Contains("Text=\"{Binding StatusMessage}\"", markup);
        Assert.Contains("Text=\"{Binding Status}\"", markup);
        Assert.DoesNotContain("DataGridCheckBoxColumn", markup);
    }

    [Fact]
    public void Shared_grid_styles_center_all_cells_vertically_and_status_cells_horizontally()
    {
        var markup = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation", "App.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation", "MainWindow.xaml"));

        Assert.Contains("<Style TargetType=\"DataGridRow\"><Setter Property=\"VerticalContentAlignment\" Value=\"Center\"", markup);
        Assert.Contains("<Style TargetType=\"DataGridCell\"><Setter Property=\"VerticalContentAlignment\" Value=\"Center\"", markup);
        Assert.Contains("<Style TargetType=\"DataGridColumnHeader\"><Setter Property=\"VerticalContentAlignment\" Value=\"Center\"", markup);
        Assert.Contains("<Style TargetType=\"ListBoxItem\"><Setter Property=\"VerticalContentAlignment\" Value=\"Center\"", markup);
        Assert.Contains("Property=\"HorizontalContentAlignment\" Value=\"Stretch\"", markup);
        Assert.Contains("x:Key=\"CenteredDataGridCellStyle\"", markup);
        Assert.Contains("Property=\"HorizontalContentAlignment\" Value=\"Center\"", markup);
        Assert.Contains("x:Key=\"VerticallyCenteredDataGridTextStyle\"", markup);
        Assert.Equal(12, Count(mainWindow, "ElementStyle=\"{StaticResource VerticallyCenteredDataGridTextStyle}\""));
        Assert.Contains("<DataGrid.RowStyle><Style TargetType=\"DataGridRow\" BasedOn=\"{StaticResource {x:Type DataGridRow}}\">", mainWindow);
    }

    [Fact]
    public void Main_window_has_an_explicit_confirmed_exit_while_close_still_minimizes_to_tray()
    {
        var presentationFolder = Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation");
        var markup = File.ReadAllText(Path.Combine(presentationFolder, "MainWindow.xaml"));
        var windowCode = File.ReadAllText(Path.Combine(presentationFolder, "MainWindow.xaml.cs"));
        var appCode = File.ReadAllText(Path.Combine(presentationFolder, "App.xaml.cs"));

        Assert.Contains("Content=\"{DynamicResource ExitApplication}\"", markup);
        Assert.Contains("Click=\"ExitApplication_Click\"", markup);
        Assert.Contains("await app.RequestExitAsync()", windowCode);
        Assert.Contains("public async Task RequestExitAsync()", appCode);
        Assert.Contains("TryFindResource(\"ExitConfirmation\")", appCode);
        Assert.Contains("if (!AllowClose)", windowCode);
    }

    [Fact]
    public void Output_grid_keeps_rows_compact_and_exposes_the_complete_selectable_error()
    {
        var presentationFolder = Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation");
        var markup = File.ReadAllText(Path.Combine(presentationFolder, "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(presentationFolder, "ViewModels", "MainViewModel.cs"));

        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", markup);
        Assert.Contains("ToolTip=\"{Binding LastError}\"", markup);
        Assert.Contains("Text=\"{Binding SelectedDestination.LastError,Mode=OneWay}\"", markup);
        Assert.Contains("IsReadOnly=\"True\" AcceptsReturn=\"True\" TextWrapping=\"Wrap\"", markup);
        Assert.Contains("Command=\"{Binding CopyLastErrorCommand}\"", markup);
        Assert.Contains("private void CopyLastError()", viewModel);
    }

    [Fact]
    public void Event_grid_locks_chronological_order_and_scrolls_to_the_latest_visible_row()
    {
        var presentationFolder = Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation");
        var markup = File.ReadAllText(Path.Combine(presentationFolder, "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(presentationFolder, "MainWindow.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(presentationFolder, "ViewModels", "MainViewModel.cs"));

        Assert.Contains("x:Name=\"EventGrid\"", markup);
        Assert.Contains("CanUserSortColumns=\"False\"", markup);
        Assert.Contains("Events.CollectionChanged += Events_CollectionChanged", codeBehind);
        Assert.Contains("EventGrid.ScrollIntoView(EventGrid.Items[EventGrid.Items.Count - 1])", codeBehind);
        Assert.Contains("Events.AppendRangeAndTrimStart(added, UiBufferLimit)", viewModel);
        Assert.DoesNotContain("InsertRangeAtStart", viewModel);
    }

    [Fact]
    public void Settings_show_the_resolved_read_only_database_path_and_open_folder_command()
    {
        var presentationFolder = Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation");
        var markup = File.ReadAllText(Path.Combine(presentationFolder, "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(presentationFolder, "ViewModels", "MainViewModel.cs"));

        Assert.Contains("Header=\"{DynamicResource SQLiteDatabase}\"", markup);
        Assert.Contains("Text=\"{Binding DatabasePath,Mode=OneWay}\"", markup);
        Assert.Contains("IsReadOnly=\"True\"", markup);
        Assert.Contains("Command=\"{Binding OpenDataFolderCommand}\"", markup);
        Assert.Contains("public string DatabasePath => _store.DatabasePath", viewModel);
        Assert.Contains("private void OpenDataFolder()", viewModel);
    }

    [Fact]
    public void Slnx_is_the_only_solution_and_contains_all_five_projects()
    {
        var slnxPath = Path.Combine(RepositoryRoot, "DirectoryMonitor.slnx");
        var buildScript = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "build-release.ps1"));
        var solution = XDocument.Load(slnxPath);

        Assert.True(File.Exists(slnxPath));
        Assert.False(File.Exists(Path.Combine(RepositoryRoot, "DirectoryMonitor.sln")));
        Assert.Equal(5, solution.Descendants("Project").Count());
        Assert.Contains("DirectoryMonitor.slnx", buildScript);
        Assert.DoesNotContain("\"DirectoryMonitor.sln\"", buildScript);
    }

    [Fact]
    public void Installer_grants_modify_permission_only_to_app_data_folder()
    {
        var installer = File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "DirectoryMonitor.iss"));

        Assert.Contains("Name: \"{app}\\Data\"; Permissions: users-modify", installer);
        Assert.Contains("Name: \"{app}\\Logs\"; Permissions: users-modify", installer);
        Assert.Contains("appsettings.json\"; DestDir: \"{app}\"; Flags: onlyifdoesntexist uninsneveruninstall; Permissions: users-modify", installer);
        Assert.DoesNotContain("{commonappdata}\\Directory Monitor", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Name: \"{app}\"; Permissions: users-modify", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void English_and_vietnamese_resource_dictionaries_have_identical_complete_keys()
    {
        var localizationFolder = Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation", "Resources", "Localization");
        var english = LoadResourceKeys(Path.Combine(localizationFolder, "Strings.en.xaml"));
        var vietnamese = LoadResourceKeys(Path.Combine(localizationFolder, "Strings.vi.xaml"));

        Assert.True(english.Count >= 70, $"Expected a complete localization dictionary but found only {english.Count} keys.");
        Assert.Empty(english.Except(vietnamese));
        Assert.Empty(vietnamese.Except(english));
    }

    [Fact]
    public void App_loads_english_by_default_and_every_window_dynamic_resource_resolves()
    {
        var presentationFolder = Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation");
        var appPath = Path.Combine(presentationFolder, "App.xaml");
        var appMarkup = File.ReadAllText(appPath);
        Assert.Contains("Source=\"Resources/Localization/Strings.en.xaml\"", appMarkup);

        var declared = LoadResourceKeys(appPath);
        declared.UnionWith(LoadResourceKeys(Path.Combine(presentationFolder, "Resources", "Localization", "Strings.en.xaml")));
        declared.UnionWith(LoadResourceKeys(Path.Combine(presentationFolder, "Resources", "Themes", "Palette.Light.xaml")));
        declared.UnionWith(LoadResourceKeys(Path.Combine(presentationFolder, "Resources", "Themes", "Palette.Dark.xaml")));
        var windowFiles = new[]
        {
            Path.Combine(presentationFolder, "MainWindow.xaml"),
            Path.Combine(presentationFolder, "Views", "DirectoryEditorWindow.xaml"),
            Path.Combine(presentationFolder, "Views", "OutputEditorWindow.xaml"),
            Path.Combine(presentationFolder, "Views", "UpdateDialogWindow.xaml"),
            Path.Combine(presentationFolder, "Views", "UpdateProgressWindow.xaml"),
            Path.Combine(presentationFolder, "Views", "ExportDialogWindow.xaml"),
            Path.Combine(presentationFolder, "Views", "ImportDialogWindow.xaml")
        };
        var used = windowFiles.SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\{DynamicResource\s+([^},\s]+)")
            .Select(match => match.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(used.Except(declared));
    }

    [Fact]
    public void Localization_service_swaps_only_merged_resource_dictionaries()
    {
        var service = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation", "Services", "LocalizationService.cs"));

        Assert.Contains("Resources.MergedDictionaries", service);
        Assert.Contains("Strings.", service);
        Assert.DoesNotContain("IReadOnlyDictionary<string, (string En, string Vi)>", service);
    }

    [Fact]
    public void Main_window_exposes_update_badge_and_complete_settings_controls()
    {
        var markup = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation", "MainWindow.xaml"));

        Assert.Contains("IsUpdateAvailable", markup);
        Assert.Contains("ShowUpdateCommand", markup);
        Assert.Contains("UpdateManifestUrl", markup);
        Assert.Contains("SelectedUpdateSchedule", markup);
        Assert.Contains("CheckForUpdatesCommand", markup);
        Assert.Contains("ClearSkippedUpdateCommand", markup);
    }

    [Fact]
    public void Appsettings_serilog_theme_grid_actions_and_transfer_ui_are_wired()
    {
        var presentationFolder = Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation");
        var appsettings = File.ReadAllText(Path.Combine(presentationFolder, "appsettings.json"));
        var app = File.ReadAllText(Path.Combine(presentationFolder, "App.xaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(presentationFolder, "MainWindow.xaml"));
        var mainViewModel = File.ReadAllText(Path.Combine(presentationFolder, "ViewModels", "MainViewModel.cs"));

        Assert.Contains("Data Source=Data\\\\directory-monitor.db", appsettings);
        Assert.Contains("Logs\\\\directory-monitor-.log", appsettings);
        Assert.Contains("\"rollingInterval\": \"Day\"", appsettings);
        Assert.Contains("\"retainedFileCountLimit\": 30", appsettings);
        Assert.Contains("DispatcherUnhandledException", app);
        Assert.Contains("TaskScheduler.UnobservedTaskException", app);
        Assert.Contains("AddHostedService<SerilogReloadService>", app);
        Assert.Contains("SelectedValue=\"{Binding SelectedThemeMode}\"", mainWindow);
        Assert.Contains("AppThemeMode.System", mainWindow);
        Assert.Contains("Command=\"{Binding DuplicateDirectoryCommand}\"", mainWindow);
        Assert.Contains("Command=\"{Binding OpenSelectedDirectoryCommand}\"", mainWindow);
        Assert.Contains("DirectoryGrid_MouseDoubleClick", mainWindow);
        Assert.Contains("OutputGrid_MouseDoubleClick", mainWindow);
        Assert.Contains("Command=\"{Binding DuplicateOutputCommand}\"", mainWindow);
        Assert.Contains("Header=\"{DynamicResource Event}\"", mainWindow);
        Assert.DoesNotContain("Header=\"{DynamicResource User}\"", mainWindow);
        Assert.DoesNotContain("Header=\"{DynamicResource Machine}\"", mainWindow);
        Assert.Contains("ClearAllEventsCommand", mainWindow);
        Assert.DoesNotContain("ClearActiveLogsAsync", mainViewModel);
        Assert.Contains("ImportDataCommand", mainWindow);
        Assert.Contains("ExportDataCommand", mainWindow);
    }

    [Fact]
    public void Light_and_dark_palettes_define_the_same_complete_brush_set()
    {
        var themeFolder = Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation", "Resources", "Themes");
        var light = LoadResourceKeys(Path.Combine(themeFolder, "Palette.Light.xaml"));
        var dark = LoadResourceKeys(Path.Combine(themeFolder, "Palette.Dark.xaml"));
        Assert.True(light.Count >= 30);
        Assert.Empty(light.Except(dark));
        Assert.Empty(dark.Except(light));
        Assert.Contains("SelectionTextBrush", light);
        Assert.Contains("DisabledTextBrush", light);
        Assert.Contains("HyperlinkBrush", light);
    }

    [Fact]
    public void Output_editor_exposes_database_examples_authentication_help_and_safe_use_sample_action()
    {
        var presentationFolder = Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Presentation");
        var markup = File.ReadAllText(Path.Combine(presentationFolder, "Views", "OutputEditorWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(presentationFolder, "Views", "OutputEditorWindow.xaml.cs"));

        Assert.Contains("DatabaseConnectionExample", markup);
        Assert.Contains("SqlServerSampleAuthentication.WindowsAuthentication", markup);
        Assert.Contains("SqlServerSampleAuthentication.SqlLogin", markup);
        Assert.Contains("PostgreSqlConnectionHelp", markup);
        Assert.Contains("SqlServerWindowsConnectionHelp", markup);
        Assert.Contains("SqlServerLoginConnectionHelp", markup);
        Assert.Contains("UseSample_Click", markup);
        Assert.Contains("ReplaceConnectionStringPrompt", codeBehind);
        Assert.Contains("MessageBoxButton.YesNo", codeBehind);
    }

    [Fact]
    public void Installer_preserves_existing_database_and_supports_portable_import_and_original_user_relaunch()
    {
        var installer = File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "DirectoryMonitor.iss"));

        Assert.Contains("Software\\Directory Monitor", installer);
        Assert.Contains("{param:IMPORTDB|}", installer);
        Assert.Contains("not FileExists(TargetDatabase)", installer);
        Assert.Contains("runasoriginaluser", installer);
        Assert.Contains("{param:RELAUNCH|0}", installer);
    }

    [Fact]
    public void Version_is_centralized_and_manifest_generator_uses_real_installer_metadata()
    {
        var buildProperties = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var generator = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "New-UpdateManifest.ps1"));

        Assert.Contains("<Version>1.1.8</Version>", buildProperties);
        Assert.Contains("Get-FileHash", generator);
        Assert.Contains("$installer.Length", generator);
        Assert.Contains("ConvertTo-Json", generator);
        Assert.Contains("Directory.Build.props", generator);
        Assert.Contains("does not match centralized version", generator);
    }

    [Fact]
    public void Localization_service_switches_live_resources_between_vietnamese_and_english()
    {
        Exception? failure = null;
        string? vietnamese = null;
        string? english = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new System.Windows.Application();
                application.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                {
                    Source = new Uri("/Directory Monitor;component/Resources/Localization/Strings.en.xaml", UriKind.Relative)
                });
                LocalizationService.Apply("vi");
                vietnamese = application.FindResource("TabMonitoring") as string;
                LocalizationService.Apply("en");
                english = application.FindResource("TabMonitoring") as string;
                application.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)), "Localization test thread did not complete.");

        Assert.Null(failure);
        Assert.Equal("Theo dõi", vietnamese);
        Assert.Equal("Monitoring", english);
    }

    private static int Count(string value, string fragment)
        => (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;

    private static HashSet<string> LoadResourceKeys(string path)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Descendants()
            .Select(element => element.Attribute(xaml + "Key")?.Value)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DirectoryMonitor.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Directory Monitor repository root.");
    }
}
