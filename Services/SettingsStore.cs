using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using D2MacroNative.Models;
using NativeBinding = D2MacroNative.Models.InputBinding;
using NativeDevice = D2MacroNative.Models.InputDevice;

namespace D2MacroNative.Services;

public sealed class SettingsStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsStore()
    {
        SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "D2MacroNative");
        SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public string SettingsDirectory { get; }
    public string SettingsPath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return CreateDefaults();
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
            return Normalize(settings ?? CreateDefaults());
        }
        catch
        {
            return CreateDefaults();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, SettingsPath, true);
    }

    public static AppSettings CreateDefaults() => new()
    {
        SetupComplete = false,
        ActivationModeVersion = 2,
        DesyncReliabilityVersion = 1,
        DesyncSelectionVersion = 1,
        SwordHeavyBindingVersion = 1,
        GameBindings = new GameBindings(),
        FortniteBindings = new FortniteBindings(),
        Timings = new TimingSettings(),
        Wall = new WallSettings { CalibrationVersion = 3 },
        Macros = new ObservableCollection<MacroConfig>
        {
            new()
            {
                Kind = MacroKind.ShatterSkate,
                Name = "Shatterskate",
                Category = "HUNTER  ·  STASIS",
                Accent = "#A8E6C0",
                Description = "Draws your heavy weapon, performs an Eager Edge heavy with Jump, then activates Shatterdive.",
                Activation = NativeBinding.Keyboard(Key.F1),
                ActivationMode = MacroActivationMode.OneTime
            },
            new()
            {
                Kind = MacroKind.GroundSkate,
                Name = "Ground Shatterskate",
                Category = "HUNTER  ·  STASIS",
                Accent = "#B7EAC9",
                Description = "Draws your heavy weapon, then performs Jump → light attack → Jump → Shatterdive.",
                Activation = NativeBinding.Keyboard(Key.F8),
                ActivationMode = MacroActivationMode.OneTime
            },
            new()
            {
                Kind = MacroKind.HunterStrandSkate,
                Name = "Strand Slam Skate",
                Category = "HUNTER  ·  STRAND",
                Accent = "#86D9A7",
                Description = "Draws your heavy weapon, performs an Eager Edge heavy with Jump, then activates Ensnaring Slam.",
                Activation = NativeBinding.Keyboard(Key.F10),
                ActivationMode = MacroActivationMode.OneTime
            },
            new()
            {
                Kind = MacroKind.WellSkate,
                Name = "Wellskate",
                Category = "WARLOCK  ·  SOLAR",
                Accent = "#C0F0D2",
                Description = "Draws your heavy weapon, holds Eager Edge heavy, then triggers Jump and Well before returning to slot 2.",
                Activation = NativeBinding.Keyboard(Key.F2),
                ActivationMode = MacroActivationMode.OneTime
            },
            new()
            {
                Kind = MacroKind.GroundWellSkate,
                Name = "Ground Wellskate",
                Category = "WARLOCK  ·  SOLAR",
                Accent = "#D0F5DC",
                Description = "Draws your heavy weapon, then performs Jump → light attack → Jump → Well of Radiance.",
                Activation = NativeBinding.Keyboard(Key.F11),
                ActivationMode = MacroActivationMode.OneTime
            },
            new()
            {
                Kind = MacroKind.StrandSkate,
                Name = "Strand Skate",
                Category = "WARLOCK  ·  STRAND",
                Accent = "#9FE2BA",
                Description = "Recorded Strand timing: sword → right-click → Class Ability + Super → saved Air Move bind for Weavewalk.",
                Activation = NativeBinding.Keyboard(Key.F9),
                ActivationMode = MacroActivationMode.OneTime
            },
            new()
            {
                Kind = MacroKind.AcrobaticsSkate,
                Name = "Acrobatics Skate",
                Category = "HUNTER  ·  SOLAR",
                Accent = "#95DDB1",
                Description = "Draws your heavy weapon, activates Eager Edge heavy, then dodges and returns to slot 2.",
                Activation = NativeBinding.Keyboard(Key.F3),
                ActivationMode = MacroActivationMode.OneTime
            },
            new()
            {
                Kind = MacroKind.BubbleSkate,
                Name = "Bubble Skate",
                Category = "TITAN  ·  VOID",
                Accent = "#A9DCC0",
                Description = "At a ledge, draws your heavy weapon and chains Eager Edge heavy → Jump → Ward of Dawn.",
                Activation = NativeBinding.Keyboard(Key.F12),
                ActivationMode = MacroActivationMode.OneTime
            },
            new()
            {
                Kind = MacroKind.LoadoutSpam,
                Name = "Desync",
                Category = "LOADOUT  ·  HOLD TO DESYNC",
                Accent = "#B7EAC9",
                Description = "While held, repeatedly equips the four selected loadouts in the displayed order.",
                Activation = NativeBinding.Mouse(NativeDevice.MouseX1),
                ActivationMode = MacroActivationMode.Hold,
                SelectedLoadout = 1,
                SelectedLoadoutSecondary = 5,
                SelectedLoadoutTertiary = 9,
                SelectedLoadoutQuaternary = 13
            },
            new()
            {
                Kind = MacroKind.LoadoutSwap,
                Name = "Loadout Swapper",
                Category = "LOADOUT  ·  ALTERNATING",
                Accent = "#9FE2BA",
                Description = "Switches between Loadout A and Loadout B each time you press the activation bind.",
                Activation = NativeBinding.Keyboard(Key.F5),
                ActivationMode = MacroActivationMode.OneTime,
                SelectedLoadout = 1,
                SelectedLoadoutSecondary = 2
            },
            new()
            {
                Kind = MacroKind.RocketGrapple,
                Name = "Rocket Grapple",
                Category = "STRAND  ·  MOVEMENT",
                Accent = "#A8E6C0",
                Description = "Draws your heavy weapon, fires and grapples with zero programmed gap, then corrects recoil. Best with a ≤30 Velocity rocket and no Impulse Amplifier.",
                Activation = NativeBinding.Mouse(NativeDevice.MouseX2),
                ActivationMode = MacroActivationMode.OneTime
            },
            new()
            {
                Kind = MacroKind.SparrowSlipstream,
                Name = "Slipstream Assist",
                Category = "SPARROW  ·  HOLD TO FLY",
                Accent = "#B7EAC9",
                Description = "Post-patch assist: summons, launches, then pulses Back, Destabilizer and your dedicated Dodge Left bind while held.",
                Activation = NativeBinding.Keyboard(Key.Home),
                ActivationMode = MacroActivationMode.Hold
            },
            new()
            {
                Kind = MacroKind.WishWall,
                Name = "Wish Wall",
                Category = "DESTINY 2  ·  DIVINITY",
                Accent = "#C8F3D7",
                Description = "Uses the optimized wall_menu4 pattern, FPS-matched speed, and calibrated recoil to enter and submit the selected wish.",
                Activation = NativeBinding.Keyboard(Key.F4),
                ActivationMode = MacroActivationMode.OneTime
            },
            new()
            {
                Kind = MacroKind.FortniteEditSpam,
                Name = "Edit Spam",
                Category = "FORTNITE  ·  HOLD TO SPAM",
                Accent = "#A8E6C0",
                Description = "Repeatedly presses your Edit and Select inputs for as long as the activation bind is held.",
                Activation = NativeBinding.Keyboard(Key.F6),
                ActivationMode = MacroActivationMode.Hold
            },
            new()
            {
                Kind = MacroKind.FortniteDragEdit,
                Name = "Drag Edit",
                Category = "FORTNITE  ·  HOLD TO DRAG",
                Accent = "#C0F0D2",
                Description = "Holds Edit and Select while you drag, then releases them in order to confirm the edit.",
                Activation = NativeBinding.Keyboard(Key.F7),
                ActivationMode = MacroActivationMode.Hold
            }
        }
    };

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.GameBindings ??= new GameBindings();
        settings.ControllerBindings ??= new ControllerOutputBindings();
        settings.ControllerTimings ??= new ControllerTimingSettings();
        settings.ControllerTimings.WellHeavyToJump = 50;
        settings.ControllerTimings.WellJumpToSuper = 0;
        settings.ControllerTimings.GroundWellJumpToLight = 15;
        settings.ControllerTimings.GroundWellLightToJump = 15;
        settings.ControllerTimings.GroundWellJumpToSuper = 15;
        if (settings.ControllerOutputBindingVersion < 1)
        {
            settings.ControllerBindings = new ControllerOutputBindings();
            settings.ControllerOutputBindingVersion = 1;
        }
        settings.FortniteBindings ??= new FortniteBindings();
        settings.Timings ??= new TimingSettings();
        settings.Wall ??= new WallSettings();
        settings.Macros ??= [];

        if (settings.Wall.CalibrationVersion < 3)
        {
            settings.Wall.CalibrationVersion = 3;
        }

        // These inputs are intentionally fixed and no longer appear in Game Binds.
        settings.GameBindings.Forward = NativeBinding.Keyboard(Key.W);
        settings.GameBindings.Jump = NativeBinding.Keyboard(Key.Space);
        if (settings.SwordHeavyBindingVersion < 1)
        {
            settings.GameBindings.HeavyAttack = NativeBinding.Mouse(NativeDevice.MouseRight);
            settings.SwordHeavyBindingVersion = 1;
        }
        settings.GameBindings.WeaponSwap = NativeBinding.Keyboard(Key.D3);
        settings.Timings.ShatterSwordReadyDelay = 400;
        settings.Timings.ShatterAirMoveGap = 22;
        settings.Timings.GroundInitialJumpHold = 20;
        settings.Timings.GroundJumpToLightDelay = 17;
        settings.Timings.GroundLightAttackHold = 21;
        settings.Timings.GroundSecondJumpHold = 15;
        settings.Timings.GroundSecondJumpToShatterdiveDelay = 12;
        settings.Timings.GroundShatterdiveOverlapHold = 56;
        settings.Timings.GroundJumpReleaseTail = 46;
        settings.Timings.WellWeaponHold = 200;
        settings.Timings.WellSwordReadyDelay = 257;
        settings.Timings.WellAttackToJumpDelay = 50;
        settings.Timings.WellJumpToSuperDelay = 0;
        settings.Timings.WellComboHold = 78;
        settings.Timings.WellJumpReleaseTail = 15;
        settings.Timings.WellSuperReleaseTail = 24;
        settings.Timings.GroundWellSwordReadyDelay = 500;
        settings.Timings.GroundWellJumpHold = 10;
        settings.Timings.GroundWellJumpToLightDelay = 15;
        settings.Timings.GroundWellLightAttackHold = 15;
        settings.Timings.GroundWellLightToJumpDelay = 15;
        settings.Timings.GroundWellJumpToSuperDelay = 15;
        settings.Timings.GroundWellSuperHold = 10;
        settings.Timings.BubbleSwordReadyDelay = 500;
        settings.Timings.BubbleAttackToJumpDelay = 10;
        settings.Timings.BubbleJumpToSuperDelay = 5;
        settings.Timings.BubbleComboHold = 20;
        settings.Timings.StrandWeaponHold = 80;
        settings.Timings.StrandSwordReadyDelay = 1430;
        settings.Timings.StrandHeavyToComboDelay = 50;
        settings.Timings.StrandSuperToWeavewalkDelay = 10;
        settings.Timings.StrandComboHold = 60;
        settings.Timings.StrandRiftReleaseTail = 10;
        settings.Timings.StrandEndDelay = 1570;
        settings.Timings.AcrobaticsSwordHold = 10;
        settings.Timings.AcrobaticsSwordReadyDelay = 1080;
        settings.Timings.AcrobaticsAttackToDodgeDelay = 50;
        settings.Timings.AcrobaticsDodgeHold = 90;
        settings.Timings.AcrobaticsExitDelay = 50;
        settings.Timings.AcrobaticsExitHold = settings.Timings.TapHold;
        settings.Timings.RocketFireHold = 10;
        settings.Timings.RocketToGrappleDelay = 0;
        settings.Timings.RocketGrappleHold = 10;
        settings.Timings.FortniteEditToSelectDelay = 8;
        if (settings.DesyncReliabilityVersion < 1)
        {
            settings.Timings.LoadoutMoveDelay = 32;
            settings.Timings.DesyncClickHold = 24;
            settings.Timings.LoadoutInterval = 55;
            settings.DesyncReliabilityVersion = 1;
        }

        if (settings.DesyncSelectionVersion < 1)
        {
            var existingDesync = settings.Macros.FirstOrDefault(macro => macro.Kind == MacroKind.LoadoutSpam);
            if (existingDesync is not null)
            {
                existingDesync.SelectedLoadout = 1;
                existingDesync.SelectedLoadoutSecondary = 5;
                existingDesync.SelectedLoadoutTertiary = 9;
                existingDesync.SelectedLoadoutQuaternary = 13;
            }
            settings.DesyncSelectionVersion = 1;
        }

        var defaults = CreateDefaults();
        var migrateDesyncMode = settings.ActivationModeVersion < 1;
        foreach (var defaultMacro in defaults.Macros)
        {
            var existing = settings.Macros.FirstOrDefault(m => m.Kind == defaultMacro.Kind);
            if (existing is null)
            {
                settings.Macros.Add(defaultMacro);
                continue;
            }

            existing.Name = defaultMacro.Name;
            existing.Category = defaultMacro.Category;
            existing.Accent = defaultMacro.Accent;
            existing.Description = defaultMacro.Description;
            existing.Activation ??= defaultMacro.Activation;
            if (existing.Activation.Device == NativeDevice.Controller)
            {
                existing.ControllerActivation = existing.Activation;
                existing.Activation = defaultMacro.Activation.Clone();
                existing.IsEnabled = true;
            }
            if (defaultMacro.Kind != MacroKind.LoadoutSpam || migrateDesyncMode)
                existing.ActivationMode = defaultMacro.ActivationMode;
            existing.SelectedLoadout = Math.Clamp(existing.SelectedLoadout, 1, 20);
            existing.SelectedLoadoutSecondary = Math.Clamp(existing.SelectedLoadoutSecondary, 1, 20);
            existing.SelectedLoadoutTertiary = Math.Clamp(existing.SelectedLoadoutTertiary, 1, 20);
            existing.SelectedLoadoutQuaternary = Math.Clamp(existing.SelectedLoadoutQuaternary, 1, 20);
        }

        // Older builds used one switch for both input engines. Preserve the
        // prior controller state once, then persist the two switches separately.
        if (settings.SeparateInputEnableVersion < 1)
        {
            foreach (var macro in settings.Macros.Where(macro => macro.ControllerActivation is not null))
                macro.ControllerIsEnabled = macro.IsEnabled;
            settings.SeparateInputEnableVersion = 1;
        }

        // Controller cards no longer expose a separate enable switch: a bind
        // means active, and UNBOUND means inactive. Preserve the first active
        // owner of each button and discard stale duplicate bindings once.
        if (settings.ControllerImplicitEnableVersion < 1)
        {
            var usedControllerButtons = new HashSet<int>();
            foreach (var macro in settings.Macros
                         .OrderByDescending(macro => macro.ControllerIsEnabled))
            {
                if (macro.ControllerActivation is null)
                {
                    macro.ControllerIsEnabled = false;
                    continue;
                }

                var button = KeyNames.NormalizeControllerCode(macro.ControllerActivation.VirtualKey);
                if (usedControllerButtons.Add(button))
                    macro.ControllerIsEnabled = true;
                else
                {
                    macro.ControllerActivation = null;
                    macro.ControllerIsEnabled = false;
                }
            }
            settings.ControllerImplicitEnableVersion = 1;
        }

        var loadoutSwapDefault = defaults.Macros.First(macro => macro.Kind == MacroKind.LoadoutSwap);
        var loadoutSwappers = settings.Macros.Where(macro => macro.Kind == MacroKind.LoadoutSwap).ToArray();
        for (var index = 0; index < loadoutSwappers.Length; index++)
        {
            var swapper = loadoutSwappers[index];
            swapper.Name = index == 0 ? "Loadout Swapper" : $"Loadout Swapper {index + 1}";
            swapper.Category = loadoutSwapDefault.Category;
            swapper.Accent = loadoutSwapDefault.Accent;
            swapper.Description = loadoutSwapDefault.Description;
            swapper.ActivationMode = MacroActivationMode.OneTime;
            swapper.SelectedLoadout = Math.Clamp(swapper.SelectedLoadout, 1, 20);
            swapper.SelectedLoadoutSecondary = Math.Clamp(swapper.SelectedLoadoutSecondary, 1, 20);
            if (swapper.SelectedLoadoutSecondary == swapper.SelectedLoadout)
                swapper.SelectedLoadoutSecondary = swapper.SelectedLoadout == 20 ? 19 : swapper.SelectedLoadout + 1;
        }

        settings.ActivationModeVersion = 2;

        // Retain retired enum values so older settings still deserialize, then
        // remove their cards without touching any other saved bind.
        for (var i = settings.Macros.Count - 1; i >= 0; i--)
            if (settings.Macros[i].Kind is MacroKind.EagerEdgeSkate or MacroKind.IcarusSkate or MacroKind.SnapSkate or MacroKind.WaterSkate)
                settings.Macros.RemoveAt(i);

        var allowedKinds = Enum.GetValues<MacroKind>().ToHashSet();
        for (var i = settings.Macros.Count - 1; i >= 0; i--)
            if (!allowedKinds.Contains(settings.Macros[i].Kind)) settings.Macros.RemoveAt(i);

        var macroIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var macro in settings.Macros)
        {
            if (macro.ControllerActivation is not null)
                macro.ControllerActivation.VirtualKey = KeyNames.NormalizeControllerCode(macro.ControllerActivation.VirtualKey);
            if (string.IsNullOrWhiteSpace(macro.Id) || !macroIds.Add(macro.Id))
            {
                macro.Id = Guid.NewGuid().ToString("N");
                macroIds.Add(macro.Id);
            }
        }

        return settings;
    }
}
