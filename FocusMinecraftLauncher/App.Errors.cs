using System.Windows;
using System.Windows.Threading;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Presentation.Dialogs;

namespace FocusMinecraftLauncher;

public partial class App
{
    private int _fatalErrorRecorded;
    private void InitializeErrorReporting()
    {
        DispatcherUnhandledException += ReportUnhandledUi;
        AppDomain.CurrentDomain.UnhandledException += ReportUnhandledProcess;
    }
    private void DisposeErrorReporting()
    {
        DispatcherUnhandledException -= ReportUnhandledUi;
        AppDomain.CurrentDomain.UnhandledException -= ReportUnhandledProcess;
    }
    private void ReportUnhandledUi(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (Interlocked.Exchange(ref _fatalErrorRecorded, 1) != 0) return;
        var report = new LauncherError(e.Exception, "启动器发生未处理异常，即将关闭", MainWindow?.IsLoaded == true ? "启动器界面操作" : "初始化启动器");
        try
        {
            report.SaveAsync().GetAwaiter().GetResult();
            ErrorDetailsDialog.Show(report);
        }
        catch
        {
            MessageBox.Show(report.Location + "\n" + report.Summary + "\n\n" + report.ReportPath,
                "FMLauncher 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        // 保留 WPF 原有的未处理异常退出行为，不把未知错误标为已处理后继续运行。
    }
    private void ReportUnhandledProcess(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception error || Interlocked.Exchange(ref _fatalErrorRecorded, 1) != 0) return;
        try { new LauncherError(error, "启动器未处理异常", "后台线程").SaveAsync().GetAwaiter().GetResult(); }
        catch { /* 进程正在退出；诊断写入失败不能再次抛出。 */ }
    }
}
