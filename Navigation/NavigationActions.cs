using System.Windows.Input;
namespace FMLauncher.Navigation;
/// <summary>页面仅发出导航请求，主窗口统一处理，不创建窗口或业务服务。</summary>
public static class NavigationActions
{
    public static RoutedUICommand OpenNews { get; } = new("打开官方资讯", nameof(OpenNews), typeof(NavigationActions));
    public static RoutedUICommand CloseAccounts { get; } = new("关闭账号面板", nameof(CloseAccounts), typeof(NavigationActions));
    public static RoutedUICommand Open { get; } = new("打开页面", nameof(Open), typeof(NavigationActions));
    public static RoutedUICommand Back { get; } = new("返回", nameof(Back), typeof(NavigationActions));
    public static RoutedUICommand ReloadSettings { get; } = new("重新读取设置", nameof(ReloadSettings), typeof(NavigationActions));
}
