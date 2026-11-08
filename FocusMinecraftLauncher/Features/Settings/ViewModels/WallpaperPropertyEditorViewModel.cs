using System.Globalization;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Features.Settings.ViewModels;

/// <summary>一个 Wallpaper Engine 用户属性的编辑状态。</summary>
public sealed class WallpaperPropertyEditorViewModel : ObservableObject
{
    public WallpaperProperty Definition { get; }
    public string Key => Definition.Key;
    public string Label => Definition.Label;
    public WallpaperPropertyKind Kind => Definition.Kind;
    public double Minimum => Definition.Minimum;
    public double Maximum => Definition.Maximum;
    public IReadOnlyList<WallpaperPropertyOption> Options => Definition.Options ?? [];
    public string Value { get; private set; }
    public double NumberValue
    {
        get => double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : Minimum;
        set => SetValue(value.ToString("0.###", CultureInfo.InvariantCulture));
    }
    public bool BoolValue
    {
        get => bool.TryParse(Value, out bool value) && value;
        set => SetValue(value ? "true" : "false");
    }
    public string TextValue { get => Value; set => SetValue(value ?? ""); }
    public event Action<string, string>? ValueChanged;

    public void Reset() => SetValue(Definition.DefaultValue);

    public WallpaperPropertyEditorViewModel(WallpaperProperty definition, string value)
    {
        Definition = definition;
        Value = value.Length > 0 ? value : definition.DefaultValue;
    }

    private void SetValue(string value)
    {
        if (string.Equals(Value, value, StringComparison.Ordinal)) return;
        Value = value;
        Changed(nameof(Value));
        Changed(nameof(NumberValue));
        Changed(nameof(BoolValue));
        Changed(nameof(TextValue));
        ValueChanged?.Invoke(Key, value);
    }
}
