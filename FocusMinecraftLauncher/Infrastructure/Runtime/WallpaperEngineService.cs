using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

public enum WallpaperKind
{
    Image,
    Video,
    Web,
    Scene
}

public sealed record WallpaperDescriptor(
    WallpaperKind Kind,
    string Path,
    string PreviewPath,
    string Title,
    string ProjectPath,
    IReadOnlyList<WallpaperProperty>? Properties = null);

public sealed record WallpaperOption(
    string Title,
    string ProjectPath,
    WallpaperKind Kind,
    string PreviewPath);

public enum WallpaperPropertyKind
{
    Slider,
    Color,
    Bool,
    Combo,
    Text
}

public sealed record WallpaperPropertyOption(string Label, string Value);

public sealed record WallpaperProperty(
    string Key,
    string Label,
    WallpaperPropertyKind Kind,
    string DefaultValue,
    double Minimum = 0,
    double Maximum = 1,
    IReadOnlyList<WallpaperPropertyOption>? Options = null);

/// <summary>通过 Wallpaper Engine 官方命令行读取当前桌面壁纸并解析项目类型。</summary>
public static class WallpaperEngineService
{
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".avi"];
    private static readonly string[] WebExtensions = [".html", ".htm"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".pnga", ".apng", ".bmp", ".gif", ".webp", ".avif", ".svg", ".tif", ".tiff"];

    public static async Task<WallpaperDescriptor> GetCurrentAsync(CancellationToken token = default)
    {
        var executables = new List<string>();
        if (FindRunningExecutable() is { } running) executables.Add(running);
        if (FindExecutable() is { } installed) executables.Add(installed);

        foreach (string executable in executables.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string[] arguments in new[]
                     {
                         new[] { "-control", "getWallpaper" },
                         new[] { "-control", "getWallpaper", "-monitor", "0" }
                     })
            {
                string path = await RunGetWallpaperAsync(executable, arguments, token);
                if (!string.IsNullOrWhiteSpace(path)) return await Task.Run(() => ResolvePath(path, token), token);
            }
        }

        throw new InvalidOperationException(executables.Count > 0
            ? "Wallpaper Engine 正在运行，但命令行没有返回当前壁纸。请从下方列表选择壁纸。"
            : "未找到 Wallpaper Engine。请确认已安装并至少运行过一次。");
    }

    private static async Task<string> RunGetWallpaperAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return "";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            string error = await errorTask;
            await process.WaitForExitAsync(timeout.Token);
            string path = output.Trim().Trim('"').TrimStart('\uFEFF');
            if (path.Length == 0) path = error.Trim().Trim('"').TrimStart('\uFEFF');
            return path;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { process.Kill(true); }
            catch (Exception) { }
            return "";
        }
    }

    public static WallpaperDescriptor ResolvePath(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string full = System.IO.Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            string project = System.IO.Path.Combine(full, "project.json");
            if (File.Exists(project)) return ReadProject(project, token);
            throw new InvalidDataException("Wallpaper Engine 壁纸目录缺少 project.json。");
        }

        string extension = System.IO.Path.GetExtension(full).ToLowerInvariant();
        string directory = System.IO.Path.GetDirectoryName(full) ?? "";
        string preview = FindPreview(directory, null);
        string title = System.IO.Path.GetFileNameWithoutExtension(full);
        if (!File.Exists(full)) throw new FileNotFoundException("找不到壁纸文件，请确认壁纸仍保存在本地。", full);

        if (extension is ".json")
            return ReadProject(full, token);

        if (extension is ".pkg")
        {
            string project = System.IO.Path.Combine(directory, "project.json");
            if (File.Exists(project)) return ReadProject(project, token);
            return new(WallpaperKind.Scene, full, preview, title, full);
        }

        if (VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return new(WallpaperKind.Video, full, preview, title, full);
        if (WebExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return new(WallpaperKind.Web, full, preview, title, full);
        }
        if (ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return new(WallpaperKind.Image, full, full, title, full);

        throw new NotSupportedException("当前 Wallpaper Engine 壁纸类型暂不支持：" + extension);
    }

    private static WallpaperDescriptor ReadProject(string projectPath, CancellationToken token)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(projectPath));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("壁纸的 project.json 内容无效。");
        string directory = System.IO.Path.GetDirectoryName(projectPath) ?? "";
        string type = Text(root, "type").ToLowerInvariant();
        string title = Text(root, "title");
        IReadOnlyList<WallpaperProperty> properties = ReadProperties(root);
        if (title.Length == 0) title = System.IO.Path.GetFileName(directory);
        string preview = Text(root, "preview");
        string file = Text(root, "file");
        bool presetImage = false;
        if (file.Length == 0 && root.TryGetProperty("preset", out JsonElement preset) && preset.ValueKind == JsonValueKind.Object)
        {
            string single = Text(preset, "image");
            if (single.Length > 0) file = single;
            else
            {
                string custom = Text(preset, "customdirectory");
                string found = FindFirstImage(System.IO.Path.Combine(directory, custom), token);
                if (found.Length > 0) file = found;
            }
            if (file.Length > 0 && type.Length == 0) type = "image";
            presetImage = type is "image" or "gif";
        }
        preview = FindPreview(directory, preview);
        if (preview.Length == 0 && type is "image" && file.Length > 0)
        {
            string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, file));
            if (ImageExtensions.Contains(System.IO.Path.GetExtension(candidate), StringComparer.OrdinalIgnoreCase))
                preview = candidate;
        }

        string target = "";
        if (file.Length > 0)
        {
            target = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, file));
            string rootPath = System.IO.Path.GetFullPath(directory) + System.IO.Path.DirectorySeparatorChar;
            // 图片预设可以显式引用用户自己的图片目录；网页与场景入口仍限制在项目内。
            if (!presetImage && !target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Wallpaper Engine 壁纸文件路径越出了项目目录。");
        }
        if (type is "web" && target.Length == 0)
        {
            string index = System.IO.Path.Combine(directory, "index.html");
            if (File.Exists(index)) target = index;
        }

        if (type is "video" && target.Length > 0 && File.Exists(target))
            return new(WallpaperKind.Video, target, preview, title, projectPath);
        if (type is "web" && target.Length > 0 && File.Exists(target))
            return new(WallpaperKind.Web, target, preview, title, projectPath, properties);
        if (type is "scene")
            return new(WallpaperKind.Scene, projectPath, preview, title, projectPath, properties);
        if (type is "image" or "gif" && target.Length > 0 && File.Exists(target))
        {
            if (preview.Length == 0) preview = target;
            return new(WallpaperKind.Image, target, preview, title, projectPath);
        }
        if (type is "application")
        {
            if (preview.Length > 0) return new(WallpaperKind.Image, preview, preview, title, projectPath);
            throw new NotSupportedException("Application 类型壁纸没有可用的预览图。");
        }

        // 项目类型缺失时按文件扩展名判断。
        string extension = System.IO.Path.GetExtension(target).ToLowerInvariant();
        if (target.Length > 0 && File.Exists(target) && VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return new(WallpaperKind.Video, target, preview, title, projectPath);
        if (target.Length > 0 && File.Exists(target) && WebExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return new(WallpaperKind.Web, target, preview, title, projectPath);
        if (target.Length > 0 && File.Exists(target) && ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return new(WallpaperKind.Image, target, preview.Length > 0 ? preview : target, title, projectPath);
        if (extension is ".pkg" or ".json")
            return new(WallpaperKind.Scene, projectPath, preview, title, projectPath, properties);
        if (type is "image" or "gif" or "video" or "web")
            throw new FileNotFoundException("壁纸项目中找不到需要显示的原始文件。", target);
        if (preview.Length > 0) return new(WallpaperKind.Image, preview, preview, title, projectPath);
        throw new NotSupportedException("无法识别这个 Wallpaper Engine 壁纸类型。");
    }

    private static string FindFirstImage(string directory, CancellationToken token)
    {
        if (!Directory.Exists(directory)) return "";
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                if (ImageExtensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) return file;
            }
            return "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string FindPreview(string directory, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, configured));
            if (File.Exists(candidate)) return candidate;
        }
        foreach (string name in new[] { "preview.jpg", "preview.jpeg", "preview.png", "preview.webp", "preview.gif", "preview.bmp" })
        {
            string candidate = System.IO.Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static IReadOnlyList<WallpaperProperty> ReadProperties(JsonElement root)
    {
        if (!root.TryGetProperty("general", out JsonElement general)
            || general.ValueKind != JsonValueKind.Object
            || !general.TryGetProperty("properties", out JsonElement properties)
            || properties.ValueKind != JsonValueKind.Object)
            return [];

        var list = new List<WallpaperProperty>();
        foreach (JsonProperty item in properties.EnumerateObject())
        {
            string key = item.Name;
            JsonElement definition = item.Value;
            if (key.Length == 0 || definition.ValueKind != JsonValueKind.Object) continue;
            string type = Text(definition, "type").ToLowerInvariant();
            // 没有 type 的条目是 tip / ui_* 之类的显示项，不是可编辑属性。
            if (type.Length == 0) continue;
            WallpaperPropertyKind kind = type switch
            {
                "slider" => WallpaperPropertyKind.Slider,
                "color" => WallpaperPropertyKind.Color,
                "bool" => WallpaperPropertyKind.Bool,
                "combo" => WallpaperPropertyKind.Combo,
                "text" => WallpaperPropertyKind.Text,
                _ => WallpaperPropertyKind.Text
            };
            string label = Text(definition, "text");
            if (label.Length == 0) label = key;
            string defaultValue = Scalar(definition, "value");
            double minimum = Number(definition, "min", 0);
            double maximum = Number(definition, "max", 1);
            if (maximum <= minimum) maximum = minimum + 1;

            var options = new List<WallpaperPropertyOption>();
            if (kind == WallpaperPropertyKind.Combo
                && definition.TryGetProperty("options", out JsonElement array)
                && array.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement option in array.EnumerateArray())
                {
                    if (option.ValueKind != JsonValueKind.Object) continue;
                    string optionLabel = Text(option, "label");
                    if (optionLabel.Length == 0) optionLabel = Text(option, "text");
                    string value = Scalar(option, "value");
                    if (optionLabel.Length == 0) optionLabel = value;
                    if (value.Length > 0) options.Add(new(optionLabel, value));
                }
            }
            if (kind == WallpaperPropertyKind.Combo && options.Count == 0)
                kind = WallpaperPropertyKind.Text;

            list.Add(new(key, label, kind, defaultValue, minimum, maximum, options));
        }
        return list;
    }

    private static string Scalar(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static double Number(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number) ? number : fallback;

    private static readonly object InventoryGate = new();
    private static DateTime _inventoryTime;
    private static IReadOnlyList<WallpaperOption> _inventory = [];

    /// <summary>扫描本地 Wallpaper Engine 项目和创意工坊壁纸，返回可显示的项目。</summary>
    public static IReadOnlyList<WallpaperOption> GetLocalWallpapers(bool force = false)
    {
        lock (InventoryGate)
        {
            if (!force && DateTime.UtcNow - _inventoryTime < TimeSpan.FromSeconds(30))
                return _inventory;
        }

        var projectFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = SteamRoots().ToList();
        if (FindExecutable() is { } executable) roots.Add(System.IO.Path.GetDirectoryName(executable)!);

        foreach (string root in roots)
        {
            bool isInstall = string.Equals(System.IO.Path.GetFileName(root), "wallpaper_engine", StringComparison.OrdinalIgnoreCase);
            string install = isInstall ? root : System.IO.Path.Combine(root, "steamapps", "common", "wallpaper_engine");
            AddProjects(System.IO.Path.Combine(install, "projects", "defaultprojects"), projectFiles);
            AddProjects(System.IO.Path.Combine(install, "projects", "myprojects"), projectFiles);

            string steamRoot = isInstall
                ? System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "..", "..", ".."))
                : root;
            AddProjects(System.IO.Path.Combine(steamRoot, "steamapps", "workshop", "content", "431960"), projectFiles);
        }

        var options = new List<WallpaperOption>();
        foreach (string project in projectFiles)
        {
            try
            {
                WallpaperDescriptor descriptor = ResolvePath(project);
                options.Add(new(descriptor.Title, project, descriptor.Kind, descriptor.PreviewPath));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException or JsonException)
            {
                Trace.TraceWarning("跳过本地 Wallpaper 项目：{0}", error.GetType().Name);
            }
        }
        WallpaperOption[] result = options
            .GroupBy(option => option.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(option => option.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(option => option.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (InventoryGate)
        {
            _inventory = result;
            _inventoryTime = DateTime.UtcNow;
        }
        return result;
    }

    private static void AddProjects(string directory, HashSet<string> projects)
    {
        if (!Directory.Exists(directory)) return;
        HashSet<string> hidden = ReadHiddenNames(directory);
        try
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                string name = System.IO.Path.GetFileName(child);
                if (hidden.Contains(name) || hidden.Contains(child)) continue;
                string project = System.IO.Path.Combine(child, "project.json");
                if (File.Exists(project))
                {
                    projects.Add(System.IO.Path.GetFullPath(project));
                    continue;
                }
                AddProjects(child, projects);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("扫描 Wallpaper 项目失败：{0}", error.GetType().Name);
        }
    }

    /// <summary>读取 WE 的 visibility.json，返回被移出库的项目目录名。</summary>
    private static HashSet<string> ReadHiddenNames(string root)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string file = System.IO.Path.Combine(root, "visibility.json");
        try
        {
            if (!File.Exists(file)) return hidden;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            Walk(document.RootElement);
        }
        catch (Exception) { return hidden; }
        return hidden;

        void Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.False)
                {
                    hidden.Add(property.Name);
                    hidden.Add(property.Name.Replace('\\', '/'));
                    hidden.Add(property.Name.Split('\\', '/')[^1]);
                }
                else if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    Walk(property.Value);
                }
            }
        }
    }

    private static IReadOnlyList<string> SteamRoots()
    {
        List<string> roots = [];
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam
                && !string.IsNullOrWhiteSpace(steam))
                roots.Add(steam.Replace('/', '\\'));
        }
        catch (Exception) { }

        foreach (string variable in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            string? programFiles = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(programFiles))
                roots.Add(System.IO.Path.Combine(programFiles, "Steam"));
        }
        roots.Add(@"C:\Program Files (x86)\Steam");
        roots.Add(@"C:\Program Files\Steam");

        foreach (string root in roots.ToArray())
        {
            string libraryFile = System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (!File.Exists(libraryFile)) continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(libraryFile), "\"path\"\\s+\"([^\"]+)\""))
                {
                    string path = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (path.Length > 0) roots.Add(path);
                }
            }
            catch (Exception) { }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? FindRunningExecutable()
    {
        foreach (string name in new[] { "wallpaper64", "wallpaper32" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch (Exception) { }
                finally { process.Dispose(); }
            }
        }
        return null;
    }

    private static string? FindExecutable()
    {
        foreach (string root in SteamRoots())
        {
            foreach (string executable in new[] { "wallpaper64.exe", "wallpaper32.exe" })
            {
                string candidate = System.IO.Path.Combine(root, "steamapps", "common", "wallpaper_engine", executable);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
