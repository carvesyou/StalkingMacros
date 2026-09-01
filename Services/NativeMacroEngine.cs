using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using D2MacroNative.Models;
using Windows.Gaming.Input;

namespace D2MacroNative.Services;

public sealed class NativeMacroEngine : IDisposable
{
    private const int WishWallStartX = 0;
    private const int WishWallStartY = 0;

    private sealed class LoadoutSession
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public bool TapExitWeapon { get; set; }
    }

    private sealed class WallRunState
    {
        // Every wall coordinate is relative to the top-left circle. The player
        // must stand at the calibrated wall position and aim at that circle.
        public int VirtualX { get; set; } = WishWallStartX;
        public int VirtualY { get; set; } = WishWallStartY;
        public int Recoil { get; set; }
        public int ShotIndex { get; set; }
        public double FractionalMouseX { get; set; }
        public double FractionalMouseY { get; set; }
    }

    private sealed class FortniteDragSession(InputBinding edit, InputBinding select)
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public InputBinding Edit { get; } = edit;
        public InputBinding Select { get; } = select;
    }

    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const uint LlkhfInjected = 0x10;
    private const uint LlmhfInjected = 0x01;
    private const int LoadoutReferenceHeight = 1080;
    private const uint ErrorSuccess = 0;
    private const byte ControllerTriggerThreshold = 30;
    private const int ControllerActivationSettleMilliseconds = 120;
    private const int PlayStationButtonBase = 0x100000;
    private const uint JoyReturnButtonsAndPov = 0x000000C0;
    private const uint JoyPovCentered = 0x0000FFFF;
    private static readonly int[] ControllerButtons =
    [
        0x0001, 0x0002, 0x0004, 0x0008, 0x0010, 0x0020, 0x0040, 0x0080,
        0x0100, 0x0200, 0x1000, 0x2000, 0x4000, 0x8000, 0x10000, 0x20000
    ];

    private readonly AppSettings _settings;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly LowLevelMouseProc _mouseProc;
    private readonly object _sync = new();
    private readonly object _controllerLogSync = new();
    private readonly HashSet<string> _pressedInputs = [];
    private readonly HashSet<string> _physicallyHeldInputs = [];
    private readonly HashSet<string> _capturedControllerReleases = [];
    private readonly HashSet<string> _runningSequences = [];
    private readonly Dictionary<string, CancellationTokenSource> _repeatSessions = [];
    private readonly Dictionary<string, bool> _loadoutUseSecondaryNext = [];
    private readonly VirtualControllerService _virtualController = new();
    private volatile bool _virtualControllerOutputActive;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Action<InputBinding>? _captureCallback;
    private bool _capturePrimaryMouse;
    private bool _captureController;
    private CancellationTokenSource? _controllerPollCancellation;
    private bool _controllerProxyStarted;
    private LoadoutSession? _loadoutSession;
    private CancellationTokenSource? _wishWallCancellation;
    private FortniteDragSession? _fortniteDragSession;
    private CancellationTokenSource? _sparrowSlipstreamCancellation;
    private bool _disposed;

    public NativeMacroEngine(AppSettings settings)
    {
        _settings = settings;
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public event Action<MacroConfig, string>? MacroTriggered;
    public event Action<string>? Notice;

    public void InitializeControllerOutput()
    {
        _virtualController.EnsureConnected();
        if (_controllerProxyStarted || _controllerPollCancellation is null) return;
        _controllerProxyStarted = true;
        _ = Task.Run(() => ProxyPhysicalControllerAsync(_controllerPollCancellation.Token));
    }

    public void Start()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        _keyboardHook = SetWindowsHookExKeyboard(WhKeyboardLl, _keyboardProc, moduleHandle, 0);
        _mouseHook = SetWindowsHookExMouse(WhMouseLl, _mouseProc, moduleHandle, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            DisposeHooks();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the native input hooks.");
        }

        _controllerPollCancellation = new CancellationTokenSource();
        _ = Task.Run(() => PollControllersAsync(_controllerPollCancellation.Token));
    }

    public void BeginCapture(Action<InputBinding> callback, bool includePrimaryMouse = false, bool includeController = false)
    {
        lock (_sync)
        {
            _captureCallback = callback;
            _capturePrimaryMouse = includePrimaryMouse;
            _captureController = includeController;
            _pressedInputs.Clear();
        }
    }

    public void CancelCapture()
    {
        lock (_sync)
        {
            _captureCallback = null;
            _capturePrimaryMouse = false;
            _captureController = false;
        }
    }

    private async Task PollControllersAsync(CancellationToken cancellationToken)
    {
        HashSet<int> previousButtons = [];
        HashSet<uint> playStationDevices = [];
        var deviceRefreshCountdown = 0;
        var xInputAvailable = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HashSet<int> currentButtons = [];
                if (xInputAvailable)
                {
                    try
                    {
                        for (uint index = 0; index < 4; index++)
                        {
                            if (XInputGetState(index, out var state) != ErrorSuccess) continue;
                            foreach (var button in ControllerButtons)
                            {
                                if (button == 0x10000 && state.Gamepad.LeftTrigger >= ControllerTriggerThreshold)
                                    currentButtons.Add(button);
                                else if (button == 0x20000 && state.Gamepad.RightTrigger >= ControllerTriggerThreshold)
                                    currentButtons.Add(button);
                                else if (button < 0x10000 && (state.Gamepad.Buttons & button) != 0)
                                    currentButtons.Add(button);
                            }
                        }
                    }
                    catch (DllNotFoundException) { xInputAvailable = false; }
                    catch (EntryPointNotFoundException) { xInputAvailable = false; }
                }

                ReadModernPlayStationButtons(currentButtons);
                if (--deviceRefreshCountdown <= 0)
                {
                    playStationDevices.Clear();
                    for (uint index = 0; index < 16; index++)
                    {
                        var info = CreateJoyInfo();
                        if (joyGetPosEx(index, ref info) != ErrorSuccess) continue;
                        var caps = new JoyCaps();
                        if (joyGetDevCaps(index, ref caps, (uint)Marshal.SizeOf<JoyCaps>()) != ErrorSuccess) continue;
                        var name = caps.ProductName ?? string.Empty;
                        if (name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("DualShock", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("PlayStation", StringComparison.OrdinalIgnoreCase))
                            playStationDevices.Add(index);
                    }
                    deviceRefreshCountdown = 125;
                }

                foreach (var index in playStationDevices)
                    ReadPlayStationButtons(index, currentButtons);

                if (ShouldProcessControllerInput())
                {
                    foreach (var button in currentButtons.Except(previousButtons))
                    {
                        LogControllerEvent($"DOWN 0x{button:X}");
                        ProcessPhysicalInput(InputBinding.Controller(button), true);
                    }
                    foreach (var button in previousButtons.Except(currentButtons))
                    {
                        LogControllerEvent($"UP   0x{button:X}");
                        ProcessPhysicalInput(InputBinding.Controller(button), false);
                    }
                }

                previousButtons = currentButtons;
                await Task.Delay(8, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal application shutdown.
        }
    }

    private async Task ProxyPhysicalControllerAsync(CancellationToken cancellationToken)
    {
        Gamepad? source = null;
        string? sourceIdentity = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_virtualControllerOutputActive)
                {
                    await Task.Delay(2, cancellationToken);
                    continue;
                }

                if (source is null || !Gamepad.Gamepads.Contains(source))
                {
                    source = FindPhysicalGamepad();
                    if (source is not null)
                    {
                        var raw = RawGameController.FromGameController(source);
                        sourceIdentity = raw is null
                            ? "unknown gamepad"
                            : $"VID 0x{raw.HardwareVendorId:X4} PID 0x{raw.HardwareProductId:X4}";
                        LogControllerEvent($"PROXY source {sourceIdentity}");
                    }
                }

                if (source is not null)
                {
                    var reading = source.GetCurrentReading();
                    _virtualController.SubmitState(
                        (ushort)reading.Buttons,
                        ToTrigger(reading.LeftTrigger),
                        ToTrigger(reading.RightTrigger),
                        ToAxis(reading.LeftThumbstickX),
                        ToAxis(reading.LeftThumbstickY),
                        ToAxis(reading.RightThumbstickX),
                        ToAxis(reading.RightThumbstickY));
                }

                await Task.Delay(4, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal application shutdown.
        }
        catch (Exception ex)
        {
            LogControllerEvent($"PROXY failed ({sourceIdentity ?? "no source"}): {ex.Message}");
            Notice?.Invoke($"Controller proxy failed: {ex.Message}");
        }
    }

    private static Gamepad? FindPhysicalGamepad()
    {
        Gamepad? fallback = null;
        foreach (var gamepad in Gamepad.Gamepads)
        {
            var raw = RawGameController.FromGameController(gamepad);
            if (raw is null)
            {
                fallback ??= gamepad;
                continue;
            }

            // ViGEm's default wired Xbox 360 target. Never proxy the virtual
            // output back into itself.
            if (raw.HardwareVendorId == 0x045E && raw.HardwareProductId == 0x028E)
                continue;
            if (raw.HardwareVendorId == 0x054C)
                return gamepad;
            fallback ??= gamepad;
        }
        return fallback;
    }

    private static byte ToTrigger(double value) =>
        (byte)Math.Clamp((int)Math.Round(value * byte.MaxValue), 0, byte.MaxValue);

    private static short ToAxis(double value) => value <= -1
        ? short.MinValue
        : (short)Math.Clamp((int)Math.Round(value * short.MaxValue), short.MinValue, short.MaxValue);

    private static JoyInfoEx CreateJoyInfo() => new()
    {
        Size = (uint)Marshal.SizeOf<JoyInfoEx>(),
        Flags = JoyReturnButtonsAndPov,
        Pov = JoyPovCentered
    };

    private static bool ReadModernPlayStationButtons(HashSet<int> buttons)
    {
        var found = false;
        try
        {
            foreach (var controller in RawGameController.RawGameControllers)
            {
                if (controller.HardwareVendorId != 0x054C) continue;
                found = true;
                var buttonStates = new bool[controller.ButtonCount];
                var switchStates = new GameControllerSwitchPosition[controller.SwitchCount];
                var axisStates = new double[controller.AxisCount];
                controller.GetCurrentReading(buttonStates, switchStates, axisStates);
                for (var index = 0; index < Math.Min(buttonStates.Length, 32); index++)
                    if (buttonStates[index]) buttons.Add(KeyNames.NormalizeControllerCode(PlayStationButtonBase + index));

                foreach (var position in switchStates)
                {
                    if (position is GameControllerSwitchPosition.Up or GameControllerSwitchPosition.UpRight or GameControllerSwitchPosition.UpLeft)
                        buttons.Add(KeyNames.NormalizeControllerCode(PlayStationButtonBase + 0x100));
                    if (position is GameControllerSwitchPosition.Right or GameControllerSwitchPosition.UpRight or GameControllerSwitchPosition.DownRight)
                        buttons.Add(KeyNames.NormalizeControllerCode(PlayStationButtonBase + 0x101));
                    if (position is GameControllerSwitchPosition.Down or GameControllerSwitchPosition.DownRight or GameControllerSwitchPosition.DownLeft)
                        buttons.Add(KeyNames.NormalizeControllerCode(PlayStationButtonBase + 0x102));
                    if (position is GameControllerSwitchPosition.Left or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.DownLeft)
                        buttons.Add(KeyNames.NormalizeControllerCode(PlayStationButtonBase + 0x103));
                }
            }
        }
        catch (COMException)
        {
            // Some Bluetooth stacks briefly invalidate the WinRT controller
            // collection while reconnecting. The legacy fallback runs below.
            return false;
        }
        return found;
    }

    private static void ReadPlayStationButtons(uint index, HashSet<int> buttons)
    {
        var info = CreateJoyInfo();
        if (joyGetPosEx(index, ref info) != ErrorSuccess) return;

        for (var buttonIndex = 0; buttonIndex < 32; buttonIndex++)
            if ((info.Buttons & (1u << buttonIndex)) != 0)
                buttons.Add(KeyNames.NormalizeControllerCode(PlayStationButtonBase + buttonIndex));

        if (info.Pov == JoyPovCentered) return;
        if (info.Pov >= 31500 || info.Pov <= 4500) buttons.Add(KeyNames.NormalizeControllerCode(PlayStationButtonBase + 0x100));
        if (info.Pov is >= 4500 and <= 13500) buttons.Add(KeyNames.NormalizeControllerCode(PlayStationButtonBase + 0x101));
        if (info.Pov is >= 13500 and <= 22500) buttons.Add(KeyNames.NormalizeControllerCode(PlayStationButtonBase + 0x102));
        if (info.Pov is >= 22500 and <= 31500) buttons.Add(KeyNames.NormalizeControllerCode(PlayStationButtonBase + 0x103));
    }

    private bool ShouldProcessControllerInput()
    {
        lock (_sync)
            return (_captureCallback is not null && _captureController)
                || (_runningSequences.Count == 0
                    && _settings.Macros.Any(macro => macro.ControllerIsEnabled && macro.ControllerActivation is not null));
    }

    public Task PreviewAsync(MacroConfig macro) => macro.Kind == MacroKind.LoadoutSpam
        ? PreviewLoadoutTilesAsync(macro)
        : RunSequenceAsync(macro, true);

    public Task PreviewControllerAsync(MacroConfig macro) => macro.Kind == MacroKind.LoadoutSpam
        ? PreviewLoadoutTilesAsync(macro)
        : RunSequenceAsync(macro, true);

    public void StopMacro(MacroConfig macro)
    {
        StopRepeating(macro);
        if (macro.Kind == MacroKind.LoadoutSpam) StopLoadout(false);
        if (macro.Kind == MacroKind.WishWall) StopWishWall();
        if (macro.Kind == MacroKind.FortniteDragEdit) StopFortniteDrag();
        if (macro.Kind == MacroKind.SparrowSlipstream) StopSparrowSlipstream();
    }

    public void Panic()
    {
        StopAllRepeating();
        StopLoadout(false);
        StopWishWall();
        StopFortniteDrag();
        StopSparrowSlipstream();
        lock (_sync) _pressedInputs.Clear();
        ReleaseControllerInputs();
        _virtualControllerOutputActive = false;
        ReleaseConfiguredInputs();
        Notice?.Invoke("Panic stop completed - every controlled input was released.");
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        if ((data.Flags & LlkhfInjected) != 0)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var message = wParam.ToInt32();
        var isDown = message is WmKeyDown or WmSysKeyDown;
        var isUp = message is WmKeyUp or WmSysKeyUp;
        if (!isDown && !isUp) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var binding = new InputBinding { Device = InputDevice.Keyboard, VirtualKey = (int)data.VirtualKey };
        return ProcessPhysicalInput(binding, isDown) ? (IntPtr)1 : CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        var message = wParam.ToInt32();
        var device = message switch
        {
            WmLButtonDown or WmLButtonUp => InputDevice.MouseLeft,
            WmRButtonDown or WmRButtonUp => InputDevice.MouseRight,
            WmXButtonDown or WmXButtonUp => InputDevice.MouseX1,
            _ => (InputDevice?)null
        };
        if (device is null)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
        if ((data.Flags & LlmhfInjected) != 0)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        if (device == InputDevice.MouseX1)
        {
            var xButton = (data.MouseData >> 16) & 0xFFFF;
            device = xButton == 1 ? InputDevice.MouseX1 : InputDevice.MouseX2;
        }

        if (device is InputDevice.MouseLeft or InputDevice.MouseRight)
        {
            lock (_sync)
                if ((_captureCallback is null || !_capturePrimaryMouse)
                    && !_settings.Macros.Any(macro => macro.IsEnabled && macro.Activation.Device == device))
                    return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        var binding = InputBinding.Mouse(device.Value);
        var isDown = message is WmLButtonDown or WmRButtonDown or WmXButtonDown;
        return ProcessPhysicalInput(binding, isDown)
            ? (IntPtr)1
            : CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool ProcessPhysicalInput(InputBinding binding, bool isDown)
    {
        Action<InputBinding>? capture = null;
        MacroConfig? macro;
        var token = $"{binding.Device}:{binding.VirtualKey}";

        lock (_sync)
        {
            // Keep physical state separate from macro activation de-bouncing.
            // Normal sequence cleanup uses this to avoid sending an artificial
            // key-up for inputs the player is still physically holding (most
            // notably W while landing after a skate).
            if (isDown)
                _physicallyHeldInputs.Add(token);
            else
                _physicallyHeldInputs.Remove(token);

            if (!isDown && _capturedControllerReleases.Remove(token))
            {
                _pressedInputs.Remove(token);
                return true;
            }
            if (!isDown) _pressedInputs.Remove(token);

            if (isDown && _captureCallback is not null)
            {
                capture = _captureCallback;
                _captureCallback = null;
                _capturePrimaryMouse = false;
                _captureController = false;
                _pressedInputs.Add(token);
                if (binding.Device == InputDevice.Controller)
                    _capturedControllerReleases.Add(token);
                macro = null;
            }
            else
            {
                macro = binding.Device == InputDevice.Controller
                    ? _settings.Macros.FirstOrDefault(item => item.ControllerIsEnabled
                        && item.ControllerActivation?.SameAs(binding) == true)
                    : _settings.Macros.FirstOrDefault(item => item.IsEnabled
                        && item.Activation.SameAs(binding));
                if (macro is null) return false;

                if (isDown && !_pressedInputs.Add(token)) return true;
            }
        }

        if (capture is not null)
        {
            capture(binding.Clone());
            return true;
        }

        if (macro is null) return true;
        if (binding.Device == InputDevice.Controller && macro.ActivationMode == MacroActivationMode.OneTime)
        {
            // A controller button is activation only. Once it is released and
            // Destiny has settled, run the exact same K&M sequence and timing
            // used by the matching Keyboard card—there is no second macro path.
            if (!isDown)
            {
                LogControllerEvent($"MATCH {macro.Name} on {binding.DisplayName} release — exact keyboard sequence");
                Notice?.Invoke($"{macro.Name}: controller activation received");
                _ = RunSequenceAsync(macro, false, ControllerActivationSettleMilliseconds);
            }
            return true;
        }
        if (macro.Kind == MacroKind.FortniteDragEdit)
        {
            HandleFortniteDragActivation(macro, isDown);
        }
        else if (macro.Kind == MacroKind.LoadoutSpam)
        {
            HandleDesyncActivation(macro, isDown);
        }
        else if (macro.Kind == MacroKind.SparrowSlipstream)
        {
            if (isDown) StartSparrowSlipstream(macro);
            else StopSparrowSlipstream();
        }
        else if (macro.Kind == MacroKind.WishWall && isDown)
        {
            bool isRunning;
            lock (_sync) isRunning = _runningSequences.Contains(macro.Id);
            if (isRunning)
                StopWishWall();
            else
                _ = RunSequenceAsync(macro, false);
        }
        else
        {
            switch (macro.ActivationMode)
            {
                case MacroActivationMode.OneTime when isDown:
                    _ = RunSequenceAsync(macro, false);
                    break;
                case MacroActivationMode.Hold when isDown:
                    StartRepeating(macro);
                    break;
                case MacroActivationMode.Hold:
                    StopRepeating(macro);
                    break;
                case MacroActivationMode.Toggle when isDown:
                    ToggleRepeating(macro);
                    break;
            }
        }
        return true;
    }

    private void HandleDesyncActivation(MacroConfig macro, bool isDown)
    {
        switch (macro.ActivationMode)
        {
            case MacroActivationMode.OneTime when isDown:
                _ = RunSequenceAsync(macro, false);
                break;
            case MacroActivationMode.Hold when isDown:
                StartLoadout(macro);
                break;
            case MacroActivationMode.Hold:
                StopLoadout(true);
                break;
            case MacroActivationMode.Toggle when isDown:
                if (IsLoadoutRunning()) StopLoadout(true);
                else StartLoadout(macro);
                break;
        }
    }

    private void HandleFortniteDragActivation(MacroConfig macro, bool isDown)
    {
        if (isDown) StartFortniteDrag(macro);
        else StopFortniteDrag();
    }

    private void ToggleRepeating(MacroConfig macro)
    {
        bool isRunning;
        lock (_sync) isRunning = _repeatSessions.ContainsKey(macro.Id);
        if (isRunning) StopRepeating(macro);
        else StartRepeating(macro);
    }

    private void StartRepeating(MacroConfig macro)
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            if (_repeatSessions.ContainsKey(macro.Id)) return;
            cancellation = new CancellationTokenSource();
            _repeatSessions[macro.Id] = cancellation;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await RunSequenceAsync(macro, false);
                    var interval = macro.Kind == MacroKind.FortniteEditSpam
                        ? _settings.Timings.FortniteSpamInterval
                        : 35;
                    await Task.Delay(interval, cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Releasing a Hold bind or pressing a Toggle bind again stops repetition.
            }
            finally
            {
                lock (_sync)
                {
                    if (_repeatSessions.TryGetValue(macro.Id, out var current) && ReferenceEquals(current, cancellation))
                        _repeatSessions.Remove(macro.Id);
                }
                cancellation.Dispose();
            }
        });
    }

    private void StopRepeating(MacroConfig macro)
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (!_repeatSessions.Remove(macro.Id, out cancellation)) return;
        }
        cancellation.Cancel();
        if (macro.Kind == MacroKind.WishWall) StopWishWall();
    }

    private void StopAllRepeating()
    {
        CancellationTokenSource[] cancellations;
        lock (_sync)
        {
            cancellations = _repeatSessions.Values.ToArray();
            _repeatSessions.Clear();
        }
        foreach (var cancellation in cancellations) cancellation.Cancel();
    }

    private void StartSparrowSlipstream(MacroConfig macro)
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            if (_sparrowSlipstreamCancellation is not null
                || _runningSequences.Count > 0
                || _fortniteDragSession is not null
                || _loadoutSession is not null)
                return;

            cancellation = new CancellationTokenSource();
            _sparrowSlipstreamCancellation = cancellation;
        }

        var binds = _settings.GameBindings;
        var ghost = binds.Ghost.Clone();
        var summonVehicle = binds.SummonVehicle.Clone();
        var boost = binds.SparrowBoost.Clone();
        var destabilizer = binds.SparrowDestabilizer.Clone();
        var back = binds.SparrowBack.Clone();
        var left = binds.SparrowLeft.Clone();
        var dodgeLeft = binds.SparrowDodgeLeft.Clone();
        var timing = _settings.Timings;

        MacroTriggered?.Invoke(macro, "Slipstream launch started");
        _ = Task.Run(async () =>
        {
            try
            {
                // Summon from a clean standing start, mount, and build the
                // primary boost needed to carry momentum off the ledge.
                await TapAsync(ghost, 50, cancellation.Token);
                await DelayPreciselyAsync(120, cancellation.Token);
                SetInput(summonVehicle, true);
                await DelayPreciselyAsync(1000, cancellation.Token);
                SetInput(summonVehicle, false);
                await DelayPreciselyAsync(220, cancellation.Token);

                SetInput(boost, true);
                await DelayPreciselyAsync(750, cancellation.Token);
                SetInput(boost, false);
                await DelayPreciselyAsync(25, cancellation.Token);

                // Establish the bottom-left launch angle before entering the
                // post-9.7 pulsed rotation. Steering and vehicle dodge are now
                // separate binds, so never rely on a double-tap of steering.
                SetInput(back, true);
                await DelayPreciselyAsync(240, cancellation.Token);
                SetInput(left, true);
                await DelayPreciselyAsync(120, cancellation.Token);
                SetInput(left, false);
                SetInput(back, false);
                await DelayPreciselyAsync(40, cancellation.Token);

                // The removed build used Task.Delay after every dodge pulse,
                // so the 72 ms pulse duration accumulated on every rotation.
                // Schedule pulse starts against an absolute high-resolution
                // clock instead, frame-aligned to the user's stable FPS cap.
                var rotationMilliseconds = GetFrameAlignedSlipstreamPeriodMilliseconds(
                    timing.SparrowRotationPeriod,
                    timing.SparrowFps);
                var rotationTicks = rotationMilliseconds * Stopwatch.Frequency / 1000.0;
                var nextDodgeDeadline = (double)Stopwatch.GetTimestamp();

                while (!cancellation.IsCancellationRequested)
                {
                    await RunPostPatchSlipstreamRotationAsync(
                        back,
                        destabilizer,
                        dodgeLeft,
                        cancellation.Token);
                    nextDodgeDeadline += rotationTicks;
                    var now = Stopwatch.GetTimestamp();
                    if (nextDodgeDeadline <= now)
                        nextDodgeDeadline = now + rotationTicks;
                    await DelayUntilTimestampAsync(nextDodgeDeadline, cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Releasing the activation bind is the normal stop path.
            }
            catch (Exception ex)
            {
                Notice?.Invoke($"{macro.Name} failed: {ex.Message}");
            }
            finally
            {
                ReleaseSparrowInputs(ghost, summonVehicle, boost, destabilizer, back, left, dodgeLeft);
                lock (_sync)
                {
                    if (ReferenceEquals(_sparrowSlipstreamCancellation, cancellation))
                        _sparrowSlipstreamCancellation = null;
                }
                cancellation.Dispose();
            }
        });
    }

    private static double GetFrameAlignedSlipstreamPeriodMilliseconds(int basePeriodMilliseconds, int fps)
    {
        var frameMilliseconds = 1000.0 / Math.Clamp(fps, 30, 500);
        return Math.Max(frameMilliseconds, Math.Round(basePeriodMilliseconds / frameMilliseconds) * frameMilliseconds);
    }

    private static async Task RunPostPatchSlipstreamRotationAsync(
        InputBinding back,
        InputBinding destabilizer,
        InputBinding dodgeLeft,
        CancellationToken cancellationToken)
    {
        // Update 9.7 changed Sparrow handling. The stable post-patch rhythm is
        // a back-pitch phase, a shorter Destabilizer roll phase, one dedicated
        // Dodge Left pulse, then a neutral window before the next rotation.
        SetInput(back, true);
        await DelayPreciselyAsync(330, cancellationToken);
        SetInput(destabilizer, true);
        await DelayPreciselyAsync(140, cancellationToken);
        await TapAsync(dodgeLeft, 24, cancellationToken);
        await DelayPreciselyAsync(48, cancellationToken);
        SetInput(destabilizer, false);
        SetInput(back, false);
    }

    private void StopSparrowSlipstream()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            cancellation = _sparrowSlipstreamCancellation;
            _sparrowSlipstreamCancellation = null;
        }

        if (cancellation is not null)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        var binds = _settings.GameBindings;
        ReleaseSparrowInputs(
            binds.Ghost,
            binds.SummonVehicle,
            binds.SparrowBoost,
            binds.SparrowDestabilizer,
            binds.SparrowBack,
            binds.SparrowLeft,
            binds.SparrowDodgeLeft);
    }

    private static void ReleaseSparrowInputs(params InputBinding[] bindings)
    {
        var released = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            var key = $"{binding.Device}:{binding.VirtualKey}";
            if (released.Add(key)) SetInput(binding, false);
        }
    }

    private Task RunControllerSequenceAsync(MacroConfig macro, bool preview)
    {
        lock (_sync)
        {
            if (_runningSequences.Count > 0 || _fortniteDragSession is not null) return Task.CompletedTask;
            _runningSequences.Add(macro.Id);
        }

        return Task.Run(async () =>
        {
            try
            {
                LogControllerEvent($"START {macro.Name}");
                _virtualControllerOutputActive = true;
                _virtualController.EnsureConnected();
                switch (macro.Kind)
                {
                    case MacroKind.ShatterSkate:
                    case MacroKind.HunterStrandSkate:
                        await RunControllerShatterAsync();
                        break;
                    case MacroKind.GroundSkate:
                        await RunControllerGroundSkateAsync();
                        break;
                    case MacroKind.WellSkate:
                        await RunControllerWellAsync();
                        break;
                    case MacroKind.GroundWellSkate:
                        await RunControllerGroundWellAsync();
                        break;
                    case MacroKind.StrandSkate:
                        await RunControllerStrandAsync();
                        break;
                    case MacroKind.AcrobaticsSkate:
                        await RunControllerAcrobaticsAsync();
                        break;
                    case MacroKind.BubbleSkate:
                        await RunControllerBubbleAsync();
                        break;
                    default:
                        throw new InvalidOperationException("This macro does not have a Controller sequence.");
                }

                MacroTriggered?.Invoke(macro, preview ? "Controller preview completed" : "Controller sequence completed");
                LogControllerEvent($"DONE  {macro.Name}");
            }
            catch (Exception ex)
            {
                LogControllerEvent($"FAIL  {macro.Name}: {ex.Message}");
                Notice?.Invoke($"{macro.Name} controller sequence failed: {ex.Message}");
            }
            finally
            {
                ReleaseControllerInputs();
                await Task.Delay(20);
                _virtualControllerOutputActive = false;
                lock (_sync) _runningSequences.Remove(macro.Id);
            }
        });
    }

    private void LogControllerEvent(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "D2MacroNative");
            Directory.CreateDirectory(directory);
            var line = $"{DateTime.Now:O} {message}{Environment.NewLine}";
            lock (_controllerLogSync)
                File.AppendAllText(Path.Combine(directory, "controller.log"), line);
        }
        catch
        {
            // Diagnostics must never interrupt controller polling or a macro.
        }
    }

    private async Task RunControllerShatterAsync()
    {
        var binds = _settings.ControllerBindings;
        var timing = _settings.ControllerTimings;
        await Task.Delay(timing.SwordReady);
        await TapControllerFreshAsync(binds.HeavyAttack, timing.ShatterHeavyHold);
        await TapControllerAsync(binds.Jump, timing.TapHold);
        await Task.Delay(timing.ShatterAirMoveGap);
        await TapControllerAsync(binds.AirMove, timing.TapHold);
        await FinishControllerAsync();
    }

    private async Task RunControllerGroundSkateAsync()
    {
        var binds = _settings.ControllerBindings;
        var timing = _settings.ControllerTimings;
        await Task.Delay(timing.SwordReady);
        await TapControllerAsync(binds.Jump, timing.GroundJumpHold);
        await Task.Delay(timing.GroundJumpToLight);
        await TapControllerAsync(binds.LightAttack, timing.GroundLightHold);
        await TapControllerAsync(binds.Jump, timing.GroundSecondJumpHold);
        await Task.Delay(timing.GroundDiveGap);
        SetControllerInput(binds.AirMove, true);
        SetControllerInput(binds.Jump, true);
        await Task.Delay(timing.GroundDiveHold);
        SetControllerInput(binds.AirMove, false);
        SetControllerInput(binds.Jump, false);
        await FinishControllerAsync();
    }

    private async Task RunControllerWellAsync()
    {
        var binds = _settings.ControllerBindings;
        var timing = _settings.ControllerTimings;
        await Task.Delay(timing.SwordReady);
        await PressControllerFreshAsync(binds.HeavyAttack);
        await Task.Delay(timing.WellHeavyToJump);
        SetControllerInput(binds.Jump, true);
        await Task.Delay(timing.WellJumpToSuper);
        SetControllerSuper(binds, true);
        await Task.Delay(timing.WellComboHold);
        SetControllerInput(binds.HeavyAttack, false);
        await Task.Delay(15);
        SetControllerInput(binds.Jump, false);
        await Task.Delay(24);
        SetControllerSuper(binds, false);
        await FinishControllerAsync();
    }

    private async Task RunControllerGroundWellAsync()
    {
        var binds = _settings.ControllerBindings;
        var timing = _settings.ControllerTimings;
        await Task.Delay(timing.SwordReady);
        await TapControllerAsync(binds.Jump, timing.TapHold);
        await Task.Delay(timing.GroundWellJumpToLight);
        await TapControllerAsync(binds.LightAttack, timing.TapHold);
        await Task.Delay(timing.GroundWellLightToJump);
        await TapControllerAsync(binds.Jump, timing.TapHold);
        await Task.Delay(timing.GroundWellJumpToSuper);
        await TapControllerSuperAsync(binds, timing.TapHold);
        await FinishControllerAsync();
    }

    private async Task RunControllerStrandAsync()
    {
        var binds = _settings.ControllerBindings;
        var timing = _settings.ControllerTimings;
        await Task.Delay(timing.SwordReady);
        await PressControllerFreshAsync(binds.HeavyAttack);
        await Task.Delay(timing.StrandHeavyToCombo);
        _virtualController.SetTogether(true, binds.Rift, binds.SuperModifier, binds.Super);
        await Task.Delay(timing.StrandComboHold);
        _virtualController.SetTogether(false, binds.Rift, binds.SuperModifier, binds.Super);
        SetControllerInput(binds.HeavyAttack, false);
        await FinishControllerAsync();
    }

    private async Task RunControllerAcrobaticsAsync()
    {
        var binds = _settings.ControllerBindings;
        var timing = _settings.ControllerTimings;
        await Task.Delay(timing.SwordReady);
        await PressControllerFreshAsync(binds.HeavyAttack);
        await Task.Delay(timing.AcrobaticsHeavyToDodge);
        await TapControllerAsync(binds.Dodge, timing.TapHold);
        await Task.Delay(45);
        SetControllerInput(binds.Dodge, true);
        await Task.Delay(timing.AcrobaticsDodgeHold);
        SetControllerInput(binds.Dodge, false);
        SetControllerInput(binds.HeavyAttack, false);
        await FinishControllerAsync();
    }

    private async Task RunControllerBubbleAsync()
    {
        var binds = _settings.ControllerBindings;
        var timing = _settings.ControllerTimings;
        await Task.Delay(timing.SwordReady);
        await PressControllerFreshAsync(binds.HeavyAttack);
        await Task.Delay(timing.BubbleHeavyToJump);
        SetControllerInput(binds.Jump, true);
        await Task.Delay(timing.BubbleJumpToSuper);
        SetControllerSuper(binds, true);
        await Task.Delay(timing.BubbleComboHold);
        SetControllerSuper(binds, false);
        SetControllerInput(binds.Jump, false);
        SetControllerInput(binds.HeavyAttack, false);
        await FinishControllerAsync();
    }

    private async Task FinishControllerAsync()
    {
        var timing = _settings.ControllerTimings;
        await Task.Delay(timing.ExitDelay);
        await TapControllerAsync(_settings.ControllerBindings.ExitWeapon, timing.TapHold);
    }

    private void SetControllerInput(InputBinding binding, bool down) => _virtualController.Set(binding, down);

    private void SetControllerSuper(ControllerOutputBindings binds, bool down)
    {
        SetControllerInput(binds.SuperModifier, down);
        SetControllerInput(binds.Super, down);
    }

    private async Task TapControllerSuperAsync(ControllerOutputBindings binds, int holdMilliseconds)
    {
        SetControllerSuper(binds, true);
        await Task.Delay(holdMilliseconds);
        SetControllerSuper(binds, false);
    }

    private async Task TapControllerAsync(InputBinding binding, int holdMilliseconds)
    {
        SetControllerInput(binding, true);
        await Task.Delay(holdMilliseconds);
        SetControllerInput(binding, false);
    }

    private async Task PressControllerFreshAsync(InputBinding binding)
    {
        SetControllerInput(binding, false);
        await Task.Delay(16);
        SetControllerInput(binding, true);
    }

    private async Task TapControllerFreshAsync(InputBinding binding, int holdMilliseconds)
    {
        await PressControllerFreshAsync(binding);
        await Task.Delay(holdMilliseconds);
        SetControllerInput(binding, false);
    }

    private void ReleaseControllerInputs() => _virtualController.Reset();

    private Task RunSequenceAsync(
        MacroConfig macro,
        bool preview,
        int initialDelayMilliseconds = 0)
    {
        CancellationTokenSource? wallCancellation = null;
        lock (_sync)
        {
            if (_runningSequences.Count > 0 || _fortniteDragSession is not null) return Task.CompletedTask;
            _runningSequences.Add(macro.Id);
            if (macro.Kind == MacroKind.WishWall)
            {
                wallCancellation = new CancellationTokenSource();
                _wishWallCancellation = wallCancellation;
            }
        }

        return Task.Run(async () =>
        {
            try
            {
                if (initialDelayMilliseconds > 0)
                {
                    LogControllerEvent($"COMPAT START {macro.Name} after {initialDelayMilliseconds} ms settle");
                    await Task.Delay(initialDelayMilliseconds);
                }
                var sequenceTiming = _settings.Timings;
                switch (macro.Kind)
                {
                    case MacroKind.ShatterSkate:
                        await RunShatterAsync(sequenceTiming);
                        break;
                    case MacroKind.GroundSkate:
                        await RunGroundSkateAsync(sequenceTiming);
                        break;
                    case MacroKind.HunterStrandSkate:
                        await RunHunterStrandAsync(sequenceTiming);
                        break;
                    case MacroKind.WellSkate:
                        await RunWellAsync(sequenceTiming);
                        break;
                    case MacroKind.GroundWellSkate:
                        await RunGroundWellAsync(sequenceTiming);
                        break;
                    case MacroKind.StrandSkate:
                        await RunStrandAsync(sequenceTiming);
                        break;
                    case MacroKind.AcrobaticsSkate:
                        await RunAcrobaticsAsync(sequenceTiming);
                        break;
                    case MacroKind.BubbleSkate:
                        await RunBubbleAsync(sequenceTiming);
                        break;
                    case MacroKind.LoadoutSpam:
                        await RunLoadoutSelectionsOnceAsync(macro);
                        break;
                    case MacroKind.LoadoutSwap:
                        await RunLoadoutSwapAsync(macro);
                        break;
                    case MacroKind.RocketGrapple:
                        await RunRocketGrappleAsync(sequenceTiming);
                        break;
                    case MacroKind.WishWall:
                        await RunWishWallAsync(wallCancellation!.Token);
                        break;
                    case MacroKind.FortniteEditSpam:
                        await RunFortniteEditSpamPulseAsync();
                        break;
                }
                MacroTriggered?.Invoke(macro, preview ? "Preview completed" : "Native sequence completed");
                if (initialDelayMilliseconds > 0)
                    LogControllerEvent($"COMPAT DONE  {macro.Name}");
            }
            catch (OperationCanceledException) when (macro.Kind == MacroKind.WishWall)
            {
                Notice?.Invoke("Wish Wall stopped.");
            }
            catch (Exception ex)
            {
                Notice?.Invoke($"{macro.Name} failed: {ex.Message}");
            }
            finally
            {
                ReleaseConfiguredInputs(preserveForwardIfPhysicallyHeld: true);
                lock (_sync)
                {
                    _runningSequences.Remove(macro.Id);
                    if (ReferenceEquals(_wishWallCancellation, wallCancellation))
                        _wishWallCancellation = null;
                }
                wallCancellation?.Dispose();
            }
        });
    }

    private async Task RunShatterAsync(TimingSettings timing)
    {
        var keys = _settings.GameBindings;
        var heavyAttack = InputBinding.Mouse(InputDevice.MouseRight);

        await TapAsync(keys.WeaponSwap, timing.TapHold);
        await Task.Delay(timing.ShatterSwordReadyDelay);
        await TapAsync(heavyAttack, timing.TapHold);
        await TapAsync(keys.Jump, timing.TapHold);
        await Task.Delay(timing.ShatterAirMoveGap);
        await TapAsync(keys.AirMove, timing.TapHold);
        await FinishWithExitWeaponAsync(true, timing);
    }

    private async Task RunWellAsync(TimingSettings timing)
    {
        var keys = _settings.GameBindings;
        var heavyAttack = InputBinding.Mouse(InputDevice.MouseRight);

        await TapAsync(keys.WeaponSwap, timing.WellWeaponHold);
        await Task.Delay(timing.WellSwordReadyDelay);
        SetInput(heavyAttack, true);
        await Task.Delay(timing.WellAttackToJumpDelay);
        SetInput(keys.Jump, true);
        await Task.Delay(timing.WellJumpToSuperDelay);
        SetInput(keys.Super, true);
        await Task.Delay(timing.WellComboHold);
        SetInput(heavyAttack, false);
        await Task.Delay(timing.WellJumpReleaseTail);
        SetInput(keys.Jump, false);
        await Task.Delay(timing.WellSuperReleaseTail);
        SetInput(keys.Super, false);

        await FinishWithExitWeaponAsync(true, timing);
    }

    private async Task RunGroundWellAsync(TimingSettings timing)
    {
        var keys = _settings.GameBindings;
        var lightAttack = InputBinding.Mouse(InputDevice.MouseLeft);

        await TapAsync(keys.WeaponSwap, timing.TapHold);
        await Task.Delay(timing.GroundWellSwordReadyDelay);
        await TapAsync(keys.Jump, timing.GroundWellJumpHold);
        await Task.Delay(timing.GroundWellJumpToLightDelay);
        await TapAsync(lightAttack, timing.GroundWellLightAttackHold);
        await Task.Delay(timing.GroundWellLightToJumpDelay);
        await TapAsync(keys.Jump, timing.GroundWellJumpHold);
        await Task.Delay(timing.GroundWellJumpToSuperDelay);
        await TapAsync(keys.Super, timing.GroundWellSuperHold);
        await FinishWithExitWeaponAsync(true, timing);
    }

    private async Task RunHunterStrandAsync(TimingSettings timing)
    {
        // Ensnaring Slam uses the same ledge timing as Shatterdive; the
        // configured Air Move input determines which Hunter dive activates.
        await RunShatterAsync(timing);
    }

    private async Task RunGroundSkateAsync(TimingSettings timing)
    {
        var keys = _settings.GameBindings;
        var lightAttack = InputBinding.Mouse(InputDevice.MouseLeft);

        await TapAsync(keys.WeaponSwap, timing.TapHold);
        await Task.Delay(timing.ShatterSwordReadyDelay);
        await TapAsync(keys.Jump, timing.GroundInitialJumpHold);
        await Task.Delay(timing.GroundJumpToLightDelay);
        await TapAsync(lightAttack, timing.GroundLightAttackHold);
        await TapAsync(keys.Jump, timing.GroundSecondJumpHold);
        await Task.Delay(timing.GroundSecondJumpToShatterdiveDelay);

        SetInput(keys.AirMove, true);
        SetInput(keys.Jump, true);
        await Task.Delay(timing.GroundShatterdiveOverlapHold);
        SetInput(keys.AirMove, false);
        await Task.Delay(timing.GroundJumpReleaseTail);
        SetInput(keys.Jump, false);
        await FinishWithExitWeaponAsync(true, timing);
    }

    private async Task RunStrandAsync(TimingSettings timing)
    {
        var keys = _settings.GameBindings;
        var heavyAttack = InputBinding.Mouse(InputDevice.MouseRight);

        SetInput(keys.WeaponSwap, true);
        await DelayPreciselyAsync(timing.StrandWeaponHold, CancellationToken.None);
        SetInput(keys.WeaponSwap, false);
        await DelayPreciselyAsync(timing.StrandSwordReadyDelay, CancellationToken.None);
        SetInput(heavyAttack, true);
        await DelayPreciselyAsync(timing.StrandHeavyToComboDelay, CancellationToken.None);
        SetInput(keys.Rift, true);
        await DelayPreciselyAsync(timing.StrandSuperToWeavewalkDelay, CancellationToken.None);
        SetInputStatesTogether((keys.Super, true), (heavyAttack, false));
        await DelayPreciselyAsync(timing.StrandComboHold, CancellationToken.None);
        SetInput(keys.Super, false);
        await DelayPreciselyAsync(timing.StrandRiftReleaseTail, CancellationToken.None);
        SetInput(keys.Rift, false);
        await DelayPreciselyAsync(timing.StrandEndDelay, CancellationToken.None);
        // Restore the original Weavewalk tail: once the Strand skate has
        // cleared its Super/Class Ability overlap, tap the saved Air Move
        // binding before returning to the shared exit weapon.
        await TapAsync(keys.AirMove, timing.TapHold);
        await FinishWithExitWeaponAsync(true, timing);
    }

    private async Task RunAcrobaticsAsync(TimingSettings timing)
    {
        var keys = _settings.GameBindings;
        var heavyAttack = InputBinding.Mouse(InputDevice.MouseRight);

        await TapAsync(keys.WeaponSwap, timing.AcrobaticsSwordHold);
        await Task.Delay(timing.AcrobaticsSwordReadyDelay);
        SetInput(heavyAttack, true);
        await Task.Delay(timing.AcrobaticsAttackToDodgeDelay);
        SetInput(keys.Dodge, true);
        await Task.Delay(timing.AcrobaticsDodgeHold);
        SetInput(keys.Dodge, false);
        SetInput(heavyAttack, false);
        await Task.Delay(timing.AcrobaticsExitDelay);
        await TapAsync(keys.WellExitWeapon, timing.AcrobaticsExitHold);
    }

    private async Task RunBubbleAsync(TimingSettings timing)
    {
        var keys = _settings.GameBindings;
        var heavyAttack = InputBinding.Mouse(InputDevice.MouseRight);

        await TapAsync(keys.WeaponSwap, timing.TapHold);
        await Task.Delay(timing.BubbleSwordReadyDelay);
        SetInput(heavyAttack, true);
        await Task.Delay(timing.BubbleAttackToJumpDelay);
        SetInput(keys.Jump, true);
        await Task.Delay(timing.BubbleJumpToSuperDelay);
        SetInput(keys.Super, true);
        await Task.Delay(timing.BubbleComboHold);
        SetInput(keys.Super, false);
        SetInput(keys.Jump, false);
        SetInput(heavyAttack, false);
        await FinishWithExitWeaponAsync(true, timing);
    }

    private async Task RunRocketGrappleAsync(TimingSettings timing)
    {
        var keys = _settings.GameBindings;
        var fire = InputBinding.Mouse(InputDevice.MouseLeft);

        await TapAsync(keys.WeaponSwap, timing.TapHold);
        await DelayPreciselyAsync(timing.RocketWeaponReadyDelay, CancellationToken.None);

        // Port the public AHK sequence literally at the event level: emit a
        // complete no-delay click, immediately emit a complete Grapple tap,
        // then apply the sensitivity-scaled relative mouse correction.
        ClickMouse(fire.Device);
        TapImmediately(keys.Grapple);

        var sensitivity = Math.Max(1.0, timing.RocketSensitivity);
        var recoilCorrection = (int)Math.Round(230.0 / sensitivity, MidpointRounding.AwayFromZero);
        SendWallRelativeMouse(0, recoilCorrection);

    }

    private async Task RunFortniteEditSpamPulseAsync()
    {
        var binds = _settings.FortniteBindings;
        var timing = _settings.Timings;
        await TapAsync(binds.Edit, timing.FortniteTapHold);
        await Task.Delay(timing.FortniteEditToSelectDelay);
        await TapAsync(binds.Select, timing.FortniteTapHold);
    }

    private void StartFortniteDrag(MacroConfig macro)
    {
        FortniteDragSession session;
        lock (_sync)
        {
            if (_fortniteDragSession is not null || _runningSequences.Count > 0) return;
            session = new FortniteDragSession(
                _settings.FortniteBindings.Edit.Clone(),
                _settings.FortniteBindings.Select.Clone());
            _fortniteDragSession = session;
        }

        // Send Edit down synchronously with the activation press. It stays down
        // through selection and is released last so Confirm Edit on Release works.
        try
        {
            SetInput(session.Edit, true);
        }
        catch (Exception ex)
        {
            lock (_sync)
                if (ReferenceEquals(_fortniteDragSession, session)) _fortniteDragSession = null;
            session.Cancellation.Dispose();
            Notice?.Invoke($"{macro.Name} failed: {ex.Message}");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var timing = _settings.Timings;
                await Task.Delay(timing.FortniteEditToSelectDelay, session.Cancellation.Token);
                SetInput(session.Select, true);
                MacroTriggered?.Invoke(macro, "Edit + Select held");
                await Task.Delay(Timeout.InfiniteTimeSpan, session.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Releasing the activation bind completes the drag edit.
            }
            catch (Exception ex)
            {
                Notice?.Invoke($"{macro.Name} failed: {ex.Message}");
            }
            finally
            {
                SetInput(session.Select, false);
                SetInput(session.Edit, false);
                lock (_sync)
                {
                    if (ReferenceEquals(_fortniteDragSession, session))
                        _fortniteDragSession = null;
                }
                session.Cancellation.Dispose();
            }
        });
    }

    private void StopFortniteDrag()
    {
        FortniteDragSession? session;
        lock (_sync)
        {
            session = _fortniteDragSession;
            if (session is not null)
            {
                try { session.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
        if (session is null) return;

        // Releasing Select finishes the drag; releasing Edit last confirms it.
        SetInput(session.Select, false);
        SetInput(session.Edit, false);
    }

    private async Task RunLoadoutSwapAsync(MacroConfig macro)
    {
        var targetWindow = GetForegroundWindow();
        if (targetWindow == IntPtr.Zero || IsOwnProcessWindow(targetWindow))
            throw new InvalidOperationException("Focus Destiny 2 before using Loadout Swapper.");

        var keys = _settings.GameBindings;
        var timing = _settings.Timings;
        var leftClick = InputBinding.Mouse(InputDevice.MouseLeft);
        bool useSecondary;
        lock (_sync)
            useSecondary = _loadoutUseSecondaryNext.TryGetValue(macro.Id, out var next) && next;
        var selectedLoadout = useSecondary ? macro.SelectedLoadoutSecondary : macro.SelectedLoadout;
        Point originalCursor = default;
        var restoreCursor = GetCursorPos(out originalCursor);

        try
        {
            await TapAsync(keys.Inventory, timing.TapHold);
            await Task.Delay(timing.LoadoutInventoryOpenDelay);
            EnsureTargetWindow(targetWindow);

            await TapAsync(keys.OpenLoadouts, timing.TapHold);
            await Task.Delay(timing.LoadoutPanelOpenDelay);
            EnsureTargetWindow(targetWindow);

            var tileCenter = GetLoadoutTileCenter(targetWindow, selectedLoadout);
            if (!SetCursorPos(tileCenter.X, tileCenter.Y))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the loadout cursor move.");

            if (timing.LoadoutMoveDelay > 0)
                await Task.Delay(timing.LoadoutMoveDelay);
            EnsureTargetWindow(targetWindow);
            await TapAsync(leftClick, timing.TapHold);
            await Task.Delay(timing.LoadoutApplyDelay);
            EnsureTargetWindow(targetWindow);

            await TapAsync(keys.CloseMenu, timing.TapHold);
            await Task.Delay(timing.LoadoutCloseDelay);
            EnsureTargetWindow(targetWindow);
            await TapAsync(keys.CloseMenu, timing.TapHold);

            lock (_sync)
                _loadoutUseSecondaryNext[macro.Id] = !useSecondary;
        }
        finally
        {
            SetInput(leftClick, false);
            if (restoreCursor && GetForegroundWindow() == targetWindow)
                SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private async Task RunWishWallAsync(CancellationToken cancellationToken)
    {
        var targetWindow = GetForegroundWindow();
        if (targetWindow == IntPtr.Zero || IsOwnProcessWindow(targetWindow))
            throw new InvalidOperationException("Focus Destiny 2 while aiming at the Wall of Wishes before using this macro.");

        var wall = _settings.Wall;
        var stages = WishWallPatterns.Get(wall.WishNumber);
        var state = new WallRunState();
        var speedProfile = GetWishWallSpeedProfile(wall.Fps);
        var cycleDelay = GetWishWallCycleDelay(speedProfile);
        var reload = InputBinding.Keyboard(System.Windows.Input.Key.R);

        for (var stageIndex = 0; stageIndex < stages.Count; stageIndex++)
        {
            await ShootWishStageAsync(targetWindow, stages[stageIndex], state, wall.Sensitivity,
                speedProfile, cycleDelay, cancellationToken);
            if (wall.WishNumber == 4 && stageIndex < stages.Count - 1)
            {
                await TapAsync(reload, _settings.Timings.TapHold, cancellationToken);
                await DelayPreciselyAsync(2100, cancellationToken);
            }
        }

        await TapAsync(reload, _settings.Timings.TapHold, cancellationToken);
        await MoveWallMouseAsync(targetWindow, 515, 310 + state.Recoil, false, state,
            wall.Sensitivity, cycleDelay, cancellationToken);
        await SubmitWishAsync(targetWindow, cancellationToken);
    }

    private async Task SubmitWishAsync(IntPtr targetWindow, CancellationToken cancellationToken)
    {
        // The player starts on the activation plate. Move toward the wall to
        // leave it, then step backward onto it again so Destiny submits the
        // completed pattern. These are the timings used by wall_menu4.ahk.
        if (GetForegroundWindow() != targetWindow)
            throw new OperationCanceledException("Destiny 2 lost focus.", cancellationToken);

        var forward = _settings.GameBindings.Forward;
        var backward = InputBinding.Keyboard(System.Windows.Input.Key.S);

        SetInput(forward, true);
        try
        {
            await Task.Delay(400, cancellationToken);
        }
        finally
        {
            SetInput(forward, false);
        }

        await Task.Delay(600, cancellationToken);
        if (GetForegroundWindow() != targetWindow)
            throw new OperationCanceledException("Destiny 2 lost focus.", cancellationToken);

        SetInput(backward, true);
        try
        {
            await Task.Delay(450, cancellationToken);
        }
        finally
        {
            SetInput(backward, false);
        }
    }

    private async Task ShootWishStageAsync(IntPtr targetWindow, int[] pattern, WallRunState state,
        double sensitivity, int speedProfile, double cycleDelay, CancellationToken cancellationToken)
    {
        foreach (var symbol in pattern)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shoot = symbol != 0;
            var (targetX, targetY) = GetWishTarget(symbol, state.Recoil);
            await MoveWallMouseAsync(targetWindow, targetX, targetY, shoot, state,
                sensitivity, cycleDelay, cancellationToken);

            if (shoot)
            {
                state.Recoil += speedProfile switch
                {
                    1 => 4,
                    2 => 3 + (state.ShotIndex % 7 is 0 or 1 or 2 ? 1 : 0),
                    _ => 2 + (state.ShotIndex % 7 != 6 ? 1 : 0)
                };
                state.ShotIndex++;
            }
            else if (state.Recoil % 2 == 0)
            {
                state.Recoil--;
            }
        }
    }

    private async Task MoveWallMouseAsync(IntPtr targetWindow, int targetX, int targetY, bool shoot,
        WallRunState state, double sensitivity, double cycleDelay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GetForegroundWindow() != targetWindow)
            throw new OperationCanceledException("Destiny 2 lost focus.", cancellationToken);

        var scale = sensitivity / 6.0;
        var exactX = ((targetX - state.VirtualX) / scale) + state.FractionalMouseX;
        var exactY = ((targetY - state.VirtualY) / scale) + state.FractionalMouseY;
        var relativeX = (int)Math.Round(exactX, MidpointRounding.AwayFromZero);
        var relativeY = (int)Math.Round(exactY, MidpointRounding.AwayFromZero);
        state.FractionalMouseX = exactX - relativeX;
        state.FractionalMouseY = exactY - relativeY;

        // wall_menu4.ahk uses mouse_event for relative look movement. Keeping
        // this path separate from the other SendInput macros reproduces those
        // raw counts while the fractional remainder prevents sensitivity drift.
        SendWallRelativeMouse(relativeX, relativeY);
        state.VirtualX = targetX;
        state.VirtualY = targetY;

        // The reference macro waits the complete cycle after every movement.
        // Do not use a cumulative deadline here: if Windows stalls one move,
        // catching up would shorten the next wall-cell settle and drop shots.
        await DelayPreciselyAsync(cycleDelay, cancellationToken);
        if (shoot)
            ClickMouse(InputDevice.MouseLeft);
    }

    private static (int X, int Y) GetWishTarget(int symbol, int recoil) => symbol switch
    {
        0 => (515, 310 + recoil),
        1 => (0, recoil),
        2 => (250, recoil),
        3 => (515, recoil),
        4 => (770, recoil),
        5 => (1005, 20 + recoil),
        6 => (0, 220 + recoil),
        7 => (250, 220 + recoil),
        8 => (515, 250 + recoil),
        9 => (770, 250 + recoil),
        10 => (1005, 250 + recoil),
        11 => (0, 480 + recoil),
        12 => (250, 480 + recoil),
        13 => (515, 480 + recoil),
        14 => (770, 480 + recoil),
        15 => (1005, 480 + recoil),
        16 => (0, 720 + recoil),
        17 => (250, 720 + recoil),
        18 => (515, 720 + recoil),
        19 => (770, 720 + recoil),
        20 => (1005, 720 + recoil),
        _ => throw new InvalidOperationException($"Unknown Wall of Wishes symbol {symbol}.")
    };

    private static int GetWishWallSpeedProfile(int fps)
    {
        // wall_menu4.ahk exposes four tested profiles. Its published setup
        // recommends profiles 1/2 around 60 FPS and 3/4 around 140+ FPS.
        return Math.Clamp(fps, 30, 500) switch
        {
            <= 75 => 1,
            <= 119 => 2,
            <= 179 => 3,
            _ => 4
        };
    }

    private static int GetWishWallCycleDelay(int speedProfile) => speedProfile switch
    {
        1 => 110,
        2 => 100,
        3 => 90,
        _ => 80
    };

    private static async Task DelayPreciselyAsync(double milliseconds, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (milliseconds * Stopwatch.Frequency / 1000.0);
        await DelayUntilTimestampAsync(deadline, cancellationToken);
    }

    private static async Task DelayUntilTimestampAsync(double deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingMilliseconds = (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remainingMilliseconds <= 0) return;

            if (remainingMilliseconds > 3)
            {
                await Task.Delay(Math.Max(1, (int)Math.Floor(remainingMilliseconds - 2)), cancellationToken);
                continue;
            }

            Thread.SpinWait(64);
        }
    }

    private void StopWishWall()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            cancellation = _wishWallCancellation;
            _wishWallCancellation = null;
        }
        cancellation?.Cancel();
    }

    private bool IsLoadoutRunning()
    {
        lock (_sync) return _loadoutSession is not null;
    }

    private async Task RunLoadoutSelectionsOnceAsync(MacroConfig macro)
    {
        var targetWindow = GetForegroundWindow();
        if (targetWindow == IntPtr.Zero || IsOwnProcessWindow(targetWindow))
            throw new InvalidOperationException("Focus the Destiny 2 Loadouts page before running Desync once.");

        Point originalCursor = default;
        var restoreCursor = GetCursorPos(out originalCursor);
        try
        {
            var tileCenters = GetLoadoutTileCenters(targetWindow, macro);
            await ClickLoadoutTilesAsync(targetWindow, tileCenters, CancellationToken.None);
            await FinishWithExitWeaponAsync(false);
        }
        finally
        {
            SetInput(InputBinding.Mouse(InputDevice.MouseLeft), false);
            if (restoreCursor && GetForegroundWindow() == targetWindow)
                SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private void StartLoadout(MacroConfig macro)
    {
        var targetWindow = GetForegroundWindow();
        if (targetWindow == IntPtr.Zero || IsOwnProcessWindow(targetWindow))
        {
            Notice?.Invoke("Focus the Destiny 2 Loadouts page, then activate the Desync bind.");
            return;
        }

        Point[] tileCenters;
        Point originalCursor;
        try
        {
            tileCenters = GetLoadoutTileCenters(targetWindow, macro);
            if (!GetCursorPos(out originalCursor))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the current cursor position.");
        }
        catch (Exception ex)
        {
            Notice?.Invoke($"{macro.Name} failed: {ex.Message}");
            return;
        }

        LoadoutSession session;
        lock (_sync)
        {
            if (_loadoutSession is not null) return;
            session = new LoadoutSession();
            _loadoutSession = session;
        }
        var cancellation = session.Cancellation;

        MacroTriggered?.Invoke(macro, "Four-loadout loop started");
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                    await ClickLoadoutTilesAsync(targetWindow, tileCenters, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Releasing the bind, changing focus or using Panic intentionally stops the loop.
            }
            catch (Exception ex)
            {
                Notice?.Invoke($"{macro.Name} failed: {ex.Message}");
            }
            finally
            {
                SetInput(InputBinding.Mouse(InputDevice.MouseLeft), false);
                SetCursorPos(originalCursor.X, originalCursor.Y);
                lock (_sync)
                {
                    if (ReferenceEquals(_loadoutSession, session))
                        _loadoutSession = null;
                }

                if (session.TapExitWeapon && GetForegroundWindow() == targetWindow)
                {
                    try
                    {
                        await FinishWithExitWeaponAsync(false);
                    }
                    catch (Exception ex)
                    {
                        Notice?.Invoke($"{macro.Name} exit weapon failed: {ex.Message}");
                    }
                }
                cancellation.Dispose();
            }
        });
    }

    private void StopLoadout(bool tapExitWeapon)
    {
        LoadoutSession? session;
        lock (_sync)
        {
            session = _loadoutSession;
            if (session is null) return;
            session.TapExitWeapon |= tapExitWeapon;
            _loadoutSession = null;
        }
        session.Cancellation.Cancel();
    }

    private async Task FinishWithExitWeaponAsync(bool releaseMovementInputs, TimingSettings? timingOverride = null)
    {
        var timing = timingOverride ?? _settings.Timings;
        // A held physical W must survive both cleanup stages. Previously this
        // inner release sent W-up before the final sequence cleanup could
        // preserve it, causing movement to stop when the player landed.
        if (releaseMovementInputs) ReleaseConfiguredInputs(preserveForwardIfPhysicallyHeld: true);
        if (timing.MacroExitDelay > 0)
            await Task.Delay(timing.MacroExitDelay);
        await TapAsync(_settings.GameBindings.WellExitWeapon, timing.TapHold);
    }

    private async Task PreviewLoadoutTilesAsync(MacroConfig macro)
    {
        var targetWindow = GetForegroundWindow();
        if (targetWindow == IntPtr.Zero || IsOwnProcessWindow(targetWindow))
        {
            Notice?.Invoke("Focus the Loadouts page before testing the four selected loadouts.");
            return;
        }

        Point originalCursor = default;
        var restoreCursor = GetCursorPos(out originalCursor);
        try
        {
            var tileCenters = GetLoadoutTileCenters(targetWindow, macro);
            await ClickLoadoutTilesAsync(targetWindow, tileCenters, CancellationToken.None);
            MacroTriggered?.Invoke(macro, "Preview clicked all four selected loadouts");
        }
        catch (Exception ex)
        {
            Notice?.Invoke($"{macro.Name} failed: {ex.Message}");
        }
        finally
        {
            SetInput(InputBinding.Mouse(InputDevice.MouseLeft), false);
            if (restoreCursor) SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private async Task ClickLoadoutTilesAsync(IntPtr targetWindow, Point[] tileCenters, CancellationToken cancellationToken)
    {
        var leftClick = InputBinding.Mouse(InputDevice.MouseLeft);
        foreach (var point in tileCenters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != targetWindow)
                throw new OperationCanceledException("The target window lost focus.", cancellationToken);

            // Finish the previous click before moving. Then confirm that the
            // cursor reached and remained on the next tile before sending a
            // complete down/up click pair. This prevents movement and click
            // events from being merged into the same game frame.
            SetInput(leftClick, false);
            await PositionLoadoutCursorAsync(targetWindow, point, cancellationToken);
            await TapAsync(leftClick, _settings.Timings.DesyncClickHold, cancellationToken);
            SetInput(leftClick, false);

            if (_settings.Timings.LoadoutInterval > 0)
                await Task.Delay(_settings.Timings.LoadoutInterval, cancellationToken);
        }
    }

    private async Task PositionLoadoutCursorAsync(IntPtr targetWindow, Point point, CancellationToken cancellationToken)
    {
        var fps = Math.Clamp(_settings.Wall.Fps, 30, 500);
        var twoFrameDelay = (int)Math.Ceiling(2000.0 / fps);
        var settleDelay = Math.Max(_settings.Timings.LoadoutMoveDelay, twoFrameDelay);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != targetWindow)
                throw new OperationCanceledException("The target window lost focus.", cancellationToken);
            if (!SetCursorPos(point.X, point.Y))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected a cursor move.");

            await Task.Delay(settleDelay, cancellationToken);
            if (GetForegroundWindow() != targetWindow)
                throw new OperationCanceledException("The target window lost focus.", cancellationToken);
            if (!GetCursorPos(out var actual))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not confirm the loadout cursor position.");
            if (actual.X == point.X && actual.Y == point.Y)
                return;
        }

        throw new InvalidOperationException("The cursor did not remain on the selected loadout tile.");
    }

    private Point[] GetLoadoutTileCenters(IntPtr targetWindow, MacroConfig macro) =>
    [
        GetLoadoutTileCenter(targetWindow, macro.SelectedLoadout),
        GetLoadoutTileCenter(targetWindow, macro.SelectedLoadoutSecondary),
        GetLoadoutTileCenter(targetWindow, macro.SelectedLoadoutTertiary),
        GetLoadoutTileCenter(targetWindow, macro.SelectedLoadoutQuaternary)
    ];

    private Point GetLoadoutTileCenter(IntPtr targetWindow, int loadoutNumber)
    {
        if (!GetClientRect(targetWindow, out var clientRect))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the target window size.");

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("The target window has no usable client area.");

        var origin = new Point { X = 0, Y = 0 };
        if (!ClientToScreen(targetWindow, ref origin))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not locate the target window.");

        var timing = _settings.Timings;
        var scale = height / (double)LoadoutReferenceHeight;
        var index = Math.Clamp(loadoutNumber, 1, 20) - 1;
        var row = index / 4;
        var column = index % 4;
        var referenceX = timing.LoadoutFirstTileX + (column * timing.LoadoutTileSpacing);
        var referenceY = timing.LoadoutTopRowY + (row * timing.LoadoutTileSpacing);
        var screenX = origin.X + (int)Math.Round(referenceX * scale);
        var screenY = origin.Y + (int)Math.Round(referenceY * scale);
        var minimumX = origin.X;
        var maximumX = origin.X + width - 1;
        var minimumY = origin.Y;
        var maximumY = origin.Y + height - 1;
        return new Point
        {
            X = Math.Clamp(screenX, minimumX, maximumX),
            Y = Math.Clamp(screenY, minimumY, maximumY)
        };
    }

    private static void EnsureTargetWindow(IntPtr targetWindow)
    {
        if (GetForegroundWindow() != targetWindow)
            throw new InvalidOperationException("Destiny 2 lost focus, so the loadout swap was stopped.");
    }

    private static bool IsOwnProcessWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static async Task TapAsync(InputBinding binding, int holdMilliseconds, CancellationToken cancellationToken = default)
    {
        SetInput(binding, true);
        try
        {
            await Task.Delay(holdMilliseconds, cancellationToken);
        }
        finally
        {
            SetInput(binding, false);
        }
    }

    private void ReleaseConfiguredInputs(bool preserveForwardIfPhysicallyHeld = false)
    {
        var bindings = _settings.GameBindings;
        foreach (var binding in new[]
                 {
                     bindings.Forward, bindings.Jump, bindings.AirMove, bindings.Super, bindings.Rift,
                     bindings.Dodge, bindings.Grapple, bindings.HeavyAttack, bindings.WeaponSwap, bindings.WellExitWeapon,
                     bindings.Inventory, bindings.OpenLoadouts, bindings.CloseMenu,
                     bindings.Ghost, bindings.SummonVehicle, bindings.SparrowBoost,
                     bindings.SparrowDestabilizer, bindings.SparrowBack, bindings.SparrowLeft,
                     bindings.SparrowDodgeLeft
                  })
        {
            if (preserveForwardIfPhysicallyHeld && binding.SameAs(bindings.Forward) && IsPhysicallyHeld(binding))
                continue;
            SetInput(binding, false);
        }

        SetInput(_settings.FortniteBindings.Edit, false);
        SetInput(_settings.FortniteBindings.Select, false);

        // Shatterskate always uses a physical right-click, independent of the
        // configurable heavy-attack binding used by the other macros.
        SetInput(InputBinding.Mouse(InputDevice.MouseLeft), false);
        SetInput(InputBinding.Mouse(InputDevice.MouseRight), false);
    }

    private bool IsPhysicallyHeld(InputBinding binding)
    {
        var token = $"{binding.Device}:{binding.VirtualKey}";
        lock (_sync) return _physicallyHeldInputs.Contains(token);
    }

    private static void SetInput(InputBinding binding, bool down)
    {
        if (binding.Device == InputDevice.Keyboard) SendKeyboard(binding.VirtualKey, down);
        else SendMouse(binding.Device, down);
    }

    private static void SetInputsTogether(bool down, params InputBinding[] bindings)
    {
        var inputs = bindings.Select(binding => CreateNativeInput(binding, down)).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected a synchronized input batch.");
    }

    private static void SetInputStatesTogether(params (InputBinding Binding, bool Down)[] states)
    {
        var inputs = states.Select(state => CreateNativeInput(state.Binding, state.Down)).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected a synchronized mixed-state input batch.");
    }

    private static void TapImmediately(InputBinding binding)
    {
        SetInputStatesTogether((binding, true), (binding, false));
    }

    private static Input CreateNativeInput(InputBinding binding, bool down)
    {
        if (binding.Device == InputDevice.Keyboard)
        {
            const uint inputKeyboard = 1;
            const uint keyeventfExtendedkey = 0x0001;
            const uint keyeventfKeyup = 0x0002;
            const uint keyeventfScancode = 0x0008;
            var scanCode = (ushort)MapVirtualKey((uint)binding.VirtualKey, 0);
            var flags = keyeventfScancode | (down ? 0u : keyeventfKeyup);
            if (IsExtendedKey(binding.VirtualKey)) flags |= keyeventfExtendedkey;

            return new Input
            {
                Type = inputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = scanCode == 0 ? (ushort)binding.VirtualKey : (ushort)0,
                        ScanCode = scanCode,
                        Flags = scanCode == 0 ? (down ? 0u : keyeventfKeyup) : flags
                    }
                }
            };
        }

        const uint inputMouse = 0;
        var mouseFlags = binding.Device switch
        {
            InputDevice.MouseLeft => down ? 0x0002u : 0x0004u,
            InputDevice.MouseRight => down ? 0x0008u : 0x0010u,
            InputDevice.MouseX1 or InputDevice.MouseX2 => down ? 0x0080u : 0x0100u,
            _ => throw new InvalidOperationException($"Unsupported synchronized input '{binding.DisplayName}'.")
        };
        var mouseData = binding.Device switch
        {
            InputDevice.MouseX1 => 1u,
            InputDevice.MouseX2 => 2u,
            _ => 0u
        };
        return new Input
        {
            Type = inputMouse,
            Union = new InputUnion { Mouse = new MouseInput { MouseData = mouseData, Flags = mouseFlags } }
        };
    }

    private static void SendKeyboard(int virtualKey, bool down)
    {
        const uint inputKeyboard = 1;
        const uint keyeventfExtendedkey = 0x0001;
        const uint keyeventfKeyup = 0x0002;
        const uint keyeventfScancode = 0x0008;
        var scanCode = (ushort)MapVirtualKey((uint)virtualKey, 0);
        var flags = keyeventfScancode | (down ? 0u : keyeventfKeyup);
        if (IsExtendedKey(virtualKey)) flags |= keyeventfExtendedkey;

        var input = new Input
        {
            Type = inputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = scanCode == 0 ? (ushort)virtualKey : (ushort)0,
                    ScanCode = scanCode,
                    Flags = scanCode == 0 ? (down ? 0u : keyeventfKeyup) : flags
                }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected a keyboard input event.");
    }

    private static void SendMouse(InputDevice device, bool down)
    {
        const uint inputMouse = 0;
        var flags = device switch
        {
            InputDevice.MouseLeft => down ? 0x0002u : 0x0004u,
            InputDevice.MouseRight => down ? 0x0008u : 0x0010u,
            InputDevice.MouseX1 or InputDevice.MouseX2 => down ? 0x0080u : 0x0100u,
            _ => 0u
        };
        if (flags == 0) return;
        var mouseData = device switch
        {
            InputDevice.MouseX1 => 1u,
            InputDevice.MouseX2 => 2u,
            _ => 0u
        };

        var input = new Input
        {
            Type = inputMouse,
            Union = new InputUnion { Mouse = new MouseInput { MouseData = mouseData, Flags = flags } }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected a mouse input event.");
    }

    private static void ClickMouse(InputDevice device)
    {
        const uint inputMouse = 0;
        var (downFlag, upFlag, mouseData) = device switch
        {
            InputDevice.MouseLeft => (0x0002u, 0x0004u, 0u),
            InputDevice.MouseRight => (0x0008u, 0x0010u, 0u),
            InputDevice.MouseX1 => (0x0080u, 0x0100u, 1u),
            InputDevice.MouseX2 => (0x0080u, 0x0100u, 2u),
            _ => (0u, 0u, 0u)
        };
        if (downFlag == 0) return;

        Input[] inputs =
        [
            new Input
            {
                Type = inputMouse,
                Union = new InputUnion { Mouse = new MouseInput { MouseData = mouseData, Flags = downFlag } }
            },
            new Input
            {
                Type = inputMouse,
                Union = new InputUnion { Mouse = new MouseInput { MouseData = mouseData, Flags = upFlag } }
            }
        ];
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected a mouse click.");
    }

    private static void SendWallRelativeMouse(int x, int y)
    {
        const uint mouseeventfMove = 0x0001;
        mouse_event(mouseeventfMove, x, y, 0, UIntPtr.Zero);
    }

    private static bool IsExtendedKey(int virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0x90 or 0x91 or 0xA3 or 0xA5;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _virtualController.Dispose();
        _controllerPollCancellation?.Cancel();
        _controllerPollCancellation?.Dispose();
        _controllerPollCancellation = null;
        Panic();
        DisposeHooks();
        GC.SuppressFinalize(this);
    }

    private void DisposeHooks()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public uint Size;
        public uint Flags;
        public uint X;
        public uint Y;
        public uint Z;
        public uint R;
        public uint U;
        public uint V;
        public uint Buttons;
        public uint ButtonNumber;
        public uint Pov;
        public uint Reserved1;
        public uint Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ProductName;
        public uint XMin;
        public uint XMax;
        public uint YMin;
        public uint YMax;
        public uint ZMin;
        public uint ZMax;
        public uint NumberOfButtons;
        public uint PeriodMin;
        public uint PeriodMax;
        public uint RMin;
        public uint RMax;
        public uint UMin;
        public uint UMax;
        public uint VMin;
        public uint VMax;
        public uint Capabilities;
        public uint MaximumAxes;
        public uint NumberOfAxes;
        public uint MaximumButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegistryKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemVxd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Union; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput { public uint Message; public ushort ParamLow; public ushort ParamHigh; }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExKeyboard(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExMouse(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport("winmm.dll", EntryPoint = "joyGetPosEx")]
    private static extern uint joyGetPosEx(uint joystickId, ref JoyInfoEx info);

    [DllImport("winmm.dll", EntryPoint = "joyGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint joyGetDevCaps(uint joystickId, ref JoyCaps capabilities, uint size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr window, out Rect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr window, ref Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
