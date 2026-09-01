using D2MacroNative.Models;

namespace D2MacroNative.ViewModels;

public sealed class GameBindItem : BindableBase
{
    private InputBinding _binding;
    private readonly Action<InputBinding> _apply;

    public GameBindItem(string id, string name, string description, string glyph, string accent,
        InputBinding binding, Action<InputBinding> apply)
    {
        Id = id;
        Name = name;
        Description = description;
        Glyph = glyph;
        Accent = accent;
        _binding = binding.Clone();
        _apply = apply;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Glyph { get; }
    public string Accent { get; }
    public InputBinding Binding => _binding;
    public string BindingDisplay => _binding.DisplayName;

    public void SetBinding(InputBinding binding)
    {
        _binding = binding.Clone();
        _apply(_binding.Clone());
        Notify(nameof(Binding));
        Notify(nameof(BindingDisplay));
    }
}

public sealed record ActivityEntry(string Time, string Title, string Detail, string Accent);

public sealed record WishOption(int Number, string Name)
{
    public string DisplayName => $"{Number}. {Name}";
    public override string ToString() => DisplayName;
}

public sealed record FpsOption(int Fps)
{
    public string DisplayName => $"{Fps} FPS";
    public override string ToString() => DisplayName;
}

public sealed record LoadoutOption(int Number)
{
    public string DisplayName => $"Loadout {Number}";
    public string ShortName => $"L{Number}";
    public override string ToString() => ShortName;
}

public sealed record ActivationModeOption(MacroActivationMode Mode, string Name)
{
    public override string ToString() => Name;
}

public sealed class TimingItem : BindableBase
{
    private int _value;
    private readonly Action<int> _apply;

    public TimingItem(string name, string description, string group, int minimum, int maximum, int value, Action<int> apply, string unit = "ms")
    {
        Name = name;
        Description = description;
        Group = group;
        Minimum = minimum;
        Maximum = maximum;
        _value = value;
        _apply = apply;
        Unit = unit;
    }

    public string Name { get; }
    public string Description { get; }
    public string Group { get; }
    public int Minimum { get; }
    public int Maximum { get; }
    public string Unit { get; }
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (!SetField(ref _value, clamped)) return;
            _apply(clamped);
        }
    }
}
