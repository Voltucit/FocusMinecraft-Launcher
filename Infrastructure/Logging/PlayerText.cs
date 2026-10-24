using FMCore.Launch;
namespace FMLauncher.Infrastructure.Logging;
/// <summary>玩家可见的阶段与启动结果；原始核心错误码仅进入诊断日志。</summary>
public static class PlayerText
{
    public static string Stage(string stage) => stage switch
    {
        "Metadata" or "Manifest" or "Profile" => "正在读取游戏信息",
        "Java" => "正在检查运行环境",
        "Account" => "正在确认账号",
        "Installation" or "Install" => "正在准备游戏文件",
        "Client" => "正在下载游戏主体",
        "Libraries" => "正在下载所需组件",
        "AssetIndex" or "AssetObjects" or "Assets" => "正在下载游戏资源",
        "AssetLayout" or "Natives" or "Logging" or "Arguments" => "正在整理启动文件",
        "Process" => "正在打开游戏",
        "DeviceAuthorization" => "正在准备 Microsoft 登录",
        "WaitingForAuthorization" => "请在 Microsoft 页面完成授权",
        "MicrosoftToken" or "XboxUserToken" or "XstsToken" or "MinecraftToken" => "正在确认登录授权",
        "Entitlements" => "正在检查游戏资格",
        "Completed" => "已完成",
        _ => !string.IsNullOrWhiteSpace(stage) && stage[0] >= 0x4e00 && stage[0] <= 0x9fff ? stage : "正在处理，请稍候"
    };
    public static string State(LaunchState state) => state switch
    {
        LaunchState.Preparing => "正在准备游戏",
        LaunchState.Starting => "正在打开游戏",
        LaunchState.Running => "游戏已启动",
        LaunchState.Stopping => "正在停止游戏",
        LaunchState.Exited => "游戏已退出",
        LaunchState.Canceled => "已取消启动",
        LaunchState.Failed => "游戏未能启动",
        _ => "准备就绪"
    };
    public static string LaunchFailure(LaunchError? error) => error?.Code switch
    {
        LaunchErrorCode.AlreadyRunning or LaunchErrorCode.InstanceBusy => "这个游戏正在运行或准备中，请先结束当前游戏。",
        LaunchErrorCode.AuthenticationError => "登录状态已失效，请重新登录后再试。",
        LaunchErrorCode.VersionError => "这个版本的信息不完整或暂不兼容，请在版本管理中检查。",
        LaunchErrorCode.JavaError => "当前 Java 不适合这个版本，请在启动设置中选择兼容的运行环境。",
        LaunchErrorCode.MissingFiles => "游戏文件不完整，请在游戏管理中检查和修复。",
        LaunchErrorCode.InstallationError => "游戏文件准备失败，请检查和修复后再试。",
        LaunchErrorCode.UnsafePath => "游戏目录包含不安全的路径，请选择正常的本地文件夹。",
        LaunchErrorCode.IoError => "无法读取游戏文件，请检查文件夹权限和磁盘空间。",
        LaunchErrorCode.InvalidArguments or LaunchErrorCode.InvalidConfig => "启动设置有冲突或内容不正确，请检查后再试。",
        LaunchErrorCode.ProcessStartFailed => "无法打开游戏，请检查 Java 是否可用。",
        _ => "游戏未能启动，可在“更多 → 诊断日志”查看原因。"
    };
}
