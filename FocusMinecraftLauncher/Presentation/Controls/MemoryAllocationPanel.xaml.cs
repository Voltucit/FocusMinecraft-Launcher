using System.Windows.Controls;
using System.Windows;
using System.Windows.Threading;
using FocusMinecraftLauncher.Presentation.ViewModels;

namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>复用全局与实例内存设置；只在控件可见时请求 ViewModel 更新系统内存读数。</summary>
public partial class MemoryAllocationPanel : UserControl
{
    private readonly DispatcherTimer _readings = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    public MemoryAllocationPanel()
    {
        InitializeComponent();
        _readings.Tick += (_, _) => ReadMemory();
        Loaded += (_, _) => UpdateReadings();
        Unloaded += (_, _) => _readings.Stop();
        IsVisibleChanged += (_, _) => UpdateReadings();
        DataContextChanged += (_, _) => { if (IsLoaded && IsVisible) ReadMemory(); };
    }
    private void UpdateReadings()
    {
        if (!IsLoaded || !IsVisible) { _readings.Stop(); return; }
        ReadMemory(); _readings.Start();
    }
    private void ReadMemory()
    {
        if (Window.GetWindow(this)?.WindowState == WindowState.Minimized) return;
        if (DataContext is MemoryAllocationViewModel model) model.RefreshSystemMemory();
    }
}
