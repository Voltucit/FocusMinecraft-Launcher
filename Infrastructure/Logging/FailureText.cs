using System.IO;
using System.Net.Http;
using System.Text.Json;
using FMCore.Authentication;
using FMCore.Java;
using FMCore.Launch;
using FMCore.Versions;
namespace FMLauncher.Infrastructure.Logging;
/// <summary>区分玩家能够采取行动的错误，详细类别由 DiagnosticLog 保存，不把代码或内部对象展示在默认界面。</summary>
public static class FailureText
{
    public static string For(Exception error) => error switch
    {
        OperationCanceledException => "操作已取消或等待超时。",
        AuthenticationException e => e.Code switch
        {
            AuthenticationErrorCode.GameNotOwned => "这个 Microsoft 账号未能验证 Minecraft Java 版游戏资格，请确认使用了正确的账号。",
            AuthenticationErrorCode.AuthorizationDeclined => "你取消了 Microsoft 授权，可以重新登录。",
            AuthenticationErrorCode.DeviceCodeExpired => "登录授权码已过期，请重新开始登录。",
            AuthenticationErrorCode.ClientRejected => "此版本的 Microsoft 登录服务配置不可用，请联系启动器开发者。",
            AuthenticationErrorCode.ReauthenticationRequired => "登录状态已失效，请重新登录。",
            AuthenticationErrorCode.ProfileMissing => "此账号尚未取得 Minecraft 玩家资料，请先完成游戏账号设置。",
            AuthenticationErrorCode.NetworkError or AuthenticationErrorCode.RequestTimeout => "登录服务连接失败，请检查网络后再试。",
            AuthenticationErrorCode.AccountNotFound => "这个账号已被移除，请选择其他账号。",
            AuthenticationErrorCode.StoreBusy => "账号资料正在使用，请稍后再试。",
            _ => "登录失败，请重新尝试；详细原因已记录到诊断日志。"
        },
        VersionException => "这个版本的信息暂时无法读取，请在游戏管理中检查文件。",
        JavaException => "运行环境不可用或与游戏不兼容，请检查 Java 设置。",
        LaunchException e => PlayerText.LaunchFailure(new(e.Code, "")),
        HttpRequestException => "连接服务失败，请检查网络后重试。",
        UnauthorizedAccessException => "无法访问这个文件夹，请检查权限后重试。",
        JsonException => "设置内容无法读取，请检查输入格式。",
        IOException => "文件操作未完成，请检查文件夹是否被占用以及磁盘空间。",
        InvalidOperationException or InvalidDataException => error.Message,
        ArgumentException => "输入的选项不正确，请检查后重试。",
        _ => "这次操作没有完成，可在“更多 → 诊断日志”查看原因。"
    };
}
