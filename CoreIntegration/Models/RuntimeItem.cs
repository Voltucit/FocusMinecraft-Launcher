namespace FMLauncher.CoreIntegration.Models;

public sealed record RuntimeItem(string Id, string Version, string Distribution, long Bytes)
{
    public string Label => $"{Distribution} {Version} · {Bytes / 1048576d:F1} MiB";
}
