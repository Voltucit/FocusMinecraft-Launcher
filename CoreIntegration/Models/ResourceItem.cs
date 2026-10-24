namespace FMLauncher.CoreIntegration.Models;

public sealed record ResourceItem(string Id, string Title, string Description, string Source)
{
    public string Label => $"{Title} · {Id}";
}
