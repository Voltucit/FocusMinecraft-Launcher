using System.Collections.ObjectModel;
using Microsoft.Win32;
using FMLauncher.Common;
using FMLauncher.CoreIntegration.Adapters;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Features.Downloads.Services;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.Features.Java.ViewModels;

public sealed class JavaViewModel : PageViewModel
{
    private JavaItem? _selected;
    private RuntimeItem? _runtime;
    private int _major = 21;
    private string _distribution = "Temurin";
    public ObservableCollection<JavaItem> Installed { get; } = [];
    public ObservableCollection<RuntimeItem> Available { get; } = [];
    public JavaItem? Selected { get => _selected; set => Set(ref _selected, value); }
    public RuntimeItem? Runtime { get => _runtime; set => Set(ref _runtime, value); }
    public int Major { get => _major; set => Set(ref _major, value); }
    public string Distribution { get => _distribution; set => Set(ref _distribution, value); }
    public AsyncCommand Detect { get; }
    public AsyncCommand Browse { get; }
    public AsyncCommand Use { get; }
    public AsyncCommand Query { get; }
    public AsyncCommand Install { get; }
    public AsyncCommand Recommend { get; }
    public JavaViewModel(JavaAdapter adapter, SettingsStore settings, OperationService operations)
    {
        Detect = Command(async ct => { var values = await adapter.DetectAsync(ct); Installed.Clear(); foreach (var value in values) Installed.Add(value); Status = $"已检测 {values.Count} 个运行环境。Java 主版本须与游戏元数据要求一致。"; });
        Recommend = Command(async ct =>
        {
            if (string.IsNullOrWhiteSpace(settings.Current.VersionId)) throw new InvalidOperationException("请先在主页选择版本并刷新，或安装游戏后再读取建议。");
            var required = await new VersionAdapter().RequiredJavaAsync(settings.Current.GameRoot, settings.Current.VersionId, ct);
            if (required is > 0) { Major = required.Value; Status = $"所选版本元数据要求 Java {Major}。可查询对应官方包，下载仍需点击安装。"; }
            else Status = "元数据没有 Java 主版本要求；请依据该游戏和加载器文档手动选择。";
        });
        Browse = Command(async ct => { var dialog = new OpenFileDialog { Filter = "Java|java.exe;javaw.exe" }; if (dialog.ShowDialog() == true) { Selected = await adapter.InspectAsync(dialog.FileName, ct); Installed.Add(Selected); } });
        Use = Command(ct => { settings.Current.JavaPath = Selected?.Path ?? throw new InvalidOperationException("请选择 Java。"); settings.Save(); Status = "Java 已保存到游戏设置。"; return Task.CompletedTask; });
        Query = Command(async ct => { var items = await adapter.AvailableAsync(Major, Distribution, ct); Available.Clear(); foreach (var item in items) Available.Add(item); Runtime = Available.FirstOrDefault(); Status = "查询只读取官方包信息，点击安装才下载。"; });
        Install = Command(async ct =>
        {
            var id = Runtime?.Id ?? throw new InvalidOperationException("请先查询并选择精确的 Java 包。");
            var value = await operations.RunAsync("安装 Java", (p, token) => adapter.InstallAsync(id, p, token), ct);
            Installed.Add(value); Selected = value; Status = "安装已完成。点击使用选中 Java 后生效。";
        });
    }
}

