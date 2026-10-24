namespace FMLauncher.CoreIntegration.Models;

public sealed record JavaItem(string Path, int Major, string Version, string Architecture)
{
    public string Label => $"Java {Version} · {Architecture}";
}
