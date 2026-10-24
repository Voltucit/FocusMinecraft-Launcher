namespace FMLauncher.CoreIntegration.Models;

public sealed record WorkProgress(string Stage, int Completed = 0, int Total = 0, long Bytes = 0, string File = "", int Attempt = 0, long? TotalBytes = null)
{
    public string Label => Stage + (Total > 0 ? $" · {Completed}/{Total}" : "") + (Bytes > 0 ? $" · {Bytes / 1048576d:F1} MiB" : "");
}
