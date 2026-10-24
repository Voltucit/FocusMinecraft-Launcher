using System.IO;
namespace FMLauncher.Appearance.Wallpapers.Providers;

internal static class ImageFile
{
    public static async Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        const int limit = 20 * 1024 * 1024;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length > limit) throw new InvalidDataException("图片超过 20 MiB。");
        using var output = new MemoryStream();
        var buffer = new byte[81920]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("图片超过 20 MiB。");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }
}

