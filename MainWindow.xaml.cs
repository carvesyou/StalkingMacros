using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using D2MacroNative.Models;
using D2MacroNative.Services;
using D2MacroNative.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NativeBinding = D2MacroNative.Models.InputBinding;
using WpfButton = System.Windows.Controls.Button;
using WpfRadioButton = System.Windows.Controls.RadioButton;

namespace D2MacroNative;

public partial class MainWindow : Window
{
    private const string DimUrl = "https://app.destinyitemmanager.com/";
    private const string ViGEmInstallerUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";
    private const string ViGEmInstallerSha256 = "89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A";
    private const string BrowserPolishScript = """
        (() => {
            const install = () => {
                if (!document.documentElement || document.getElementById('stalking-browser-polish')) return;
                const style = document.createElement('style');
                style.id = 'stalking-browser-polish';
                style.textContent = `
                    html {
                        background: #080808 !important;
                        scrollbar-color: #56635a #0a0a0a !important;
                        scrollbar-width: thin !important;
                    }
                    ::-webkit-scrollbar {
                        width: 9px !important;
                        height: 9px !important;
                    }
                    ::-webkit-scrollbar-track,
                    ::-webkit-scrollbar-corner {
                        background: #0a0a0a !important;
                    }
                    ::-webkit-scrollbar-thumb {
                        min-height: 34px !important;
                        background: #56635a !important;
                        border: 2px solid #0a0a0a !important;
                        border-radius: 999px !important;
                    }
                    ::-webkit-scrollbar-thumb:hover {
                        background: #9fd8b3 !important;
                    }
                `;
                (document.head || document.documentElement).appendChild(style);
            };
            install();
            document.addEventListener('DOMContentLoaded', install, { once: true });
        })();
        """;
    private readonly SettingsStore _settingsStore = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private readonly NativeMacroEngine _engine;
    private readonly AppUpdateService _updateService = new();
    private AppSettings _settings;
    private TrayIconService? _tray;
    private bool _allowExit;
    private bool _suppressMacroEvents;
    private bool _sitesBrowserInitializing;
    private WebView2? _sitesBrowser;
    private string _currentSiteUrl = DimUrl;
    private Action<NativeBinding>? _captureApply;

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        Macros = _settings.Macros;
        MacroConfig Macro(MacroKind kind) => Macros.First(macro => macro.Kind == kind);
        HunterSkates =
        [
            Macro(MacroKind.ShatterSkate),
            Macro(MacroKind.GroundSkate),
            Macro(MacroKind.HunterStrandSkate),
            Macro(MacroKind.AcrobaticsSkate)
        ];
        WarlockSkates =
        [
            Macro(MacroKind.WellSkate),
            Macro(MacroKind.GroundWellSkate),
            Macro(MacroKind.StrandSkate)
        ];
        TitanSkates = [Macro(MacroKind.BubbleSkate)];
        ControllerMacros = new ObservableCollection<MacroConfig>(Macros.Where(macro =>
            macro.Kind is not MacroKind.FortniteEditSpam and not MacroKind.FortniteDragEdit));
        InventoryMacros = new ObservableCollection<MacroConfig>(
            Macros.Where(macro => macro.Kind == MacroKind.LoadoutSwap)
                .Concat([Macro(MacroKind.LoadoutSpam)]));
        MiscMacros = [Macro(MacroKind.RocketGrapple), Macro(MacroKind.SparrowSlipstream), Macro(MacroKind.WishWall)];
        FortniteMacros =
        [
            Macro(MacroKind.FortniteEditSpam),
            Macro(MacroKind.FortniteDragEdit)
        ];
        GameBindItems = [];
        FortniteBindItems = [];
        WishOptions =
        [
            new(1, "Ethereal Key"),
            new(2, "Glittering Key Chest"),
            new(3, "Numbers of Power Emblem"),
            new(4, "Shuro Chi Encounter"),
            new(5, "Morgeth Encounter"),
            new(6, "Vault Encounter"),
            new(7, "Riven Encounter"),
            new(8, "Hope for the Future Song"),
            new(9, "Failsafe Dialogue"),
            new(10, "Drifter Dialogue"),
            new(11, "Precision-Kill Party Effect"),
            new(12, "Player Head Effect"),
            new(13, "Petra's Run"),
            new(14, "Corrupted Eggs")
        ];
        FpsOptions = new[] { 30, 60, 75, 90, 120, 144, 165, 180, 240, 360, 500 }
            .Select(fps => new FpsOption(fps))
            .ToArray();
        LoadoutOptions = Enumerable.Range(1, 20).Select(number => new LoadoutOption(number)).ToArray();
        ActivationModes =
        [
            new(MacroActivationMode.OneTime, "One Time"),
            new(MacroActivationMode.Hold, "Hold"),
            new(MacroActivationMode.Toggle, "Toggle")
        ];
        BuildGameBindItems();
        BuildFortniteBindItems();

        _engine = new NativeMacroEngine(_settings);
        _engine.MacroTriggered += Engine_MacroTriggered;
        _engine.Notice += Engine_Notice;

        InitializeComponent();
        DataContext = this;

        foreach (var macro in Macros) AttachMacro(macro);
        RefreshLoadoutCardControls();
        Wall.PropertyChanged += (_, _) => ScheduleSave();
        Timings.PropertyChanged += (_, _) => ScheduleSave();
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings(false);
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        RefreshSetupTexts();
    }

    public ObservableCollection<MacroConfig> Macros { get; }
    public IReadOnlyList<MacroConfig> HunterSkates { get; }
    public IReadOnlyList<MacroConfig> WarlockSkates { get; }
    public IReadOnlyList<MacroConfig> TitanSkates { get; }
    public ObservableCollection<MacroConfig> ControllerMacros { get; }
    public ObservableCollection<MacroConfig> InventoryMacros { get; }
    public IReadOnlyList<MacroConfig> MiscMacros { get; }
    public IReadOnlyList<MacroConfig> FortniteMacros { get; }
    public ObservableCollection<GameBindItem> GameBindItems { get; }
    public ObservableCollection<GameBindItem> FortniteBindItems { get; }
    public IReadOnlyList<WishOption> WishOptions { get; }
    public IReadOnlyList<FpsOption> FpsOptions { get; }
    public IReadOnlyList<LoadoutOption> LoadoutOptions { get; }
    public IReadOnlyList<ActivationModeOption> ActivationModes { get; }
    public WallSettings Wall => _settings.Wall;
    public TimingSettings Timings => _settings.Timings;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyNativeWindowStyle(new WindowInteropHelper(this).Handle);
        WarnAboutOtherMacroProcesses();
        try
        {
            _engine.Start();
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
        }

        _tray = new TrayIconService(
            () => Dispatcher.Invoke(ShowWindow),
            () => Dispatcher.Invoke(Panic),
            () => Dispatcher.Invoke(ExitApplication));

        ShowToast("Input engine ready");
        if (!_settings.SetupComplete) ShowFirstRunSetup();
        await CheckForUpdatesAsync(false);
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(true);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            if (manual) ShowToast("Checking for updates...");
            var update = await _updateService.CheckAsync();
            if (update is null)
            {
                if (manual) ShowToast("You already have the latest version");
                return;
            }

            var browserWasVisible = _sitesBrowser?.Visibility == Visibility.Visible;
            if (_sitesBrowser is not null) _sitesBrowser.Visibility = Visibility.Collapsed;
            var choice = System.Windows.MessageBox.Show(this,
                $"Version {update.Version.ToString(3)} is available.\n\nDownload, verify and install it now? Your binds and settings will be preserved.",
                "/stalking macro update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (browserWasVisible && _sitesBrowser is not null) _sitesBrowser.Visibility = Visibility.Visible;
            if (choice != MessageBoxResult.Yes) return;

            var progress = new Progress<int>(percent => ShowToast($"Downloading update... {percent}%"));
            var downloaded = await _updateService.DownloadAsync(update, progress);
            SaveSettings(false);
            Panic();
            AppUpdateService.LaunchInstaller(downloaded);
            ExitApplication();
        }
        catch (Exception ex)
        {
            if (manual)
                System.Windows.MessageBox.Show(this, ex.Message, "Update check failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void WarnAboutOtherMacroProcesses()
    {
        var currentId = Environment.ProcessId;
        var conflicts = Process.GetProcesses()
            .Where(process => process.Id != currentId
                && (process.ProcessName.Contains("stalking-macro", StringComparison.OrdinalIgnoreCase)
                    || process.ProcessName.Equals("D2MacroSystem", StringComparison.OrdinalIgnoreCase)))
            .Select(process => process.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (conflicts.Length == 0) return;

        System.Windows.MessageBox.Show(this,
            $"Another macro tool is already running: {string.Join(", ", conflicts)}.\n\nClose it and restart this app. Multiple macro tools can create competing virtual controllers that Destiny ignores.",
            "Controller Conflict Detected",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async Task CheckViGEmBusOnFirstRunAsync()
    {
        if (IsViGEmBusInstalled())
        {
            if (!_settings.ViGEmInstallPrompted)
            {
                _settings.ViGEmInstallPrompted = true;
                SaveSettings(false);
            }
            return;
        }

        if (_settings.ViGEmInstallPrompted) return;
        _settings.ViGEmInstallPrompted = true;
        SaveSettings(false);

        var choice = System.Windows.MessageBox.Show(this,
            "Controller macros require the ViGEmBus 1.22.0 system driver. ViGEmBus is retired and no longer receives updates.\n\nInstall the official driver now for controller support?",
            "Install Controller Support",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes) return;

        var installerDirectory = Path.Combine(Path.GetTempPath(), "stalking-macro-vigembus");
        var installerPath = Path.Combine(installerDirectory, "ViGEmBus_1.22.0.exe");
        try
        {
            ShowToast("Downloading verified ViGEmBus installer...");
            Directory.CreateDirectory(installerDirectory);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("stalking-macro/7.9.1");
            var installerBytes = await client.GetByteArrayAsync(ViGEmInstallerUrl);
            var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installerBytes));
            if (!actualHash.Equals(ViGEmInstallerSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The ViGEmBus installer checksum did not match the official release.");
            await File.WriteAllBytesAsync(installerPath, installerBytes);

            var installer = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/exenoui /qn /norestart",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Windows could not start the ViGEmBus installer.");
            using (installer)
                await installer.WaitForExitAsync();

            for (var attempt = 0; attempt < 20 && !IsViGEmBusInstalled(); attempt++)
                await Task.Delay(250);

            if (!IsViGEmBusInstalled())
                throw new InvalidOperationException("ViGEmBus did not appear after installation. Restart Windows and try again.");

            System.Windows.MessageBox.Show(this,
                "ViGEmBus installed successfully. Pure controller macros are ready.",
                "Controller Support Ready",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _settings.ViGEmInstallPrompted = false;
            SaveSettings(false);
            System.Windows.MessageBox.Show(this,
                $"Controller support was not installed.\n\n{ex.Message}",
                "ViGEmBus Installation Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
        }
    }

    private static bool IsViGEmBusInstalled()
    {
        try
        {
            using var query = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "query ViGEmBus",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (query is null || !query.WaitForExit(3000)) return false;
            return query.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void BuildGameBindItems()
    {
        GameBindItems.Clear();
        var binds = _settings.GameBindings;
        GameBindItems.Add(new("AirMove", "Air Move / Dive", "Used by Shatterdive, Ensnaring Slam, Icarus Dash, and Weavewalk.", "A", "#A8E6C0", binds.AirMove,
            value => { binds.AirMove = value; RefreshSetupTexts(); ScheduleSave(); }));
        GameBindItems.Add(new("Super", "Super", "Used by regular and Ground Wellskate plus Warlock Strand Skate.", "S", "#C0F0D2", binds.Super,
            value => { binds.Super = value; RefreshSetupTexts(); ScheduleSave(); }));
        GameBindItems.Add(new("Rift", "Warlock Rift", "Class ability input used by Warlock Strand Skate.", "R", "#9FE2BA", binds.Rift,
            value => { binds.Rift = value; ScheduleSave(); }));
        GameBindItems.Add(new("Dodge", "Acrobatics Dodge", "Hunter class ability input used by Acrobatics Skate.", "D", "#95DDB1", binds.Dodge,
            value => { binds.Dodge = value; ScheduleSave(); }));
        GameBindItems.Add(new("Grapple", "Grapple / Grenade", "Your Strand Grapple input used by Rocket Grapple.", "G", "#A8E6C0", binds.Grapple,
            value => { binds.Grapple = value; ScheduleSave(); }));
        GameBindItems.Add(new("WellExitWeapon", "Exit Weapon", "Every skate finishes with this weapon bind; defaults to keyboard 2.", "2", "#C0F0D2", binds.WellExitWeapon,
            value => { binds.WellExitWeapon = value; ScheduleSave(); }));
        GameBindItems.Add(new("Inventory", "Character / Inventory", "Opens the Character screen before a selected loadout is equipped.", "I", "#9FE2BA", binds.Inventory,
            value => { binds.Inventory = value; ScheduleSave(); }));
        GameBindItems.Add(new("OpenLoadouts", "Open Loadouts", "Opens the loadout panel after the Character screen appears.", "←", "#B7EAC9", binds.OpenLoadouts,
            value => { binds.OpenLoadouts = value; ScheduleSave(); }));
        GameBindItems.Add(new("CloseMenu", "Close Menus", "Pressed twice after the selected loadout is equipped.", "X", "#C8F3D7", binds.CloseMenu,
            value => { binds.CloseMenu = value; ScheduleSave(); }));
        GameBindItems.Add(new("Ghost", "Ghost", "Opens Ghost before the Slipstream Assist summons your Sparrow.", "G", "#B7EAC9", binds.Ghost,
            value => { binds.Ghost = value; ScheduleSave(); }));
        GameBindItems.Add(new("SummonVehicle", "Summon Vehicle", "Hold input used to summon and mount your Sparrow.", "V", "#A8E6C0", binds.SummonVehicle,
            value => { binds.SummonVehicle = value; ScheduleSave(); }));
        GameBindItems.Add(new("SparrowBoost", "Sparrow Boost", "Initial boost used to launch from the ledge.", "B", "#C0F0D2", binds.SparrowBoost,
            value => { binds.SparrowBoost = value; ScheduleSave(); }));
        GameBindItems.Add(new("SparrowDestabilizer", "Sparrow Destabilizer", "Air-roll modifier; unavailable inside Monument of Triumph SRL races.", "D", "#9FE2BA", binds.SparrowDestabilizer,
            value => { binds.SparrowDestabilizer = value; ScheduleSave(); }));
        GameBindItems.Add(new("SparrowBack", "Sparrow Back / Pitch", "Back input held to build and maintain the slipstream pitch.", "S", "#95DDB1", binds.SparrowBack,
            value => { binds.SparrowBack = value; ScheduleSave(); }));
        GameBindItems.Add(new("SparrowLeft", "Sparrow Steer Left", "Left steering input used only to establish the launch angle.", "A", "#C8F3D7", binds.SparrowLeft,
            value => { binds.SparrowLeft = value; ScheduleSave(); }));
        GameBindItems.Add(new("SparrowDodgeLeft", "Sparrow Dodge Left", "Dedicated Vehicle Dodge Left bind used once per post-patch Slipstream rotation.", "DL", "#A8E6C0", binds.SparrowDodgeLeft,
            value => { binds.SparrowDodgeLeft = value; ScheduleSave(); }));
    }

    private void BuildFortniteBindItems()
    {
        FortniteBindItems.Clear();
        var binds = _settings.FortniteBindings;
        FortniteBindItems.Add(new("FortniteEdit", "Edit", "Your Fortnite Edit building bind.", "E", "#A8E6C0", binds.Edit,
            value => { binds.Edit = value; ScheduleSave(); }));
        FortniteBindItems.Add(new("FortniteSelect", "Select", "Your Fortnite Select Building Edit bind; defaults to left mouse.", "S", "#C0F0D2", binds.Select,
            value => { binds.Select = value; ScheduleSave(); }));
    }

    private void Macro_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressMacroEvents || sender is not MacroConfig macro) return;
        if (e.PropertyName is nameof(MacroConfig.SelectedLoadout)
            or nameof(MacroConfig.SelectedLoadoutSecondary)
            or nameof(MacroConfig.SelectedLoadoutTertiary)
            or nameof(MacroConfig.SelectedLoadoutQuaternary))
        {
            if (macro.IsLoadoutSwap && macro.SelectedLoadout == macro.SelectedLoadoutSecondary)
            {
                _suppressMacroEvents = true;
                if (e.PropertyName == nameof(MacroConfig.SelectedLoadout))
                    macro.SelectedLoadoutSecondary = macro.SelectedLoadout == 20 ? 19 : macro.SelectedLoadout + 1;
                else
                    macro.SelectedLoadout = macro.SelectedLoadoutSecondary == 1 ? 2 : macro.SelectedLoadoutSecondary - 1;
                _suppressMacroEvents = false;
                ShowToast("Loadout A and B must be different, so the other slot was adjusted.");
            }
            else if (macro.IsDesync)
            {
                var selections = new[]
                {
                    macro.SelectedLoadout,
                    macro.SelectedLoadoutSecondary,
                    macro.SelectedLoadoutTertiary,
                    macro.SelectedLoadoutQuaternary
                };
                if (selections.Distinct().Count() != selections.Length)
                {
                    var changedIndex = e.PropertyName switch
                    {
                        nameof(MacroConfig.SelectedLoadout) => 0,
                        nameof(MacroConfig.SelectedLoadoutSecondary) => 1,
                        nameof(MacroConfig.SelectedLoadoutTertiary) => 2,
                        _ => 3
                    };
                    var occupied = selections.Where((_, index) => index != changedIndex).ToHashSet();
                    var replacement = Enumerable.Range(1, 20).First(slot => !occupied.Contains(slot));
                    _suppressMacroEvents = true;
                    switch (changedIndex)
                    {
                        case 0: macro.SelectedLoadout = replacement; break;
                        case 1: macro.SelectedLoadoutSecondary = replacement; break;
                        case 2: macro.SelectedLoadoutTertiary = replacement; break;
                        default: macro.SelectedLoadoutQuaternary = replacement; break;
                    }
                    _suppressMacroEvents = false;
                    ShowToast("Desync loadouts must be unique, so that slot was adjusted.");
                }
            }
            ScheduleSave();
            return;
        }
        if (e.PropertyName == nameof(MacroConfig.ActivationMode))
        {
            _engine.StopMacro(macro);
            ScheduleSave();
            return;
        }
        if (e.PropertyName == nameof(MacroConfig.IsEnabled))
        {
            var collision = macro.IsEnabled
                ? Macros.FirstOrDefault(other => other != macro && other.IsEnabled
                    && other.Activation.SameAs(macro.Activation))
                : null;
            if (collision is not null)
            {
                _suppressMacroEvents = true;
                macro.IsEnabled = false;
                _suppressMacroEvents = false;
                ShowToast($"That keyboard bind is already used by {collision.Name}.");
            }
            else if (!macro.IsEnabled)
                _engine.StopMacro(macro);
            ScheduleSave();
            return;
        }

        if (e.PropertyName != nameof(MacroConfig.ControllerIsEnabled)) return;

        var controllerCollision = macro.ControllerIsEnabled && macro.ControllerActivation is not null
            ? Macros.FirstOrDefault(other => other != macro && other.ControllerIsEnabled
                && other.ControllerActivation?.SameAs(macro.ControllerActivation) == true)
            : null;
        if (controllerCollision is not null)
        {
            _suppressMacroEvents = true;
            macro.ControllerIsEnabled = false;
            _suppressMacroEvents = false;
            ShowToast($"That controller bind is already used by {controllerCollision.Name}.");
        }
        else if (!macro.ControllerIsEnabled)
            _engine.StopMacro(macro);
        ScheduleSave();
    }

    private void Engine_MacroTriggered(MacroConfig macro, string result)
    {
        Dispatcher.BeginInvoke(() =>
        {
            macro.Runs++;
            macro.LastResult = result;
        });
    }

    private void Engine_Notice(string message) => Dispatcher.BeginInvoke(() => ShowToast(message));

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        DestinyPage.Visibility = sender == DashboardNav ? Visibility.Visible : Visibility.Collapsed;
        SitesPage.Visibility = sender == SitesNav ? Visibility.Visible : Visibility.Collapsed;
        FortnitePage.Visibility = sender == FortniteNav ? Visibility.Visible : Visibility.Collapsed;
        if (sender == SitesNav) await EnsureSitesBrowserAsync();
    }

    private void DestinyCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfRadioButton { Tag: string category }) ShowDestinyCategory(category);
    }

    private void ShowDestinyCategory(string category)
    {
        var showBinds = category == "Binds";
        var showController = category == "Controller";
        DashboardPage.Visibility = showBinds || showController ? Visibility.Collapsed : Visibility.Visible;
        BindsPage.Visibility = showBinds ? Visibility.Visible : Visibility.Collapsed;
        ControllerSkatesPage.Visibility = showController ? Visibility.Visible : Visibility.Collapsed;
        SkatesSection.Visibility = category == "Keyboard" ? Visibility.Visible : Visibility.Collapsed;
        InventorySection.Visibility = category == "Inventory" ? Visibility.Visible : Visibility.Collapsed;
        MiscSection.Visibility = category == "Misc" ? Visibility.Visible : Visibility.Collapsed;
        DestinyPageTitle.Text = category switch
        {
            "Inventory" => "Inventory Macros",
            "Misc" => "Misc Macros",
            _ => "Keyboard Macros"
        };
        DashboardPage.ScrollToTop();
        BindsPage.ScrollToTop();
        ControllerSkatesPage.ScrollToTop();
        Dispatcher.BeginInvoke(() =>
        {
            DashboardPage.ScrollToTop();
            BindsPage.ScrollToTop();
            ControllerSkatesPage.ScrollToTop();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SkateClass_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfRadioButton { Tag: string className }) return;
        HunterSkatesPage.Visibility = className == "Hunter" ? Visibility.Visible : Visibility.Collapsed;
        WarlockSkatesPage.Visibility = className == "Warlock" ? Visibility.Visible : Visibility.Collapsed;
        TitanSkatesPage.Visibility = className == "Titan" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ControllerSkateClass_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfRadioButton { Tag: string className }) return;
        ControllerHunterPage.Visibility = className == "Hunter" ? Visibility.Visible : Visibility.Collapsed;
        ControllerWarlockPage.Visibility = className == "Warlock" ? Visibility.Visible : Visibility.Collapsed;
        ControllerTitanPage.Visibility = className == "Titan" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FortniteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfRadioButton { Tag: string category }) ShowFortniteCategory(category);
    }

    private void ShowFortniteCategory(string category)
    {
        var showBinds = category == "Binds";
        FortniteMacrosPage.Visibility = showBinds ? Visibility.Collapsed : Visibility.Visible;
        FortniteBindsPage.Visibility = showBinds ? Visibility.Visible : Visibility.Collapsed;
        FortniteMacrosPage.ScrollToTop();
        FortniteBindsPage.ScrollToTop();
    }

    private async Task EnsureSitesBrowserAsync()
    {
        if (_sitesBrowser?.CoreWebView2 is not null || _sitesBrowserInitializing) return;
        _sitesBrowserInitializing = true;
        SitesStatusPanel.Visibility = Visibility.Visible;
        SitesStatusTitle.Text = "Loading Destiny sites…";
        SitesStatusDetail.Text = "The embedded browser is starting.";
        SitesFallbackButton.Visibility = Visibility.Collapsed;

        try
        {
            if (_sitesBrowser is null)
            {
                _sitesBrowser = new WebView2
                {
                    CreationProperties = new CoreWebView2CreationProperties()
                };
                SitesBrowserHost.Children.Add(_sitesBrowser);
            }

            var userDataFolder = Path.Combine(_settingsStore.SettingsDirectory, "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await _sitesBrowser.EnsureCoreWebView2Async(environment);

            var core = _sitesBrowser.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 did not finish initializing.");
            var browserSettings = core.Settings;
            browserSettings.IsStatusBarEnabled = false;
            browserSettings.IsZoomControlEnabled = true;
            browserSettings.AreDefaultContextMenusEnabled = true;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BrowserPolishScript);
            core.NewWindowRequested += SitesBrowser_NewWindowRequested;
            _sitesBrowser.NavigationCompleted += SitesBrowser_NavigationCompleted;
            core.Navigate(_currentSiteUrl);
        }
        catch (Exception ex)
        {
            SitesStatusTitle.Text = "Embedded browser unavailable";
            SitesStatusDetail.Text = $"{ex.Message}\nYou can still open the selected site in your default browser.";
            SitesFallbackButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _sitesBrowserInitializing = false;
        }
    }

    private async void SiteTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfRadioButton { Tag: string url }) return;
        _currentSiteUrl = url;
        await EnsureSitesBrowserAsync();
        _sitesBrowser?.CoreWebView2?.Navigate(_currentSiteUrl);
    }

    private async void SitesBrowser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            try
            {
                if (_sitesBrowser?.CoreWebView2 is { } core)
                    await core.ExecuteScriptAsync(BrowserPolishScript);
            }
            catch
            {
                // Cosmetic browser styling must never block a successfully loaded site.
            }

            SitesStatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SitesStatusTitle.Text = "Site could not load";
        SitesStatusDetail.Text = $"WebView2 returned {e.WebErrorStatus}. Check your connection or open the page externally.";
        SitesFallbackButton.Visibility = Visibility.Visible;
        SitesStatusPanel.Visibility = Visibility.Visible;
    }

    private void SitesBrowser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(e.Uri))
            Dispatcher.BeginInvoke(() => _sitesBrowser?.CoreWebView2?.Navigate(e.Uri));
    }

    private void SitesBack_Click(object sender, RoutedEventArgs e)
    {
        if (_sitesBrowser?.CanGoBack == true) _sitesBrowser.GoBack();
    }

    private void SitesForward_Click(object sender, RoutedEventArgs e)
    {
        if (_sitesBrowser?.CanGoForward == true) _sitesBrowser.GoForward();
    }

    private void SitesReload_Click(object sender, RoutedEventArgs e) => _sitesBrowser?.CoreWebView2?.Reload();

    private void SitesExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = _sitesBrowser?.Source?.AbsoluteUri ?? _currentSiteUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open browser: {ex.Message}");
        }
    }

    private void MacroToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: MacroConfig macro })
            ShowToast(macro.IsEnabled ? $"{macro.Name} enabled" : $"{macro.Name} disabled");
    }

    private void ControllerMacroToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: MacroConfig macro })
            ShowToast(macro.ControllerIsEnabled ? $"{macro.Name} controller enabled" : $"{macro.Name} controller disabled");
    }

    private void AddLoadoutSwapper_Click(object sender, RoutedEventArgs e)
    {
        var currentSwappers = InventoryMacros.Where(macro => macro.Kind == MacroKind.LoadoutSwap).ToArray();
        if (currentSwappers.Length >= 2)
        {
            ShowToast("Two Loadout Swappers are already configured.");
            return;
        }

        var activation = FindAvailableMacroBinding();
        var macro = new MacroConfig
        {
            Kind = MacroKind.LoadoutSwap,
            Name = "Loadout Swapper 2",
            Category = "LOADOUT  ·  ALTERNATING",
            Accent = "#B7F0CD",
            Description = "Alternates Loadout A and Loadout B with every activation press.",
            Activation = activation,
            ActivationMode = MacroActivationMode.OneTime,
            SelectedLoadout = currentSwappers[0].SelectedLoadout == 1 ? 2 : 1,
            SelectedLoadoutSecondary = currentSwappers[0].SelectedLoadoutSecondary == 3 ? 4 : 3,
            IsEnabled = false
        };

        Macros.Add(macro);
        ControllerMacros.Add(macro);
        var desyncIndex = InventoryMacros.ToList().FindIndex(item => item.Kind == MacroKind.LoadoutSpam);
        InventoryMacros.Insert(desyncIndex < 0 ? InventoryMacros.Count : desyncIndex, macro);
        AttachMacro(macro);
        RefreshLoadoutCardControls();
        ScheduleSave();
        ShowToast($"Loadout Swapper 2 added on {activation.DisplayName}. Rebind it anytime.");
    }

    private void RemoveLoadoutSwapper_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { DataContext: MacroConfig macro } || !macro.CanRemoveLoadoutSwap) return;
        _engine.StopMacro(macro);
        macro.PropertyChanged -= Macro_PropertyChanged;
        InventoryMacros.Remove(macro);
        ControllerMacros.Remove(macro);
        Macros.Remove(macro);
        RefreshLoadoutCardControls();
        ScheduleSave();
        ShowToast("Second Loadout Swapper removed.");
    }

    private NativeBinding FindAvailableMacroBinding()
    {
        Key[] candidates =
        [
            Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12,
            Key.D0, Key.D9, Key.D8, Key.D7, Key.D6
        ];
        return candidates.Select(NativeBinding.Keyboard)
            .FirstOrDefault(candidate => Macros.All(macro => !macro.Activation.SameAs(candidate)))
            ?? NativeBinding.Keyboard(Key.F6);
    }

    private void AttachMacro(MacroConfig macro) => macro.PropertyChanged += Macro_PropertyChanged;

    private void RefreshLoadoutCardControls()
    {
        var swappers = InventoryMacros.Where(macro => macro.Kind == MacroKind.LoadoutSwap).ToArray();
        for (var index = 0; index < swappers.Length; index++)
            swappers[index].SetLoadoutCardControls(index == 0 && swappers.Length < 2, index > 0);
    }

    private void CaptureMacro_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { DataContext: MacroConfig macro }) return;
        BeginCapture($"Bind {macro.Name}", binding =>
        {
            var collision = Macros.FirstOrDefault(other => other != macro && other.IsEnabled && macro.IsEnabled && other.Activation.SameAs(binding));
            if (collision is not null)
            {
                ShowToast($"{binding.DisplayName} is already active on {collision.Name}.");
                return;
            }
            macro.SetActivation(binding);
            ShowToast($"{macro.Name}: {binding.DisplayName}");
            ScheduleSave();
        }, includePrimaryMouse: macro.Kind is MacroKind.FortniteEditSpam or MacroKind.FortniteDragEdit);
    }

    private void CaptureControllerBind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { DataContext: MacroConfig macro }) return;
        BeginCapture($"Controller bind: {macro.Name}", binding =>
        {
            if (binding.Device != D2MacroNative.Models.InputDevice.Controller)
            {
                ShowToast("Controller tab only accepts controller buttons.");
                return;
            }

            var collision = Macros.FirstOrDefault(other => other != macro && other.ControllerIsEnabled
                && other.ControllerActivation?.SameAs(binding) == true);
            if (collision is not null)
            {
                ShowToast($"{binding.DisplayName} is already active on {collision.Name}.");
                return;
            }

            macro.SetControllerActivation(binding);
            macro.ControllerIsEnabled = true;
            ShowToast($"{macro.Name}: {binding.DisplayName} — controller enabled");
            ScheduleSave();
        }, includeController: true);
    }

    private void ClearControllerBind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { DataContext: MacroConfig macro }) return;
        macro.ControllerIsEnabled = false;
        macro.ControllerActivation = null;
        ScheduleSave();
        ShowToast($"{macro.Name} controller bind cleared.");
    }

    private async void TestMacroOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { DataContext: MacroConfig macro }) return;
        ShowToast($"{macro.Name} runs in 2 seconds — focus Destiny 2.");
        await Task.Delay(2000);
        await _engine.PreviewControllerAsync(macro);
    }

    private void CaptureGameBind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { DataContext: GameBindItem item }) return;
        BeginCapture($"Capture {item.Name}", binding =>
        {
            item.SetBinding(binding);
            ShowToast($"{item.Name}: {binding.DisplayName}");
        }, includePrimaryMouse: true);
    }

    private void SetupCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string id }) return;
        var item = GameBindItems.First(bind => bind.Id == id);
        BeginCapture($"Capture {item.Name}", binding =>
        {
            item.SetBinding(binding);
            RefreshSetupTexts();
        }, includePrimaryMouse: true);
    }

    private void BeginCapture(string title, Action<NativeBinding> apply, bool includePrimaryMouse = false, bool includeController = false)
    {
        _captureApply = apply;
        CaptureTitleText.Text = title;
        CaptureHelpText.Text = includeController
            ? "Press an Xbox, PS4, or PS5 controller button. PS buttons are normalized to the matching virtual Xbox output."
            : "Press any keyboard key or mouse button. Standalone modifiers such as L Alt are captured separately.";
        CaptureOverlay.Visibility = Visibility.Visible;
        _engine.BeginCapture(binding => Dispatcher.BeginInvoke(() =>
        {
            CaptureOverlay.Visibility = Visibility.Collapsed;
            var callback = _captureApply;
            _captureApply = null;
            callback?.Invoke(binding);
        }), includePrimaryMouse, includeController);
    }

    private void CancelCapture_Click(object sender, RoutedEventArgs e)
    {
        _engine.CancelCapture();
        _captureApply = null;
        CaptureOverlay.Visibility = Visibility.Collapsed;
        ShowToast("Capture cancelled");
    }

    private void ShowFirstRunSetup()
    {
        RefreshSetupTexts();
        SetupOverlay.Visibility = Visibility.Visible;
    }

    private void ShowSetup_Click(object sender, RoutedEventArgs e) => ShowFirstRunSetup();

    private void CompleteSetup_Click(object sender, RoutedEventArgs e)
    {
        _settings.SetupComplete = true;
        SetupOverlay.Visibility = Visibility.Collapsed;
        DashboardNav.IsChecked = true;
        DestinySkatesTab.IsChecked = true;
        DestinyPage.Visibility = Visibility.Visible;
        ShowDestinyCategory("Keyboard");
        SitesPage.Visibility = Visibility.Collapsed;
        FortnitePage.Visibility = Visibility.Collapsed;
        SaveSettings(false);
        ShowToast("Game binds saved");
    }

    private void RefreshSetupTexts()
    {
        if (!IsInitialized) return;
        SetupAirMoveText.Text = _settings.GameBindings.AirMove.DisplayName;
        SetupSuperText.Text = _settings.GameBindings.Super.DisplayName;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveSettings(true);

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettings(bool notify)
    {
        try
        {
            _settingsStore.Save(_settings);
            if (notify) ShowToast("Settings saved");
        }
        catch (Exception ex)
        {
            ShowToast($"Save failed: {ex.Message}");
        }
    }

    private void Panic()
    {
        _engine.Panic();
        ShowToast("All controlled inputs released");
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseToTray_Click(object sender, RoutedEventArgs e) => ShowCloseChoice();

    private void ShowCloseChoice()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // WebView2 is a native HWND and otherwise renders above WPF overlays.
        if (_sitesBrowser is not null) _sitesBrowser.Visibility = Visibility.Collapsed;
        CloseChoiceOverlay.Visibility = Visibility.Visible;
        Activate();
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        CloseChoiceOverlay.Visibility = Visibility.Collapsed;
        Hide();
        _tray?.ShowBalloon("/stalking macro", "Still running. Enabled binds remain active.");
    }

    private void ForceShutdown_Click(object sender, RoutedEventArgs e)
    {
        CloseChoiceOverlay.Visibility = Visibility.Collapsed;
        ExitApplication();
    }

    private void ShowWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (_sitesBrowser is not null && SitesPage.Visibility == Visibility.Visible)
            _sitesBrowser.Visibility = Visibility.Visible;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplication()
    {
        if (_allowExit) return;
        _allowExit = true;
        _saveTimer.Stop();
        SaveSettings(false);
        _engine.Panic();
        _engine.Dispose();
        _sitesBrowser?.Dispose();
        _tray?.Dispose();
        _tray = null;
        System.Windows.Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExit) return;
        e.Cancel = true;
        ShowCloseChoice();
    }

    private static void ApplyNativeWindowStyle(IntPtr window)
    {
        var enabled = 1;
        DwmSetWindowAttribute(window, 20, ref enabled, sizeof(int));
        var cornerPreference = 2;
        DwmSetWindowAttribute(window, 33, ref cornerPreference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
