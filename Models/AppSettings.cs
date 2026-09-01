using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Media;

namespace D2MacroNative.Models;

public abstract class BindableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputDevice
{
    Keyboard,
    MouseLeft,
    MouseRight,
    MouseX1,
    MouseX2,
    Controller
}

public sealed class InputBinding : BindableBase
{
    private InputDevice _device;
    private int _virtualKey;

    public InputDevice Device
    {
        get => _device;
        set { if (SetField(ref _device, value)) Notify(nameof(DisplayName)); }
    }

    public int VirtualKey
    {
        get => _virtualKey;
        set { if (SetField(ref _virtualKey, value)) Notify(nameof(DisplayName)); }
    }

    [JsonIgnore]
    public string DisplayName => KeyNames.Display(this);

    public InputBinding Clone() => new() { Device = Device, VirtualKey = VirtualKey };

    public bool SameAs(InputBinding? other) => other is not null && Device == other.Device && VirtualKey == other.VirtualKey;

    public static InputBinding Keyboard(Key key) => new()
    {
        Device = InputDevice.Keyboard,
        VirtualKey = KeyInterop.VirtualKeyFromKey(key)
    };

    public static InputBinding Mouse(InputDevice device) => new() { Device = device };
    public static InputBinding Controller(int button) => new()
    {
        Device = InputDevice.Controller,
        VirtualKey = KeyNames.NormalizeControllerCode(button)
    };
}

public static class KeyNames
{
    private const int PlayStationButtonBase = 0x100000;

    public static int NormalizeControllerCode(int code)
    {
        if (code < PlayStationButtonBase) return code;
        if (code is >= PlayStationButtonBase and < PlayStationButtonBase + 32)
        {
            return (code - PlayStationButtonBase) switch
            {
                0 => 0x4000,  // Square / X
                1 => 0x1000,  // Cross / A
                2 => 0x2000,  // Circle / B
                3 => 0x8000,  // Triangle / Y
                4 => 0x0100,  // L1 / LB
                5 => 0x0200,  // R1 / RB
                6 => 0x10000, // L2 / LT
                7 => 0x20000, // R2 / RT
                8 => 0x0020,  // Share/Create / View
                9 => 0x0010,  // Options / Menu
                10 => 0x0040, // L3
                11 => 0x0080, // R3
                _ => code
            };
        }

        return code switch
        {
            PlayStationButtonBase + 0x100 => 0x0001,
            PlayStationButtonBase + 0x101 => 0x0008,
            PlayStationButtonBase + 0x102 => 0x0002,
            PlayStationButtonBase + 0x103 => 0x0004,
            _ => code
        };
    }

    public static string Display(InputBinding binding)
    {
        if (binding.Device != InputDevice.Keyboard)
        {
            if (binding.Device == InputDevice.Controller)
            {
                if (binding.VirtualKey >= PlayStationButtonBase)
                    return PlayStationDisplay(binding.VirtualKey);
                return binding.VirtualKey switch
                {
                    0x0001 => "↑ / PS ↑",
                    0x0002 => "↓ / PS ↓",
                    0x0004 => "← / PS ←",
                    0x0008 => "→ / PS →",
                    0x0010 => "MENU / OPTIONS",
                    0x0020 => "VIEW / CREATE",
                    0x0040 => "L3",
                    0x0080 => "R3",
                    0x0100 => "LB / L1",
                    0x0200 => "RB / R1",
                    0x1000 => "A / CROSS",
                    0x2000 => "B / CIRCLE",
                    0x4000 => "X / SQUARE",
                    0x8000 => "Y / TRIANGLE",
                    0x10000 => "LT / L2",
                    0x20000 => "RT / R2",
                    _ => $"PAD {binding.VirtualKey:X}"
                };
            }
            return binding.Device switch
            {
                InputDevice.MouseLeft => "LMB",
                InputDevice.MouseRight => "RMB",
                InputDevice.MouseX1 => "MOUSE 4",
                InputDevice.MouseX2 => "MOUSE 5",
                _ => "MOUSE"
            };
        }

        var key = KeyInterop.KeyFromVirtualKey(binding.VirtualKey);
        return key switch
        {
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            Key.LeftAlt => "L ALT",
            Key.RightAlt => "R ALT",
            Key.LeftCtrl => "L CTRL",
            Key.RightCtrl => "R CTRL",
            Key.LeftShift => "L SHIFT",
            Key.RightShift => "R SHIFT",
            Key.LWin => "L WIN",
            Key.RWin => "R WIN",
            Key.Return => "ENTER",
            Key.Back => "BACKSPACE",
            Key.Capital => "CAPS LOCK",
            Key.Next => "PAGE DOWN",
            Key.Prior => "PAGE UP",
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",
            Key.Oem5 => "\\",
            Key.Oem1 => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.None => $"VK {binding.VirtualKey:X2}",
            _ => key.ToString().ToUpperInvariant()
        };
    }

    private static string PlayStationDisplay(int code)
    {
        if (code is >= PlayStationButtonBase and < PlayStationButtonBase + 32)
        {
            var index = code - PlayStationButtonBase;
            return index switch
            {
                0 => "PS SQUARE",
                1 => "PS CROSS",
                2 => "PS CIRCLE",
                3 => "PS TRIANGLE",
                4 => "PS L1",
                5 => "PS R1",
                6 => "PS L2",
                7 => "PS R2",
                8 => "PS SHARE/CREATE",
                9 => "PS OPTIONS",
                10 => "PS L3",
                11 => "PS R3",
                12 => "PS BUTTON",
                13 => "PS TOUCHPAD",
                _ => $"PS BUTTON {index + 1}"
            };
        }

        return code switch
        {
            PlayStationButtonBase + 0x100 => "PS ↑",
            PlayStationButtonBase + 0x101 => "PS →",
            PlayStationButtonBase + 0x102 => "PS ↓",
            PlayStationButtonBase + 0x103 => "PS ←",
            _ => $"PS {code - PlayStationButtonBase:X}"
        };
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MacroKind
{
    EagerEdgeSkate,
    ShatterSkate,
    GroundSkate,
    HunterStrandSkate,
    WellSkate,
    GroundWellSkate,
    WaterSkate,
    IcarusSkate,
    SnapSkate,
    StrandSkate,
    AcrobaticsSkate,
    BubbleSkate,
    LoadoutSpam,
    LoadoutSwap,
    RocketGrapple,
    SparrowSlipstream,
    WishWall,
    FortniteEditSpam,
    FortniteDragEdit
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MacroActivationMode
{
    OneTime,
    Hold,
    Toggle
}

public sealed class MacroConfig : BindableBase
{
    private InputBinding _activation = InputBinding.Keyboard(Key.F1);
    private InputBinding? _controllerActivation;
    private MacroActivationMode _activationMode = MacroActivationMode.OneTime;
    private bool _isEnabled;
    private bool _controllerIsEnabled;
    private int _selectedLoadout = 1;
    private int _selectedLoadoutSecondary = 2;
    private int _selectedLoadoutTertiary = 3;
    private int _selectedLoadoutQuaternary = 4;
    private int _runs;
    private string _lastResult = "READY";
    private bool _canAddLoadoutSwap;
    private bool _canRemoveLoadoutSwap;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MacroKind Kind { get; set; }
    public string Name { get; set; } = "Macro";
    public string Category { get; set; } = "MOVEMENT";
    public string Accent { get; set; } = "#A8E6C0";
    public string Description { get; set; } = "Native input sequence";

    public InputBinding Activation
    {
        get => _activation;
        set
        {
            _activation = value ?? InputBinding.Keyboard(Key.F1);
            Notify();
            Notify(nameof(BindingDisplay));
        }
    }

    public InputBinding? ControllerActivation
    {
        get => _controllerActivation;
        set
        {
            _controllerActivation = value?.Clone();
            Notify();
            Notify(nameof(ControllerBindingDisplay));
        }
    }

    public MacroActivationMode ActivationMode
    {
        get => _activationMode;
        set
        {
            if (SetField(ref _activationMode, value))
                Notify(nameof(ActivationModeDisplay));
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (SetField(ref _isEnabled, value)) Notify(nameof(StatusText)); }
    }

    public bool ControllerIsEnabled
    {
        get => _controllerIsEnabled;
        set { if (SetField(ref _controllerIsEnabled, value)) Notify(nameof(ControllerStatusText)); }
    }

    public int SelectedLoadout
    {
        get => _selectedLoadout;
        set => SetField(ref _selectedLoadout, Math.Clamp(value, 1, 20));
    }

    public int SelectedLoadoutSecondary
    {
        get => _selectedLoadoutSecondary;
        set => SetField(ref _selectedLoadoutSecondary, Math.Clamp(value, 1, 20));
    }

    public int SelectedLoadoutTertiary
    {
        get => _selectedLoadoutTertiary;
        set => SetField(ref _selectedLoadoutTertiary, Math.Clamp(value, 1, 20));
    }

    public int SelectedLoadoutQuaternary
    {
        get => _selectedLoadoutQuaternary;
        set => SetField(ref _selectedLoadoutQuaternary, Math.Clamp(value, 1, 20));
    }

    [JsonIgnore]
    public int Runs
    {
        get => _runs;
        set => SetField(ref _runs, value);
    }

    [JsonIgnore]
    public string LastResult
    {
        get => _lastResult;
        set => SetField(ref _lastResult, value);
    }

    [JsonIgnore] public string BindingDisplay => Activation.DisplayName;
    [JsonIgnore] public string ControllerBindingDisplay => ControllerActivation?.DisplayName ?? "UNBOUND";
    [JsonIgnore] public string ControllerDescription => Description;
    [JsonIgnore] public string StatusText => IsEnabled ? "ACTIVE" : "OFF";
    [JsonIgnore] public string ControllerStatusText => ControllerIsEnabled ? "ACTIVE" : "OFF";
    [JsonIgnore] public bool IsLoadoutSwap => Kind == MacroKind.LoadoutSwap;
    [JsonIgnore] public bool IsDesync => Kind == MacroKind.LoadoutSpam;
    [JsonIgnore] public bool IsActivationModeLocked => Kind != MacroKind.LoadoutSpam;
    [JsonIgnore] public bool CanAddLoadoutSwap
    {
        get => _canAddLoadoutSwap;
        private set => SetField(ref _canAddLoadoutSwap, value);
    }
    [JsonIgnore] public bool CanRemoveLoadoutSwap
    {
        get => _canRemoveLoadoutSwap;
        private set => SetField(ref _canRemoveLoadoutSwap, value);
    }
    [JsonIgnore] public string ActivationModeDisplay => ActivationMode switch
    {
        MacroActivationMode.OneTime => "One Time",
        MacroActivationMode.Hold => "Hold",
        MacroActivationMode.Toggle => "Toggle",
        _ => "One Time"
    };
    [JsonIgnore] public System.Windows.Media.Brush AccentBrush =>
        (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(Accent)!;

    public void SetActivation(InputBinding binding)
    {
        Activation = binding.Clone();
    }

    public void SetControllerActivation(InputBinding binding) => ControllerActivation = binding;

    public void SetLoadoutCardControls(bool canAdd, bool canRemove)
    {
        CanAddLoadoutSwap = canAdd;
        CanRemoveLoadoutSwap = canRemove;
    }
}

public sealed class GameBindings
{
    public InputBinding Forward { get; set; } = InputBinding.Keyboard(Key.W);
    public InputBinding Jump { get; set; } = InputBinding.Keyboard(Key.Space);
    public InputBinding AirMove { get; set; } = InputBinding.Keyboard(Key.X);
    public InputBinding Super { get; set; } = InputBinding.Keyboard(Key.F);
    public InputBinding Rift { get; set; } = InputBinding.Keyboard(Key.V);
    public InputBinding Dodge { get; set; } = InputBinding.Keyboard(Key.V);
    public InputBinding Grapple { get; set; } = InputBinding.Keyboard(Key.Q);
    public InputBinding HeavyAttack { get; set; } = InputBinding.Mouse(InputDevice.MouseRight);
    public InputBinding WeaponSwap { get; set; } = InputBinding.Keyboard(Key.D3);
    public InputBinding WellExitWeapon { get; set; } = InputBinding.Keyboard(Key.D2);
    public InputBinding Inventory { get; set; } = InputBinding.Keyboard(Key.F1);
    public InputBinding OpenLoadouts { get; set; } = InputBinding.Keyboard(Key.Left);
    public InputBinding CloseMenu { get; set; } = InputBinding.Keyboard(Key.Escape);
    public InputBinding Ghost { get; set; } = InputBinding.Keyboard(Key.Tab);
    public InputBinding SummonVehicle { get; set; } = InputBinding.Keyboard(Key.E);
    public InputBinding SparrowBoost { get; set; } = InputBinding.Mouse(InputDevice.MouseRight);
    public InputBinding SparrowDestabilizer { get; set; } = InputBinding.Keyboard(Key.LeftShift);
    public InputBinding SparrowBack { get; set; } = InputBinding.Keyboard(Key.S);
    public InputBinding SparrowLeft { get; set; } = InputBinding.Keyboard(Key.A);
    public InputBinding SparrowDodgeLeft { get; set; } = InputBinding.Mouse(InputDevice.MouseX1);
}

public sealed class ControllerOutputBindings
{
    // Pure virtual Xbox output. PS4/PS5 capture is normalized to these codes.
    public InputBinding HeavyAttack { get; set; } = InputBinding.Controller(0x20000); // RT / R2
    public InputBinding LightAttack { get; set; } = InputBinding.Controller(0x0200);  // RB / R1
    public InputBinding Jump { get; set; } = InputBinding.Controller(0x1000);         // A / Cross
    public InputBinding AirMove { get; set; } = InputBinding.Controller(0x2000);      // B / Circle
    public InputBinding SuperModifier { get; set; } = InputBinding.Controller(0x0100); // LB / L1
    public InputBinding Super { get; set; } = InputBinding.Controller(0x0200);        // RB / R1
    public InputBinding Rift { get; set; } = InputBinding.Controller(0x2000);         // B / Circle
    public InputBinding Dodge { get; set; } = InputBinding.Controller(0x2000);        // B / Circle
    public InputBinding ExitWeapon { get; set; } = InputBinding.Controller(0x8000);   // Y / Triangle
}

public sealed class ControllerTimingSettings
{
    public int ReleaseSettle { get; set; } = 20;
    public int TapHold { get; set; } = 35;
    public int SwordReady { get; set; } = 120;
    public int ShatterHeavyHold { get; set; } = 45;
    public int ShatterAirMoveGap { get; set; } = 22;
    public int GroundJumpHold { get; set; } = 35;
    public int GroundJumpToLight { get; set; } = 20;
    public int GroundLightHold { get; set; } = 35;
    public int GroundSecondJumpHold { get; set; } = 35;
    public int GroundDiveGap { get; set; } = 15;
    public int GroundDiveHold { get; set; } = 60;
    public int WellHeavyToJump { get; set; } = 50;
    public int WellJumpToSuper { get; set; } = 0;
    public int WellComboHold { get; set; } = 78;
    public int GroundWellJumpToLight { get; set; } = 15;
    public int GroundWellLightToJump { get; set; } = 15;
    public int GroundWellJumpToSuper { get; set; } = 15;
    public int StrandHeavyToCombo { get; set; } = 50;
    public int StrandSuperToWeavewalk { get; set; } = 1;
    public int StrandComboHold { get; set; } = 35;
    public int AcrobaticsHeavyToDodge { get; set; } = 50;
    public int AcrobaticsDodgeHold { get; set; } = 90;
    public int BubbleHeavyToJump { get; set; } = 45;
    public int BubbleJumpToSuper { get; set; } = 10;
    public int BubbleComboHold { get; set; } = 35;
    public int ExitDelay { get; set; } = 50;
}

public sealed class FortniteBindings
{
    public InputBinding Edit { get; set; } = InputBinding.Keyboard(Key.F);
    public InputBinding Select { get; set; } = InputBinding.Mouse(InputDevice.MouseLeft);
}

public sealed class TimingSettings : BindableBase
{
    private int _tapHold = 14;
    private int _shatterSwordReadyDelay = 400;
    private int _shatterJumpGap = 10;
    private int _shatterAirMoveGap = 22;
    private int _shatterReleaseDelay = 20;
    private int _groundInitialJumpHold = 20;
    private int _groundJumpToLightDelay = 17;
    private int _groundLightAttackHold = 21;
    private int _groundSecondJumpHold = 15;
    private int _groundSecondJumpToShatterdiveDelay = 12;
    private int _groundShatterdiveOverlapHold = 56;
    private int _groundJumpReleaseTail = 46;
    private int _wellWeaponHold = 200;
    private int _wellSwordReadyDelay = 257;
    private int _wellAttackToJumpDelay = 50;
    private int _wellJumpToSuperDelay;
    private int _wellComboHold = 78;
    private int _wellJumpReleaseTail = 15;
    private int _wellSuperReleaseTail = 24;
    private int _groundWellSwordReadyDelay = 500;
    private int _groundWellJumpHold = 10;
    private int _groundWellJumpToLightDelay = 15;
    private int _groundWellLightAttackHold = 15;
    private int _groundWellLightToJumpDelay = 15;
    private int _groundWellJumpToSuperDelay = 15;
    private int _groundWellSuperHold = 10;
    private int _bubbleSwordReadyDelay = 500;
    private int _bubbleAttackToJumpDelay = 10;
    private int _bubbleJumpToSuperDelay = 5;
    private int _bubbleComboHold = 20;
    private int _strandWeaponHold = 80;
    private int _strandSwordReadyDelay = 1430;
    private int _strandHeavyToComboDelay = 50;
    private int _strandSuperToWeavewalkDelay = 10;
    private int _strandComboHold = 60;
    private int _strandRiftReleaseTail = 10;
    private int _strandEndDelay = 1570;
    private int _macroExitDelay = 50;
    private int _rocketWeaponReadyDelay = 450;
    private int _rocketFireHold = 10;
    private int _rocketToGrappleDelay = 0;
    private int _rocketGrappleHold = 10;
    private double _rocketSensitivity = 6;
    private int _eagerSwordReadyDelay = 220;
    private int _eagerLightAttackHold = 20;
    private int _eagerSwapToGlideDelay = 15;
    private int _icarusGlideToDashDelay = 12;
    private int _acrobaticsSwordHold = 10;
    private int _acrobaticsSwordReadyDelay = 1080;
    private int _acrobaticsAttackToDodgeDelay = 50;
    private int _acrobaticsDodgeHold = 90;
    private int _acrobaticsExitDelay = 50;
    private int _acrobaticsExitHold = 14;
    private int _acrobaticsDodgeDelay = 50;
    private int _acrobaticsJumpDelay = 100;
    private int _acrobaticsReleaseDelay = 20;
    private int _loadoutMoveDelay = 32;
    private int _desyncClickHold = 24;
    private int _loadoutInterval = 55;
    private int _loadoutFirstTileX = 116;
    private int _loadoutTopRowY = 385;
    private int _loadoutTileSpacing = 96;
    private int _loadoutInventoryOpenDelay = 700;
    private int _loadoutPanelOpenDelay = 500;
    private int _loadoutApplyDelay = 550;
    private int _loadoutCloseDelay = 250;
    private int _fortniteTapHold = 12;
    private int _fortniteEditToSelectDelay = 8;
    private int _fortniteSpamInterval = 25;
    private int _sparrowFps = 60;
    private int _sparrowRotationPeriod = 612;

    public int TapHold { get => _tapHold; set => SetField(ref _tapHold, Math.Clamp(value, 5, 80)); }
    public int ShatterSwordReadyDelay { get => _shatterSwordReadyDelay; set => SetField(ref _shatterSwordReadyDelay, Math.Clamp(value, 0, 500)); }
    public int ShatterJumpGap { get => _shatterJumpGap; set => SetField(ref _shatterJumpGap, Math.Clamp(value, 0, 100)); }
    public int ShatterAirMoveGap { get => _shatterAirMoveGap; set => SetField(ref _shatterAirMoveGap, Math.Clamp(value, 0, 100)); }
    public int ShatterReleaseDelay { get => _shatterReleaseDelay; set => SetField(ref _shatterReleaseDelay, Math.Clamp(value, 5, 100)); }
    public int GroundInitialJumpHold { get => _groundInitialJumpHold; set => SetField(ref _groundInitialJumpHold, Math.Clamp(value, 5, 100)); }
    public int GroundJumpToLightDelay { get => _groundJumpToLightDelay; set => SetField(ref _groundJumpToLightDelay, Math.Clamp(value, 0, 100)); }
    public int GroundLightAttackHold { get => _groundLightAttackHold; set => SetField(ref _groundLightAttackHold, Math.Clamp(value, 5, 100)); }
    public int GroundSecondJumpHold { get => _groundSecondJumpHold; set => SetField(ref _groundSecondJumpHold, Math.Clamp(value, 5, 100)); }
    public int GroundSecondJumpToShatterdiveDelay { get => _groundSecondJumpToShatterdiveDelay; set => SetField(ref _groundSecondJumpToShatterdiveDelay, Math.Clamp(value, 0, 100)); }
    public int GroundShatterdiveOverlapHold { get => _groundShatterdiveOverlapHold; set => SetField(ref _groundShatterdiveOverlapHold, Math.Clamp(value, 5, 150)); }
    public int GroundJumpReleaseTail { get => _groundJumpReleaseTail; set => SetField(ref _groundJumpReleaseTail, Math.Clamp(value, 0, 150)); }
    public int WellWeaponHold { get => _wellWeaponHold; set => SetField(ref _wellWeaponHold, Math.Clamp(value, 5, 500)); }
    public int WellSwordReadyDelay { get => _wellSwordReadyDelay; set => SetField(ref _wellSwordReadyDelay, Math.Clamp(value, 0, 1000)); }
    public int WellAttackToJumpDelay { get => _wellAttackToJumpDelay; set => SetField(ref _wellAttackToJumpDelay, Math.Clamp(value, 0, 250)); }
    public int WellJumpToSuperDelay { get => _wellJumpToSuperDelay; set => SetField(ref _wellJumpToSuperDelay, Math.Clamp(value, 0, 150)); }
    public int WellComboHold { get => _wellComboHold; set => SetField(ref _wellComboHold, Math.Clamp(value, 5, 200)); }
    public int WellJumpReleaseTail { get => _wellJumpReleaseTail; set => SetField(ref _wellJumpReleaseTail, Math.Clamp(value, 0, 100)); }
    public int WellSuperReleaseTail { get => _wellSuperReleaseTail; set => SetField(ref _wellSuperReleaseTail, Math.Clamp(value, 0, 100)); }
    public int GroundWellSwordReadyDelay { get => _groundWellSwordReadyDelay; set => SetField(ref _groundWellSwordReadyDelay, Math.Clamp(value, 0, 1000)); }
    public int GroundWellJumpHold { get => _groundWellJumpHold; set => SetField(ref _groundWellJumpHold, Math.Clamp(value, 5, 100)); }
    public int GroundWellJumpToLightDelay { get => _groundWellJumpToLightDelay; set => SetField(ref _groundWellJumpToLightDelay, Math.Clamp(value, 0, 250)); }
    public int GroundWellLightAttackHold { get => _groundWellLightAttackHold; set => SetField(ref _groundWellLightAttackHold, Math.Clamp(value, 5, 100)); }
    public int GroundWellLightToJumpDelay { get => _groundWellLightToJumpDelay; set => SetField(ref _groundWellLightToJumpDelay, Math.Clamp(value, 0, 250)); }
    public int GroundWellJumpToSuperDelay { get => _groundWellJumpToSuperDelay; set => SetField(ref _groundWellJumpToSuperDelay, Math.Clamp(value, 0, 100)); }
    public int GroundWellSuperHold { get => _groundWellSuperHold; set => SetField(ref _groundWellSuperHold, Math.Clamp(value, 5, 100)); }
    public int BubbleSwordReadyDelay { get => _bubbleSwordReadyDelay; set => SetField(ref _bubbleSwordReadyDelay, Math.Clamp(value, 0, 1000)); }
    public int BubbleAttackToJumpDelay { get => _bubbleAttackToJumpDelay; set => SetField(ref _bubbleAttackToJumpDelay, Math.Clamp(value, 0, 100)); }
    public int BubbleJumpToSuperDelay { get => _bubbleJumpToSuperDelay; set => SetField(ref _bubbleJumpToSuperDelay, Math.Clamp(value, 0, 100)); }
    public int BubbleComboHold { get => _bubbleComboHold; set => SetField(ref _bubbleComboHold, Math.Clamp(value, 5, 100)); }
    public int StrandWeaponHold { get => _strandWeaponHold; set => SetField(ref _strandWeaponHold, Math.Clamp(value, 5, 250)); }
    public int StrandSwordReadyDelay { get => _strandSwordReadyDelay; set => SetField(ref _strandSwordReadyDelay, Math.Clamp(value, 0, 2500)); }
    public int StrandHeavyToComboDelay { get => _strandHeavyToComboDelay; set => SetField(ref _strandHeavyToComboDelay, Math.Clamp(value, 0, 100)); }
    public int StrandSuperToWeavewalkDelay { get => _strandSuperToWeavewalkDelay; set => SetField(ref _strandSuperToWeavewalkDelay, Math.Clamp(value, 0, 100)); }
    public int StrandComboHold { get => _strandComboHold; set => SetField(ref _strandComboHold, Math.Clamp(value, 5, 100)); }
    public int StrandRiftReleaseTail { get => _strandRiftReleaseTail; set => SetField(ref _strandRiftReleaseTail, Math.Clamp(value, 0, 100)); }
    public int StrandEndDelay { get => _strandEndDelay; set => SetField(ref _strandEndDelay, Math.Clamp(value, 0, 2500)); }
    public int MacroExitDelay { get => _macroExitDelay; set => SetField(ref _macroExitDelay, Math.Clamp(value, 0, 250)); }
    public int RocketWeaponReadyDelay { get => _rocketWeaponReadyDelay; set => SetField(ref _rocketWeaponReadyDelay, Math.Clamp(value, 0, 1500)); }
    public int RocketFireHold { get => _rocketFireHold; set => SetField(ref _rocketFireHold, Math.Clamp(value, 5, 100)); }
    public int RocketToGrappleDelay { get => _rocketToGrappleDelay; set => SetField(ref _rocketToGrappleDelay, Math.Clamp(value, 0, 250)); }
    public int RocketGrappleHold { get => _rocketGrappleHold; set => SetField(ref _rocketGrappleHold, Math.Clamp(value, 5, 150)); }
    public double RocketSensitivity
    {
        get => _rocketSensitivity;
        set
        {
            if (SetField(ref _rocketSensitivity, Math.Clamp(value, 1, 100)))
                Notify(nameof(RocketRecoilDisplay));
        }
    }
    [JsonIgnore] public string RocketRecoilDisplay => $"{Math.Round(230.0 / RocketSensitivity, MidpointRounding.AwayFromZero):0} RAW COUNTS";
    public int EagerSwordReadyDelay { get => _eagerSwordReadyDelay; set => SetField(ref _eagerSwordReadyDelay, Math.Clamp(value, 0, 1000)); }
    public int EagerLightAttackHold { get => _eagerLightAttackHold; set => SetField(ref _eagerLightAttackHold, Math.Clamp(value, 5, 100)); }
    public int EagerSwapToGlideDelay { get => _eagerSwapToGlideDelay; set => SetField(ref _eagerSwapToGlideDelay, Math.Clamp(value, 0, 150)); }
    public int IcarusGlideToDashDelay { get => _icarusGlideToDashDelay; set => SetField(ref _icarusGlideToDashDelay, Math.Clamp(value, 0, 150)); }
    public int AcrobaticsSwordHold { get => _acrobaticsSwordHold; set => SetField(ref _acrobaticsSwordHold, Math.Clamp(value, 5, 250)); }
    public int AcrobaticsSwordReadyDelay { get => _acrobaticsSwordReadyDelay; set => SetField(ref _acrobaticsSwordReadyDelay, Math.Clamp(value, 0, 2000)); }
    public int AcrobaticsAttackToDodgeDelay { get => _acrobaticsAttackToDodgeDelay; set => SetField(ref _acrobaticsAttackToDodgeDelay, Math.Clamp(value, 0, 250)); }
    public int AcrobaticsDodgeHold { get => _acrobaticsDodgeHold; set => SetField(ref _acrobaticsDodgeHold, Math.Clamp(value, 5, 500)); }
    public int AcrobaticsExitDelay { get => _acrobaticsExitDelay; set => SetField(ref _acrobaticsExitDelay, Math.Clamp(value, 0, 2500)); }
    public int AcrobaticsExitHold { get => _acrobaticsExitHold; set => SetField(ref _acrobaticsExitHold, Math.Clamp(value, 5, 500)); }
    public int AcrobaticsDodgeDelay { get => _acrobaticsDodgeDelay; set => SetField(ref _acrobaticsDodgeDelay, Math.Clamp(value, 0, 250)); }
    public int AcrobaticsJumpDelay { get => _acrobaticsJumpDelay; set => SetField(ref _acrobaticsJumpDelay, Math.Clamp(value, 0, 350)); }
    public int AcrobaticsReleaseDelay { get => _acrobaticsReleaseDelay; set => SetField(ref _acrobaticsReleaseDelay, Math.Clamp(value, 5, 100)); }
    public int LoadoutMoveDelay { get => _loadoutMoveDelay; set => SetField(ref _loadoutMoveDelay, Math.Clamp(value, 0, 50)); }
    public int DesyncClickHold { get => _desyncClickHold; set => SetField(ref _desyncClickHold, Math.Clamp(value, 10, 100)); }
    public int LoadoutInterval { get => _loadoutInterval; set => SetField(ref _loadoutInterval, Math.Clamp(value, 20, 500)); }
    public int LoadoutFirstTileX { get => _loadoutFirstTileX; set => SetField(ref _loadoutFirstTileX, Math.Clamp(value, 0, 1920)); }
    public int LoadoutTopRowY { get => _loadoutTopRowY; set => SetField(ref _loadoutTopRowY, Math.Clamp(value, 0, 1080)); }
    public int LoadoutTileSpacing { get => _loadoutTileSpacing; set => SetField(ref _loadoutTileSpacing, Math.Clamp(value, 20, 400)); }
    public int LoadoutInventoryOpenDelay { get => _loadoutInventoryOpenDelay; set => SetField(ref _loadoutInventoryOpenDelay, Math.Clamp(value, 100, 2500)); }
    public int LoadoutPanelOpenDelay { get => _loadoutPanelOpenDelay; set => SetField(ref _loadoutPanelOpenDelay, Math.Clamp(value, 100, 2500)); }
    public int LoadoutApplyDelay { get => _loadoutApplyDelay; set => SetField(ref _loadoutApplyDelay, Math.Clamp(value, 100, 2500)); }
    public int LoadoutCloseDelay { get => _loadoutCloseDelay; set => SetField(ref _loadoutCloseDelay, Math.Clamp(value, 50, 1000)); }
    public int FortniteTapHold { get => _fortniteTapHold; set => SetField(ref _fortniteTapHold, Math.Clamp(value, 5, 80)); }
    public int FortniteEditToSelectDelay { get => _fortniteEditToSelectDelay; set => SetField(ref _fortniteEditToSelectDelay, Math.Clamp(value, 0, 100)); }
    public int FortniteSpamInterval { get => _fortniteSpamInterval; set => SetField(ref _fortniteSpamInterval, Math.Clamp(value, 10, 150)); }
    public int SparrowFps
    {
        get => _sparrowFps;
        set
        {
            if (SetField(ref _sparrowFps, Math.Clamp(value, 30, 500)))
                Notify(nameof(SparrowRhythmDisplay));
        }
    }

    public int SparrowRotationPeriod
    {
        get => _sparrowRotationPeriod;
        set
        {
            if (SetField(ref _sparrowRotationPeriod, Math.Clamp(value, 450, 800)))
                Notify(nameof(SparrowRhythmDisplay));
        }
    }

    [JsonIgnore]
    public string SparrowRhythmDisplay
    {
        get
        {
            var frameMilliseconds = 1000.0 / SparrowFps;
            var alignedMilliseconds = Math.Round(SparrowRotationPeriod / frameMilliseconds) * frameMilliseconds;
            return $"{alignedMilliseconds:0.0} MS ROTATION";
        }
    }
}

public sealed class WallSettings : BindableBase
{
    private double _sensitivity = 6;
    private int _fps = 60;
    private int _wishNumber = 4;

    public double Sensitivity
    {
        get => _sensitivity;
        set => SetField(ref _sensitivity, Math.Clamp(value, 1, 100));
    }

    public int Fps
    {
        get => _fps;
        set
        {
            if (SetField(ref _fps, Math.Clamp(value, 30, 500)))
                Notify(nameof(SpeedProfileDisplay));
        }
    }

    [JsonIgnore]
    public string SpeedProfileDisplay => Fps switch
    {
        <= 75 => "STABILITY 1  ·  110 MS",
        <= 119 => "STABILITY 2  ·  100 MS",
        <= 179 => "FAST 3  ·  90 MS",
        _ => "FAST 4  ·  80 MS"
    };

    public int CalibrationVersion { get; set; }

    public int WishNumber
    {
        get => _wishNumber;
        set => SetField(ref _wishNumber, Math.Clamp(value, 1, 14));
    }
}

public sealed class AppSettings
{
    public bool SetupComplete { get; set; }
    public bool ViGEmInstallPrompted { get; set; }
    public int ActivationModeVersion { get; set; }
    public int DesyncReliabilityVersion { get; set; }
    public int DesyncSelectionVersion { get; set; }
    public int SwordHeavyBindingVersion { get; set; }
    public int ControllerOutputBindingVersion { get; set; }
    public int SeparateInputEnableVersion { get; set; }
    public int ControllerImplicitEnableVersion { get; set; }
    public GameBindings GameBindings { get; set; } = new();
    public ControllerOutputBindings ControllerBindings { get; set; } = new();
    public ControllerTimingSettings ControllerTimings { get; set; } = new();
    public FortniteBindings FortniteBindings { get; set; } = new();
    public TimingSettings Timings { get; set; } = new();
    public WallSettings Wall { get; set; } = new();
    public ObservableCollection<MacroConfig> Macros { get; set; } = [];
}
