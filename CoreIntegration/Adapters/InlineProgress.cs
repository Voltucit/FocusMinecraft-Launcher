namespace FMLauncher.CoreIntegration.Adapters;

/// <summary>SDK 回调可能来自多个后台线程，不捕获同步上下文；UI 由上层 Progress 或 Dispatcher 调度。</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

