using Microsoft.Win32;
using FMLauncher.Common;
using FMLauncher.CoreIntegration.Adapters;
namespace FMLauncher.Features.Modpacks.ViewModels;

public sealed class ModpacksViewModel : PageViewModel
{
    private string _archive = "";
    public string Archive { get => _archive; set => Set(ref _archive, value); }
    public AsyncCommand Browse { get; }
    public AsyncCommand Read { get; }
    public ModpacksViewModel(ModpackAdapter adapter)
    {
        Browse = Command(ct => { var dialog = new OpenFileDialog { Filter = "整合包|*.mrpack;*.zip" }; if (dialog.ShowDialog() == true) Archive = dialog.FileName; return Task.CompletedTask; });
        Read = Command(async ct => Status = await adapter.ReadAsync(Archive, ct));
    }
}
