namespace FocusMinecraftLauncher.Features.Downloads.Models;

/// <summary>数字版本按分段数值排序；非数字标识归入“其他”，不假定它们的发布先后。</summary>
public sealed class MinecraftVersionOrder : IComparer<string>
{
    public static MinecraftVersionOrder Instance { get; } = new();
    public int Compare(string? left, string? right)
    {
        var leftNumeric = Version.TryParse(left, out var a);
        var rightNumeric = Version.TryParse(right, out var b);
        if (leftNumeric && rightNumeric) return a!.CompareTo(b);
        if (leftNumeric != rightNumeric) return leftNumeric ? 1 : -1;
        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }
    public static string Series(string version) => Version.TryParse(version, out var value) ? $"{value.Major}.{value.Minor}" : "其他";
}
