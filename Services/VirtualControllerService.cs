using D2MacroNative.Models;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace D2MacroNative.Services;

public sealed class VirtualControllerService : IDisposable
{
    private readonly object _sync = new();
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;

    public void EnsureConnected()
    {
        lock (_sync)
        {
            if (_controller is not null) return;
            try
            {
                _client = new ViGEmClient();
                _controller = _client.CreateXbox360Controller();
                _controller.Connect();
                _controller.AutoSubmitReport = false;
                _controller.ResetReport();
                _controller.SubmitReport();
            }
            catch (Exception ex)
            {
                DisposeCore();
                throw new InvalidOperationException(
                    "ViGEmBus is required for pure controller output but is not installed or available.", ex);
            }
        }
    }

    public void Set(InputBinding binding, bool pressed)
    {
        lock (_sync)
        {
            EnsureConnected();
            Apply(binding, pressed);
            _controller!.SubmitReport();
        }
    }

    public void SetTogether(bool pressed, params InputBinding[] bindings)
    {
        lock (_sync)
        {
            EnsureConnected();
            foreach (var binding in bindings) Apply(binding, pressed);
            _controller!.SubmitReport();
        }
    }

    private void Apply(InputBinding binding, bool pressed)
    {
        if (binding.Device != InputDevice.Controller)
            throw new InvalidOperationException($"Controller output '{binding.DisplayName}' is not a controller button.");

        var code = KeyNames.NormalizeControllerCode(binding.VirtualKey);
        if (code == 0x10000)
        {
            _controller!.SetSliderValue(Xbox360Slider.LeftTrigger, pressed ? byte.MaxValue : byte.MinValue);
            return;
        }
        if (code == 0x20000)
        {
            _controller!.SetSliderValue(Xbox360Slider.RightTrigger, pressed ? byte.MaxValue : byte.MinValue);
            return;
        }

        _controller!.SetButtonState(MapButton(code), pressed);
    }

    public void SubmitState(
        ushort buttons,
        byte leftTrigger,
        byte rightTrigger,
        short leftX,
        short leftY,
        short rightX,
        short rightY)
    {
        lock (_sync)
        {
            EnsureConnected();
            _controller!.ResetReport();
            _controller.SetButtonsFull(buttons);
            _controller.SetSliderValue(Xbox360Slider.LeftTrigger, leftTrigger);
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, rightTrigger);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, leftX);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, leftY);
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, rightX);
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, rightY);
            _controller.SubmitReport();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (_controller is null) return;
            _controller.ResetReport();
            _controller.SubmitReport();
        }
    }

    private static Xbox360Button MapButton(int code) => code switch
    {
        0x0001 => Xbox360Button.Up,
        0x0002 => Xbox360Button.Down,
        0x0004 => Xbox360Button.Left,
        0x0008 => Xbox360Button.Right,
        0x0010 => Xbox360Button.Start,
        0x0020 => Xbox360Button.Back,
        0x0040 => Xbox360Button.LeftThumb,
        0x0080 => Xbox360Button.RightThumb,
        0x0100 => Xbox360Button.LeftShoulder,
        0x0200 => Xbox360Button.RightShoulder,
        0x1000 => Xbox360Button.A,
        0x2000 => Xbox360Button.B,
        0x4000 => Xbox360Button.X,
        0x8000 => Xbox360Button.Y,
        _ => throw new InvalidOperationException($"Unsupported virtual controller button: 0x{code:X}.")
    };

    public void Dispose()
    {
        lock (_sync) DisposeCore();
    }

    private void DisposeCore()
    {
        if (_controller is not null)
        {
            try { _controller.ResetReport(); _controller.SubmitReport(); } catch { }
            try { _controller.Disconnect(); } catch { }
            _controller = null;
        }
        _client?.Dispose();
        _client = null;
    }
}
