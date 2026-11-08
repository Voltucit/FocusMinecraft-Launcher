using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>为壁纸播放器提供只读的本机文件源；直接流式传输场景包，不在 UI 线程读取大文件。</summary>
public sealed class WallpaperWebHost : IAsyncDisposable
{
    private readonly string _root, _sourcePath, _projectPath, _kind, _properties;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _loop;
    private bool _disposed;
    private long _bytesServed;
    private static readonly Lazy<byte[]> Engine = new(() =>
    {
        using var stream = typeof(WallpaperWebHost).Assembly.GetManifestResourceStream("FMLauncher.WallpaperEngine.js")
            ?? throw new FileNotFoundException("启动器缺少壁纸渲染资源，请重新安装完整版本。");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    public string Url { get; }
    public long BytesServed => Interlocked.Read(ref _bytesServed);

    public WallpaperWebHost(string sourcePath, string projectPath, string kind, string properties)
    {
        _sourcePath = Path.GetFullPath(sourcePath);
        _projectPath = Path.GetExtension(projectPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
            && File.Exists(projectPath) ? Path.GetFullPath(projectPath) : "";
        _kind = kind.ToLowerInvariant();
        _properties = properties;
        // 图片预设允许引用项目之外的原图；文件源仍仅开放该图片所在目录。
        _root = Path.GetDirectoryName(_kind == "image" || _projectPath.Length == 0 ? _sourcePath : _projectPath)!;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _loop = Task.Run(AcceptLoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _listener.Stop();
        try { await _loop; }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        _cancellation.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cancellation.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();
                string? request = await ReadRequestAsync(stream, _cancellation.Token);
                if (request is null) return;
                string[] lines = request.Split("\r\n", StringSplitOptions.None);
                string[] parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return;
                string method = parts[0];
                if (method is not ("GET" or "HEAD")) { await WriteStatusAsync(stream, "405 Method Not Allowed"); return; }
                string path = Uri.UnescapeDataString(parts[1].Split('?')[0]);
                if (path is "/" or "/index.html")
                {
                    await WriteAsync(stream, method, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(HostPage()));
                    return;
                }
                if (path == "/engine/webwallgl.global.min.js")
                {
                    await WriteAsync(stream, method, "text/javascript; charset=utf-8", Engine.Value);
                    return;
                }
                if (path == "/wallpaper/project.json")
                {
                    JsonObject project = _projectPath.Length > 0
                        ? JsonNode.Parse(await File.ReadAllTextAsync(_projectPath, _cancellation.Token)) as JsonObject ?? new() : new();
                    project["type"] = _kind;
                    if (_kind != "scene" || Path.GetExtension(_sourcePath).Equals(".pkg", StringComparison.OrdinalIgnoreCase))
                        project["file"] = Path.GetRelativePath(_root, _sourcePath).Replace('\\', '/');
                    await WriteAsync(stream, method, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(project.ToJsonString()));
                    return;
                }
                const string prefix = "/wallpaper/";
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) { await WriteStatusAsync(stream, "404 Not Found"); return; }
                string file = Path.GetFullPath(Path.Combine(_root, path[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
                string root = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                { await WriteStatusAsync(stream, "404 Not Found"); return; }
                string? range = lines.FirstOrDefault(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))?[6..].Trim();
                await WriteFileAsync(stream, method, file, range);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // 切换壁纸时浏览器会主动中断旧连接。
            catch (Exception error) { WallpaperDiagnostics.Write("壁纸文件服务：" + error.Message); }
        }
    }

    private string HostPage()
    {
        string entry = string.Join('/', Path.GetRelativePath(_root, _sourcePath).Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        bool media = _kind == "image";
        string source = media
            ? "WebWallGL.mediaSource(" + JsonSerializer.Serialize(Url + "wallpaper/" + entry) + ", {type:" + JsonSerializer.Serialize(Path.GetExtension(_sourcePath).Equals(".gif", StringComparison.OrdinalIgnoreCase) ? "gif" : "image") + "})"
            : "WebWallGL.httpSource(" + JsonSerializer.Serialize(Url + "wallpaper") + ")";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent}#wp{position:fixed;inset:0}</style>
            </head><body><div id="wp"></div><script src="/engine/webwallgl.global.min.js"></script><script>
            let failed = false, closed = false, pendingPaused = false;
            let pendingProperties = {{JsonSerializer.Serialize(_properties)}};
            function fail(error) {
                if (failed || closed) return;
                failed = true;
                try { (window.__fmlWallpaper || error?.instance)?.destroy({releasePkgCache:true}); } catch (_) {}
                const detail = (error?.message || String(error)) + (error?.stack ? "\n" + error.stack : "");
                window.chrome?.webview?.postMessage("wallpaper-error:" + detail);
            }
            window.addEventListener("error", event => { if (event.message) fail(event.error || event.message); });
            window.addEventListener("unhandledrejection", event => fail(event.reason));
            window.fmlSetProperties = function(json) {
                pendingProperties = json;
                if (window.__fmlWallpaper) try { window.__fmlWallpaper.setProperties(JSON.parse(json)); } catch (error) { fail(error); }
            };
            window.fmlSetBlur = function(percent) {
                const radius = Math.max(0, Math.min(100, Number(percent) || 0)) * 0.5;
                const target = document.querySelector("#wp");
                target.style.filter = radius > 0 ? `blur(${radius}px)` : "none";
                target.style.transform = radius > 0 ? "scale(1.08)" : "none";
            };
            window.fmlSetPaused = function(paused) {
                pendingPaused = !!paused;
                const wp = window.__fmlWallpaper;
                if (wp) pendingPaused ? wp.pause() : wp.resume();
            };
            window.addEventListener("pagehide", () => {
                closed = true;
                try { window.__fmlWallpaper?.destroy({releasePkgCache:true}); } catch (_) {}
            });
            (async function() {
                try {
                    if (!window.WebWallGL) throw new Error("壁纸渲染资源未加载");
                    const wp = await WebWallGL.mount(document.querySelector("#wp"), {
                        source: {{source}}, fps:30, volume:0, audio:null, media:null,
                        renderDpr:Math.min(devicePixelRatio || 1, 1.5, 1920 / Math.max(innerWidth, innerHeight, 1)),
                        mountTimeoutMs:60000, properties:JSON.parse(pendingProperties || "{}"),
                        onError:fail
                    });
                    if (closed || failed) { wp.destroy({releasePkgCache:true}); return; }
                    window.__fmlWallpaper = wp;
                    window.fmlSetProperties(pendingProperties);
                    window.fmlSetPaused(pendingPaused);
                    window.chrome?.webview?.postMessage("wallpaper-ready");
                } catch(error) { fail(error); }
            })();
            </script></body></html>
            """;
    }

    private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        using var memory = new MemoryStream();
        while (memory.Length < 64 * 1024)
        {
            int read = await stream.ReadAsync(buffer, token);
            if (read == 0) return null;
            memory.Write(buffer, 0, read);
            string text = Encoding.ASCII.GetString(memory.GetBuffer(), 0, (int)memory.Length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }
        return null;
    }

    /// <summary>支持浏览器的字节范围请求，网页壁纸中的视频无需每次重新传输整个文件。</summary>
    private async Task WriteFileAsync(NetworkStream stream, string method, string path, string? range)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long start = 0, end = file.Length - 1;
        bool partial = !string.IsNullOrWhiteSpace(range);
        if (partial)
        {
            if (!range!.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || range.Contains(','))
            { await WriteStatusAsync(stream, "416 Range Not Satisfiable"); return; }
            string[] pair = range[6..].Split('-', 2);
            if (pair.Length != 2 || file.Length == 0) { await WriteStatusAsync(stream, "416 Range Not Satisfiable"); return; }
            if (pair[0].Length == 0 && long.TryParse(pair[1], out long suffix) && suffix > 0) start = Math.Max(0, file.Length - suffix);
            else if (!long.TryParse(pair[0], out start) || start < 0) { await WriteStatusAsync(stream, "416 Range Not Satisfiable"); return; }
            else if (pair[1].Length > 0 && (!long.TryParse(pair[1], out end) || end < start))
            { await WriteStatusAsync(stream, "416 Range Not Satisfiable"); return; }
            if (start >= file.Length) { await WriteStatusAsync(stream, "416 Range Not Satisfiable"); return; }
            end = Math.Min(end, file.Length - 1);
        }
        long length = Math.Max(0, end - start + 1);
        string extra = partial ? $"Content-Range: bytes {start}-{end}/{file.Length}\r\n" : "";
        string headers = $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\nContent-Type: {ContentType(path)}\r\nContent-Length: {length}\r\nAccept-Ranges: bytes\r\n{extra}Cache-Control: no-cache\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _cancellation.Token);
        if (method == "HEAD") return;
        file.Position = start;
        byte[] buffer = new byte[64 * 1024];
        while (length > 0)
        {
            int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), _cancellation.Token);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), _cancellation.Token);
            Interlocked.Add(ref _bytesServed, read);
            length -= read;
        }
    }
    private async Task WriteAsync(NetworkStream stream, string method, string type, byte[] bytes)
    {
        string headers = $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _cancellation.Token);
        if (method != "HEAD") await stream.WriteAsync(bytes, _cancellation.Token);
        Interlocked.Add(ref _bytesServed, bytes.Length);
    }
    private async Task WriteStatusAsync(NetworkStream stream, string status) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _cancellation.Token);
    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8", ".json" => "application/json; charset=utf-8",
        ".png" or ".apng" or ".pnga" => "image/png", ".jpg" or ".jpeg" or ".jfif" => "image/jpeg",
        ".svg" => "image/svg+xml", ".webp" => "image/webp", ".avif" => "image/avif", ".gif" => "image/gif", ".bmp" => "image/bmp",
        ".mp4" or ".m4v" => "video/mp4", ".webm" => "video/webm", ".ogv" => "video/ogg",
        ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", ".wav" => "audio/wav", ".flac" => "audio/flac",
        ".woff" => "font/woff", ".woff2" => "font/woff2", ".ttf" => "font/ttf", ".otf" => "font/otf", ".wasm" => "application/wasm",
        _ => "application/octet-stream"
    };
}
