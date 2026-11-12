using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>面向玩家的原因与建议，以及保留异常链的诊断记录。诊断不改变任务或游戏进程生命周期。</summary>
public sealed class LauncherError
{
    public string Id { get; } = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
    public DateTime Created { get; } = DateTime.Now;
    public string Action { get; }
    public string Stage { get; }
    public string Reason { get; }
    public string Advice { get; }
    public string Context { get; }
    public string Details { get; }
    public string RelatedInformation => Context == Reason ? "" : Context.Length > 1200 ? Context[..1200] + "…（完整信息见技术详情）" : Context;
    public string ReportPath { get; private set; } = "";
    public string SaveNotice { get; private set; } = "";
    public string Location => "出错位置：" + Stage;
    public string Reference => Created.ToString("yyyy-MM-dd HH:mm:ss") + "  错误编号：" + Id;
    public string Summary => Reason + Environment.NewLine + "建议：" + Advice;

    public LauncherError(Exception error, string action, string stage, Func<string, string>? sanitize = null, string context = "")
    {
        sanitize ??= RuntimeSupport.SafeText;
        Action = sanitize(action);
        Stage = sanitize(string.IsNullOrWhiteSpace(stage) ? action : stage);
        var diagnosis = Describe(error);
        Reason = sanitize(diagnosis.Reason);
        Advice = sanitize(diagnosis.Advice);
        var messages = string.Join(Environment.NewLine, Chain(error).Select(e => e.Message).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(8));
        if (Chain(error).OfType<GameExitException>().FirstOrDefault() is { Analysis.Length: > 0 } exit)
            messages += Environment.NewLine + exit.Analysis;
        Context = sanitize(string.IsNullOrWhiteSpace(context) ? messages : context + Environment.NewLine + messages);
        Details = sanitize(error.ToString());
    }

    public string BuildText() => $"FMLauncher {typeof(LauncherError).Assembly.GetName().Version}\n{Reference}\n操作：{Action}\n{Location}\n原因：{Reason}\n建议：{Advice}\n\n相关信息：\n{Context}\n\n技术详情：\n{Details}";

    /// <summary>优先保存在启动器日志目录；目录不可写时退回本机临时目录，均不影响原来的失败处理。</summary>
    public async Task SaveAsync()
    {
        var fileName = "FMLauncher-error-" + Id + ".log";
        foreach (var folder in new[] { Path.Combine(AppContext.BaseDirectory, "FMLauncher", "Logs", "Errors"), Path.Combine(Path.GetTempPath(), "FMLauncher", "Errors") })
        {
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, fileName);
                await File.WriteAllTextAsync(path, BuildText(), new UTF8Encoding(false)).ConfigureAwait(false);
                ReportPath = path;
                SaveNotice = "";
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { SaveNotice = "错误报告未能保存到文件，可使用“复制错误信息”。"; }
        }
    }

    public static (string Reason, string Advice) Describe(Exception error)
    {
        if (error is GameExitException exit) return (exit.Message, exit.Advice);
        if (error is Preview.JavaNotFoundException java)
            return (java.Message, "使用 Java 引导下载所需版本，或在游戏的启动设置中选择可用的 Java。");
        var chain = Chain(error).ToArray();
        // 文件系统错误优先于包装它的 HTTP/安装错误，避免把磁盘写入失败提示成网络故障。
        if (chain.OfType<UnauthorizedAccessException>().Any())
            return ("没有权限读取或写入所需文件。", "检查相关路径的访问权限，或改用当前用户可写的游戏目录；相关路径见错误详情。");
        var io = chain.OfType<IOException>().FirstOrDefault(e => (e.HResult & 0xffff) is 32 or 33 or 112 or 39 or 206);
        if (io != null)
            return (io.HResult & 0xffff) switch
            {
                112 or 39 => ("磁盘空间不足，无法继续写入文件。", "清理游戏目录和系统临时目录所在磁盘的空间后重试。"),
                206 => ("文件路径过长。", "把启动器或游戏目录移到较短的路径后重试。"),
                _ => ("所需文件被其他程序占用。", "关闭正在使用该文件的游戏、压缩软件或编辑器后重试；相关文件见错误详情。")
            };
        if (chain.OfType<FileNotFoundException>().FirstOrDefault() is { } missing)
            return (string.IsNullOrEmpty(missing.FileName) ? "找不到所需文件。" : "找不到文件：" + missing.FileName,
                "检查文件是否被移动或被安全软件隔离；游戏资源缺失时使用“检查并补全文件”。");
        if (chain.OfType<DirectoryNotFoundException>().Any())
            return ("所需文件夹不存在或所在磁盘不可用。", "检查错误详情中的路径、外接磁盘和游戏目录设置后重试。");
        if (chain.OfType<Win32Exception>().FirstOrDefault() is { } win32)
            return win32.NativeErrorCode switch
            {
                2 or 3 => ("无法启动程序：可执行文件或路径不存在。", "检查 Java 或相关组件的路径；文件可能已被移动或被安全软件隔离。"),
                5 or 740 => ("Windows 拒绝启动所需程序。", "检查程序的访问权限及安全软件拦截记录。"),
                193 or 216 => ("可执行文件格式或系统架构不兼容。", "重新选择适合当前 Windows 架构的 Java 或组件，勿选择安装包或损坏文件。"),
                1223 => ("操作被 Windows 或用户取消。", "如需继续，请重试并完成 Windows 权限确认。"),
                _ => ($"Windows 无法启动所需程序（错误 {win32.NativeErrorCode}）。", "查看错误详情中的程序路径与系统提示，检查 Java 或相关组件是否完整。")
            };
        if (chain.Any(e => e is AuthenticationException) || chain.OfType<HttpRequestException>().Any(e => e.HttpRequestError == HttpRequestError.SecureConnectionError))
            return ("无法建立安全连接（TLS/证书校验失败）。", "检查系统日期、代理和 HTTPS 拦截软件；不要关闭证书校验。仍失败时稍后重试或切换下载源。");
        if (chain.OfType<HttpRequestException>().FirstOrDefault(e => e.StatusCode.HasValue) is { } http)
        {
            var status = (int)http.StatusCode!;
            return status switch
            {
                401 => ("服务拒绝授权（HTTP 401）。", "登录相关操作请重新登录；下载操作请刷新资源信息后重试。"),
                403 => ("服务器拒绝访问（HTTP 403）。", "资源可能限制下载或地址已过期；刷新资源信息、切换下载源，或从资源官网获取文件。"),
                404 => ("服务器上找不到请求的资源（HTTP 404）。", "资源可能已被移除或地址已失效；刷新版本列表，或选择其他可用版本和下载源。"),
                407 => ("代理服务器要求身份验证（HTTP 407）。", "检查系统代理的账号与连接配置后重试。"),
                408 or 504 => ($"服务器响应超时（HTTP {status}）。", "检查网络和代理，稍后重试；下载时可使用自动选择或切换下载源。"),
                429 => ("请求过于频繁，服务器已限流（HTTP 429）。", "等待一段时间后重试，避免连续重复操作。"),
                >= 500 => ($"远端服务暂时异常（HTTP {status}）。", "稍后重试；下载时可切换下载源。此错误不代表本地游戏文件损坏。"),
                _ => ($"请求未被服务器接受（HTTP {status}）。", "查看相关信息中的服务提示，刷新资源或登录状态后重试。")
            };
        }
        if (chain.OfType<SocketException>().Any(e => e.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
            || chain.OfType<HttpRequestException>().Any(e => e.HttpRequestError == HttpRequestError.NameResolutionError))
            return ("无法解析服务器地址（DNS 查询失败）。", "检查网络、DNS 和代理是否可用，再重试或切换下载源。");
        if (chain.Any(e => e is TimeoutException or OperationCanceledException))
            return ("操作等待超时，未能在限定时间内完成。", "检查网络与代理后重试；下载时可切换下载源。若发生在本地安装阶段，请查看安装器输出。");
        if (chain.Any(e => e is HttpRequestException or SocketException))
            return ("无法连接服务器，或连接在传输过程中中断。", "检查网络、系统代理和防火墙后重试；相关服务及原始错误见错误详情。");
        if (chain.OfType<JsonException>().Any())
            return ("无法解析 JSON 配置或服务返回的数据。", "查看详情中的文件、行号与字段；本地配置有误时先备份再修正，网络数据有误时刷新或切换下载源。");
        var messages = string.Join("\n", chain.Select(e => e.Message));
        if (chain.Any(e => e is InvalidDataException) && new[] { "SHA-1", "SHA1", "SHA-512", "校验", "hash mismatch", "size mismatch", "大小不匹配" }.Any(key => messages.Contains(key, StringComparison.OrdinalIgnoreCase)))
            return ("文件完整性校验未通过。", "重新下载对应文件或切换下载源；不要跳过校验。整合包手动补充文件时需选择完全相同的版本。");
        if (chain.Any(e => e is InvalidDataException))
            return ("文件内容无效、损坏，或不符合要求的格式。", "查看相关信息中的具体文件；压缩包请重新获取完整文件，下载资源请刷新信息或切换下载源。");
        if (chain.Any(e => e is OutOfMemoryException))
            return ("启动器可用内存不足。", "关闭占用较高的程序后重试；使用动态壁纸时可改用静态壁纸。");
        if (error is NullReferenceException or IndexOutOfRangeException)
            return ("启动器处理数据时发生内部错误。", "请复制错误信息用于反馈，保留错误编号；原始异常及调用位置见技术详情。");
        // 保留调用方有意义的外层说明，不再只显示 GetBaseException 而丢失文件和业务上下文。
        var reason = error is AggregateException ? chain.FirstOrDefault(e => e is not AggregateException)?.Message : error.Message;
        return (string.IsNullOrWhiteSpace(reason) ? "操作未能完成，尚未识别出具体原因。" : reason,
            error is InvalidOperationException or ArgumentException or NotSupportedException
                ? "按上方提示检查相关选择或配置后重试；仍失败时复制错误信息用于反馈。"
                : "查看错误详情；若重试后仍失败，请提供错误编号和复制的错误信息。");
    }

    private static IEnumerable<Exception> Chain(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        {
            foreach (var child in aggregate.InnerExceptions)
                foreach (var entry in Chain(child)) yield return entry;
        }
        else if (error.InnerException is { } inner)
            foreach (var entry in Chain(inner)) yield return entry;
    }
}
