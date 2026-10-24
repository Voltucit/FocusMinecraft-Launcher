namespace FMLauncher.Features.Launch.Models;

public sealed record SessionMessage(string Kind, string Text = "", int? ProcessId = null, int? ExitCode = null);

