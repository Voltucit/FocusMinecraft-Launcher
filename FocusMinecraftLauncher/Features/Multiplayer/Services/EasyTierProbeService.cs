using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FocusMinecraftLauncher.Features.Multiplayer.Services;

/// <summary>用无 TUN 的临时 EasyTier 实例验证共享节点和私有网络凭据，不建立游戏房间。</summary>
public static class EasyTierProbeService
{
    public static async Task<string> ProbeAsync(string node, string networkName, string password, CancellationToken token)
    {
        if (!EasyTierComponents.IsInstalled)
            throw new IOException("联机组件未安装，无法测试私有网络；请先安装联机组件。");
        string work = await EasyTierComponents.PrepareProbeAsync(token).ConfigureAwait(false);
        Process? core = null;
        try
        {
            int rpc = FreePort();
            string identity = networkName.Trim();
            string secret = password.Length > 0 ? password : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            if (identity.Length == 0) identity = "FMLauncher-Probe-" + Guid.NewGuid().ToString("N");
            string config = Path.Combine(work, "probe.toml");
            await File.WriteAllTextAsync(config, BuildConfig(node, identity, secret), token).ConfigureAwait(false);
            var info = new ProcessStartInfo(Path.Combine(work, "easytier-core.exe"))
            {
                WorkingDirectory = work,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "--disable-env-parsing", "--config-file", config, "--rpc-portal", "127.0.0.1:" + rpc, "--rpc-portal-whitelist", "127.0.0.1/32", "--no-tun" })
                info.ArgumentList.Add(argument);
            core = Process.Start(info) ?? throw new IOException("无法启动 EasyTier 测试实例。");
            var output = DrainAsync(core.StandardOutput);
            var error = DrainAsync(core.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (!deadline.IsCancellationRequested)
            {
                if (core.HasExited) throw new IOException("EasyTier 测试实例提前退出：" + Trim(await error));
                try
                {
                    using JsonDocument peers = await QueryAsync(Path.Combine(work, "easytier-cli.exe"), work, rpc, "peer", deadline.Token).ConfigureAwait(false);
                    if (peers.RootElement.ValueKind == JsonValueKind.Array && peers.RootElement.EnumerateArray().Any(HasRemotePeer))
                        return "连接节点成功";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
                await Task.Delay(500, deadline.Token).ConfigureAwait(false);
            }
            throw new TimeoutException("节点未接受当前网络名称或密码，或节点不可用。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("测试节点连通性超时。");
        }
        finally
        {
            if (core != null)
            {
                try { if (!core.HasExited) core.Kill(true); } catch (Exception) { }
                try { await core.WaitForExitAsync().ConfigureAwait(false); } catch (Exception) { }
                core.Dispose();
            }
            EasyTierComponents.DeleteProbeDirectory(work);
        }
    }

    private static string BuildConfig(string node, string networkName, string secret)
    {
        static string Quote(string value) => JsonSerializer.Serialize(value);
        return $"""
            instance_id = "{Guid.NewGuid()}"
            instance_name = "FMLauncherProbe"
            hostname = "FMLauncherProbe"
            listeners = []
            [network_identity]
            network_name = {Quote(networkName)}
            network_secret = {Quote(secret)}
            [[peer]]
            uri = {Quote(node)}
            [flags]
            enable_encryption = true
            enable_exit_node = false
            accept_dns = false
            relay_network_whitelist = ""
            """;
    }

    private static bool HasRemotePeer(JsonElement peer)
    {
        string cost = peer.TryGetProperty("cost", out JsonElement costElement) ? costElement.ToString() : "";
        if (string.Equals(cost, "Local", StringComparison.OrdinalIgnoreCase)) return false;
        string ip = peer.TryGetProperty("ipv4", out JsonElement ipElement) ? ipElement.ToString() : "";
        return !string.IsNullOrWhiteSpace(ip) || cost.Length > 0;
    }

    private static async Task<JsonDocument> QueryAsync(string cli, string work, int rpc, string command, CancellationToken token)
    {
        var info = new ProcessStartInfo(cli)
        {
            WorkingDirectory = work,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "--rpc-portal", "127.0.0.1:" + rpc, "--output", "json", command }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法读取 EasyTier 测试状态。");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token);
            _ = DrainAsync(process.StandardError);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("EasyTier 测试实例尚未就绪。");
            return JsonDocument.Parse(await output.ConfigureAwait(false));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        try { return await reader.ReadToEndAsync().ConfigureAwait(false); }
        catch (Exception) { return ""; }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Trim(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim();
}
