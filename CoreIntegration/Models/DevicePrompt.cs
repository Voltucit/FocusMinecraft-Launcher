namespace FMLauncher.CoreIntegration.Models;

public sealed record DevicePrompt(string Code, Uri Address, DateTimeOffset ExpiresAt);
