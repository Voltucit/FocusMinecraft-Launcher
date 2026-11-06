using System.ComponentModel;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Home.ViewModels.Instances;

/// <summary>固定实例上下文并持有各子页，导航不会重建草稿或导出选择。</summary>
public sealed class InstanceWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly PreviewSession _session;
    private int _tab;
    private string _gameDirectory = "";
    public PreviewInstance Instance { get; }
    public InstanceOverviewViewModel Overview { get; }
    public InstanceLaunchSettingsViewModel Settings { get; }
    public InstanceSavesViewModel Saves { get; }
    public InstanceContentViewModel ResourcePacks { get; }
    public InstanceContentViewModel Shaders { get; }
    public InstanceContentViewModel Mods { get; }
    public InstanceBackupsViewModel Backups { get; }
    public InstanceLogsViewModel Logs { get; }
    public InstanceExportViewModel Export { get; }
    public UiCommand ReturnHome { get; }
    public bool IsAvailable => _session.Instances.Contains(Instance);
    public int SelectedTab
    {
        get=>_tab;
        set
        {
            if(!Set(ref _tab,value))return;
            // 每次进入列表页都重读目录：游戏里可能刚改过 Mod 或新建过存档。
            if(value==2)_ = Saves.LoadAsync();
            else if(value==3)_ = ResourcePacks.LoadAsync();
            else if(value==4)_ = Shaders.LoadAsync();
            else if(value==5)_ = Mods.LoadAsync();
            else if(value==8)_ = Export.RefreshFilesAsync();
        }
    }
    public string Metadata => Instance.VersionText + " / " + Instance.ComponentsText;
    public InstanceWorkspaceViewModel(PreviewSession session, PreviewInstance instance, Action back)
    {
        _session=session;Instance=instance;_gameDirectory=instance.GameDirectory;ReturnHome=new(_=>back());
        Overview=new(session,instance,back);Settings=new(session,instance);
        Saves=new(session,instance);
        ResourcePacks=new(session,instance,InstanceContentKind.ResourcePack);
        Shaders=new(session,instance,InstanceContentKind.Shader);
        Mods=new(session,instance,InstanceContentKind.Mod);
        Backups=new(session,instance);Logs=new(instance);
        Export=new(session,instance,Mods);session.PropertyChanged+=SessionChanged;
    }
    private void SessionChanged(object? sender,PropertyChangedEventArgs e)
    {
        Changed(nameof(IsAvailable));Changed(nameof(Metadata));
        if (!_gameDirectory.Equals(Instance.GameDirectory, StringComparison.OrdinalIgnoreCase))
        { _gameDirectory = Instance.GameDirectory; _ = Export.RefreshFilesAsync(); }
        if(!IsAvailable && Export.IsBusy)Export.Cancel.Execute(null);
    }
    public void Dispose()
    { _session.PropertyChanged-=SessionChanged;Settings.Dispose();Saves.Dispose();ResourcePacks.Dispose();Shaders.Dispose();Mods.Dispose();Backups.Dispose();Logs.Dispose();Export.Dispose(); }
}
