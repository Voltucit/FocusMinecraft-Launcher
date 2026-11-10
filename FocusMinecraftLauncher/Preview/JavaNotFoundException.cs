namespace FocusMinecraftLauncher.Preview;

/// <summary>启动或安装需要 Java，但本机没有可用运行时时抛出；UI 据此展示下载或手动选择引导。</summary>
public sealed class JavaNotFoundException(PreviewInstance instance, int? requiredMajor, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public PreviewInstance Instance { get; } = instance;
    public int? RequiredMajor { get; } = requiredMajor;
}
