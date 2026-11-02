using System.Globalization;
using FocusMinecraftLauncher.Common;

namespace FocusMinecraftLauncher.Presentation.ViewModels;

/// <summary>内存设置草稿及 Windows 物理内存读数；自动分配在实际启动时重新计算。</summary>
public sealed class MemoryAllocationViewModel : ObservableObject
{
    private bool _automatic = true, _optimize;
    private double _allocated = 4, _maximum = 64;
    private double? _total, _used;
    private int? _mods;

    public bool IsAutomatic
    {
        get => _automatic;
        set
        {
            if (!Set(ref _automatic, value)) return;
            Changed(nameof(IsCustom)); Changed(nameof(CanAdjustMemory)); Changed(nameof(AllocationText));
            Changed(nameof(AutomaticMemoryHint));
        }
    }
    public bool IsCustom { get => !IsAutomatic; set { if (value) IsAutomatic = false; } }
    public bool OptimizeBeforeLaunch { get => _optimize; set => Set(ref _optimize, value); }
    public double MinimumMemoryGb => 0.5;
    public double MaximumMemoryGb => _maximum;
    public string MaximumMemoryText => TotalMemoryGb is > 0 ? FormatTotal(MaximumMemoryGb) : "待获取";
    public bool CanAdjustMemory => IsCustom && HasSystemMemory;
    public double AllocatedMemoryGb
    {
        get => _allocated;
        set
        {
            if (!double.IsFinite(value)) return;
            var stepped = Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;
            if (!Set(ref _allocated, Math.Clamp(stepped, MinimumMemoryGb, MaximumMemoryGb))) return;
            Changed(nameof(AllocationText)); Changed(nameof(MemoryMb));
        }
    }
    public int MemoryMb => (int)Math.Round(AllocatedMemoryGb * 1024);
    public string AllocationText => IsAutomatic ? "按 Mod 数量与可用内存自动分配" : Format(AllocatedMemoryGb);

    /// <summary>实例页已知 Mod 数量时用于估算；全局设置留空，只给通用说明。</summary>
    public int? ModCount
    {
        get => _mods;
        set { if (Set(ref _mods, value)) Changed(nameof(AutomaticMemoryHint)); }
    }
    /// <summary>自动模式的说明与本次估算值；数值仅用于提示，真正分配仍在启动时按当时可用内存重算。</summary>
    public string AutomaticMemoryHint
    {
        get
        {
            if (ModCount is not { } mods) return "按各游戏的 Mod 数量与启动时的可用内存自动分配";
            if (!HasSystemMemory) return "按 Mod 数量与可用内存自动分配";
            var estimate = Infrastructure.Runtime.RuntimeSupport.AutomaticMemoryMb(TotalMemoryGb!.Value, AvailableMemoryGb ?? 0, mods);
            var size = Format(estimate / 1024d);
            return mods == 0
                ? $"原版游戏预计分配 {size}，启动时按可用内存调整"
                : $"按 {mods} 个 Mod 预计分配 {size}，启动时按可用内存调整";
        }
    }

    // 页面可见期间更新 Windows 实际读数；未知读数保持为空，不显示为实测 0。
    public double? TotalMemoryGb
    {
        get => _total;
        set
        {
            if (!Set(ref _total, Reading(value))) return;
            if (_total is > 0)
            {
                // 总容量按 GB 四舍五入；已用、可用和使用率仍基于未取整的 Windows 读数。
                Set(ref _maximum, Math.Max(MinimumMemoryGb, Math.Round(_total.Value, MidpointRounding.AwayFromZero)), nameof(MaximumMemoryGb));
                SetAllocation(Math.Clamp(_allocated, MinimumMemoryGb, MaximumMemoryGb));
            }
            RefreshReadings();
        }
    }
    public double? UsedMemoryGb
    {
        get => _used;
        set { if (Set(ref _used, Reading(value))) RefreshReadings(); }
    }
    public double? AvailableMemoryGb => TotalMemoryGb is > 0 && UsedMemoryGb.HasValue
        ? Math.Max(0, TotalMemoryGb.Value - UsedMemoryGb.Value) : null;
    public bool HasSystemMemory => TotalMemoryGb is > 0 && UsedMemoryGb.HasValue;
    public double MemoryUsagePercent => HasSystemMemory
        ? Math.Clamp(UsedMemoryGb!.Value / TotalMemoryGb!.Value * 100, 0, 100) : 0;
    public string UsedMemoryText => UsedMemoryGb.HasValue ? Format(UsedMemoryGb.Value) : "待获取";
    public string TotalMemoryText => TotalMemoryGb is > 0 ? FormatTotal(TotalMemoryGb.Value) : "待获取";
    public string AvailableMemoryText => AvailableMemoryGb.HasValue ? "可用 " + Format(AvailableMemoryGb.Value) : "无法读取系统内存";
    public string UsageText => HasSystemMemory ? $"系统内存使用率 {MemoryUsagePercent:0.0}%" : "尚无系统内存读数";

    /// <summary>沿用已有 MiB 设置，限制在本机容量内；不将旧配置强制舍入为滑块步长或立即覆盖保存值。</summary>
    public void LoadMemoryMb(int memoryMb)
    {
        var gb = Math.Max(512, memoryMb) / 1024d;
        SetAllocation(TotalMemoryGb is > 0 ? Math.Clamp(gb, MinimumMemoryGb, MaximumMemoryGb) : gb);
    }

    public MemoryAllocationViewModel() => RefreshSystemMemory();
    /// <summary>读取 Windows 物理内存，失败时清除旧读数，避免把缓存值冒充当前值。</summary>
    public void RefreshSystemMemory()
    {
        try { var memory = Infrastructure.Runtime.RuntimeSupport.Memory(); TotalMemoryGb = memory.Total; UsedMemoryGb = memory.Total - memory.Available; }
        catch (System.IO.IOException) { TotalMemoryGb = null; UsedMemoryGb = null; }
    }
    private void RefreshReadings()
    {
        Changed(nameof(AvailableMemoryGb)); Changed(nameof(HasSystemMemory)); Changed(nameof(MemoryUsagePercent));
        Changed(nameof(UsedMemoryText)); Changed(nameof(TotalMemoryText)); Changed(nameof(MaximumMemoryText)); Changed(nameof(AvailableMemoryText)); Changed(nameof(UsageText)); Changed(nameof(CanAdjustMemory)); Changed(nameof(AutomaticMemoryHint));
    }
    private void SetAllocation(double value)
    {
        if (!Set(ref _allocated, value, nameof(AllocatedMemoryGb))) return;
        Changed(nameof(MemoryMb)); Changed(nameof(AllocationText));
    }
    private static double? Reading(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
    private static string Format(double value) => value.ToString("0.0", CultureInfo.CurrentCulture) + " GB";
    private static string FormatTotal(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.CurrentCulture) + " GB";
}
