using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyVideoEditor.Core.Rendering;

namespace MyVideoEditor.App.ViewModels;

/// <summary>One effect on the selected clip, with its settings.</summary>
public sealed partial class EffectViewModel : ObservableObject
{
    private readonly ClipInspectorViewModel _owner;

    public EffectViewModel(ClipInspectorViewModel owner, int index, EffectDefinition definition, Core.Model.Effect effect)
    {
        _owner = owner;
        Index = index;
        Definition = definition;
        _enabled = effect.Enabled;
        Parameters = definition.Parameters.Select(p => new EffectParameterViewModel(this, p, effect.Get(p.Name, p.Default))).ToList();
    }

    public int Index { get; }
    public EffectDefinition Definition { get; }
    public string Label => Definition.Label + (Definition.IsAudio ? " (sound)" : "");
    public string Description => Definition.Description;
    public IReadOnlyList<EffectParameterViewModel> Parameters { get; }
    public bool HasParameters => Parameters.Count > 0;

    [ObservableProperty]
    private bool _enabled;

    partial void OnEnabledChanged(bool value) =>
        _owner.UpdateEffect(Index, value ? "Enable effect" : "Disable effect", e => e.Enabled = value, null);

    internal void SetParameter(string name, double value) =>
        _owner.UpdateEffect(Index, Definition.Label, e => e.Parameters[name] = value, $"fx:{Index}:{name}");

    [RelayCommand]
    private void Remove() => _owner.RemoveEffect(Index);

    [RelayCommand]
    private void Reset() => _owner.UpdateEffect(Index, "Reset effect", e => e.Parameters = Definition.CreateDefault().Parameters, null);
}

public sealed partial class EffectParameterViewModel : ObservableObject
{
    private readonly EffectViewModel _effect;
    private double _value;

    public EffectParameterViewModel(EffectViewModel effect, EffectParameter parameter, double value)
    {
        _effect = effect;
        Parameter = parameter;
        _value = value;
    }

    public EffectParameter Parameter { get; }
    public string Label => Parameter.Label;
    public double Min => Parameter.Min;
    public double Max => Parameter.Max;
    public double Step => Parameter.Step;
    public bool IsColor => Parameter.IsColor;
    public bool IsNumber => !Parameter.IsColor;

    public double Value
    {
        get => _value;
        set
        {
            if (Math.Abs(_value - value) < 1e-9)
                return;
            _value = Math.Clamp(value, Min, Max);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
            _effect.SetParameter(Parameter.Name, _value);
        }
    }

    public string ValueText => Parameter.Step >= 1 ? $"{_value:0}{Parameter.Unit}" : $"{_value:0.##}{Parameter.Unit}";

    public Color ColorValue
    {
        get
        {
            var rgb = (uint)_value & 0xFFFFFF;
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        set => Value = (value.R << 16) | (value.G << 8) | value.B;
    }
}
