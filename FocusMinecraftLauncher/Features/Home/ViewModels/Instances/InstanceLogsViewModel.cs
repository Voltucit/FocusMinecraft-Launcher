using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Home.ViewModels.Instances;

/// <summary>游戏日志与崩溃报告：读末尾日志、按签名给出可能原因，并列出崩溃报告。</summary>
public sealed class InstanceLogsViewModel : ObservableObject
{
    private readonly PreviewInstance _instance;
    private string _notice="",_text="";
    private bool _busy;
    private LogFinding? _selectedFinding;
    private CrashRow? _selectedCrash;
    public ObservableCollection<LogFinding> Findings { get; }=[];
    public ObservableCollection<CrashRow> Crashes { get; }=[];
    public UiCommand Reload {get;}
    public UiCommand OpenFolder {get;}
    public UiCommand CopyReport {get;}
    public UiCommand OpenCrash {get;}
    public bool IsBusy {get=>_busy;private set{ if(!Set(ref _busy,value))return; Changed(nameof(IsIdle)); Changed(nameof(StateText)); }}
    public bool IsIdle=>!_busy;
    public bool HasText=>_text.Length>0;
    public bool HasFindings=>Findings.Count>0;
    public bool HasCrashes=>Crashes.Count>0;
    public bool HasAdvice=>SelectedFinding is not null;
    public string Notice {get=>_notice;private set=>Set(ref _notice,value);}
    public string Text {get=>_text;private set{ if(!Set(ref _text,value))return; Changed(nameof(HasText)); }}
    public string StateText=>_busy?"正在读取日志…":HasText?"已载入日志末尾":"还没有日志。启动一次游戏后会自动写入 logs\\latest.log。";
    public LogFinding? SelectedFinding
    {
        get=>_selectedFinding;
        set { if(!Set(ref _selectedFinding,value))return; Changed(nameof(HasAdvice)); Changed(nameof(SelectedAdvice)); }
    }
    public string SelectedAdvice=>SelectedFinding?.Advice ?? "";
    public CrashRow? SelectedCrash {get=>_selectedCrash;set=>Set(ref _selectedCrash,value);}
    public InstanceLogsViewModel(PreviewInstance instance)
    {
        _instance=instance;
        Reload=new(async _=>await RefreshAsync(),_=>!_busy);
        OpenFolder=new(_=>{try{RuntimeSupport.OpenFolder(Path.GetDirectoryName(GameLogs.LogPath(instance))!);}catch(Exception error){Notice=RuntimeSupport.Error(error);}});
        CopyReport=new(_=>{if(_text.Length==0)return;try{System.Windows.Clipboard.SetText(BuildReport());Notice="已把日志与诊断结果复制到剪贴板。";}catch(Exception error){Notice=RuntimeSupport.Error(error);}},_=>HasText);
        OpenCrash=new(_=>{if(SelectedCrash is not {} row)return;try{RuntimeSupport.OpenFolder(Path.GetDirectoryName(row.Path)!);}catch(Exception error){Notice=RuntimeSupport.Error(error);}},_=>SelectedCrash is not null);
        _ = RefreshAsync();
    }
    private string BuildReport()
    {
        var builder=new System.Text.StringBuilder();
        builder.AppendLine("=== 诊断结果 ===");
        if(Findings.Count==0)builder.AppendLine("没有识别到已知问题。");
        foreach(var finding in Findings)builder.AppendLine("["+finding.Title+"] "+finding.Detail+Environment.NewLine+"建议："+finding.Advice);
        builder.AppendLine();
        builder.AppendLine("=== 崩溃报告 ===");
        foreach(var crash in Crashes)builder.AppendLine(crash.Title);
        builder.AppendLine();
        builder.AppendLine("=== 日志末尾 ===");
        builder.Append(_text);
        return builder.ToString();
    }
    private async Task RefreshAsync()
    {
        if(_busy)return;
        IsBusy=true;Notice="";
        try
        {
            Crashes.Clear();
            foreach(var crash in GameLogs.Crashes(_instance))Crashes.Add(new CrashRow(crash));
            var text=await GameLogs.ReadLatestAsync(_instance,CancellationToken.None);
            Text=text;
            Findings.Clear();
            foreach(var finding in GameLogs.Analyze(text))Findings.Add(finding);
            SelectedFinding=Findings.FirstOrDefault();
            if(!HasText&&!HasCrashes)Notice="这个游戏还没有日志，先启动一次游戏。";
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);}
        finally{IsBusy=false;Changed(nameof(HasFindings));Changed(nameof(HasCrashes));Changed(nameof(HasAdvice));CommandManager.InvalidateRequerySuggested();}
    }
    public void Dispose(){}
}
public sealed class CrashRow(FileInfo file):ObservableObject
{
    public string Path {get;}=file.FullName;
    public string Title=>file.Name;
    public string Detail=>file.LastWriteTime.ToString("yyyy-MM-dd HH:mm")+" · "+RuntimeSupport.FormatSize(file.Length);
}
