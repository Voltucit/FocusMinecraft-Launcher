using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>读取本地 Mod 的展示资料和内嵌图标；不根据文件名推断游戏兼容性。</summary>
public sealed record LocalModMetadata(string Name, string Description, string Version, string Loader, ImageSource? Icon, string Error)
{
    public static LocalModMetadata Read(string path)
    {
        var name = Path.GetFileName(path);
        string description = "", version = "版本未提供", loader = "元数据未提供", iconPath = "";
        ImageSource? icon = null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            JsonNode? Json(ZipArchiveEntry entry)
            {
                if (entry.Length > 2 * 1024 * 1024) throw new InvalidDataException("Mod 元数据文件过大。");
                using var stream = entry.Open(); return JsonNode.Parse(stream);
            }
            if (archive.GetEntry("fabric.mod.json") is { } fabric)
            {
                var data = Json(fabric);
                name = data?["name"]?.GetValue<string>() ?? data?["id"]?.GetValue<string>() ?? name;
                description = data?["description"]?.GetValue<string>() ?? "";
                version = data?["version"]?.GetValue<string>() ?? version; loader = "Fabric";
                iconPath = data?["icon"] is JsonObject sizes
                    ? sizes.OrderBy(pair => int.TryParse(pair.Key, out var size) ? Math.Abs(size - 64) : int.MaxValue).FirstOrDefault().Value?.GetValue<string>() ?? ""
                    : data?["icon"]?.GetValue<string>() ?? "";
            }
            else if ((archive.GetEntry("META-INF/neoforge.mods.toml") ?? archive.GetEntry("META-INF/mods.toml")) is { } toml)
            {
                if (toml.Length > 2 * 1024 * 1024) throw new InvalidDataException("Mod 元数据文件过大。");
                using var reader = new StreamReader(toml.Open()); var text = reader.ReadToEnd();
                var firstMod = Regex.Match(text, @"(?ms)^\s*\[\[mods\]\](.*?)(?=^\s*\[|\z)");
                var section = firstMod.Success ? firstMod.Groups[1].Value : text;
                string Field(string key)
                {
                    var match = Regex.Match(section, @"(?ms)^\s*" + Regex.Escape(key) + "\\s*=\\s*(?:'''(.*?)'''|\"\"\"(.*?)\"\"\"|'([^'\\r\\n]*)'|\"([^\"\\r\\n]*)\")");
                    return match.Groups.Cast<Group>().Skip(1).FirstOrDefault(group => group.Success)?.Value.Trim() ?? "";
                }
                name = Field("displayName") is { Length: > 0 } title ? title : name;
                description = Field("description"); iconPath = Field("logoFile");
                var declared = Field("version");
                if (declared == "${file.jarVersion}" && archive.GetEntry("META-INF/MANIFEST.MF") is { Length: < 1024 * 1024 } manifest)
                {
                    using var manifestReader = new StreamReader(manifest.Open());
                    declared = Regex.Match(manifestReader.ReadToEnd(), @"(?mi)^Implementation-Version:\s*([^\r\n]+)").Groups[1].Value.Trim();
                }
                if (declared.Length > 0 && !declared.Contains("${")) version = declared;
                loader = toml.FullName.Contains("neoforge") ? "NeoForge" : "Forge";
            }
            else if (archive.GetEntry("mcmod.info") is { } legacy)
            {
                var data = Json(legacy); var item = data is JsonArray array ? array.FirstOrDefault() : data?["modList"]?.AsArray().FirstOrDefault();
                name = item?["name"]?.GetValue<string>() ?? name; description = item?["description"]?.GetValue<string>() ?? "";
                version = item?["version"]?.GetValue<string>() ?? version; iconPath = item?["logoFile"]?.GetValue<string>() ?? ""; loader = "Forge";
            }
            if (iconPath.Length > 0 && archive.GetEntry(iconPath.Replace('\\', '/').TrimStart('/')) is { Length: > 0 and <= 8 * 1024 * 1024 } image)
            {
                try
                {
                    using var source = image.Open(); using var buffer = new MemoryStream(); source.CopyTo(buffer); buffer.Position = 0;
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 64; bitmap.StreamSource = buffer; bitmap.EndInit(); bitmap.Freeze(); icon = bitmap;
                }
                catch (Exception error) when (error is IOException or NotSupportedException or FormatException) { }
            }
            return new(name, description, version, loader, icon, "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        { return new(name, description, version, loader, null, "无法读取此 Mod 的资料，请检查文件。"); }
    }
}
