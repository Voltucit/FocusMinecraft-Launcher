using System.ComponentModel;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Home.ViewModels.Instances;

/// <summary>固定实例上下文并持有四个子页，导航不会重建草稿或导出选择。</summary>
public sealed class InstanceWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly PreviewSession _session;
    private int _tab;
    private string _gameDirectory = "";
    public PreviewInstance Instance { get; }
    public InstanceOverviewViewModel Overview { get; }
    public InstanceLaunchSettingsViewModel Settings { get; }
    public InstanceModsViewModel Mods { get; }
    public InstanceExportViewModel Export { get; }
    public UiCommand ReturnHome { get; }
    public bool IsAvailable => _session.Instances.Contains(Instance);
    public int SelectedTab
    {
        get=>_tab;
        set
        {
            if(!Set(ref _tab,value))return;
            if(value==2)_ = Mods.LoadAsync();
            if(value==3)_ = Export.RefreshFilesAsync();
        }
    }
    public string Metadata => Instance.VersionText + " / " + Instance.ComponentsText;
    public InstanceWorkspaceViewModel(PreviewSession session, PreviewInstance instance, Action back)
    {
        _session=session;Instance=instance;_gameDirectory=instance.GameDirectory;ReturnHome=new(_=>back());
        Overview=new(session,instance,back);Settings=new(session,instance);Mods=new(session,instance);
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
    { _session.PropertyChanged-=SessionChanged;Settings.Dispose();Mods.Dispose();Export.Dispose(); }
}
