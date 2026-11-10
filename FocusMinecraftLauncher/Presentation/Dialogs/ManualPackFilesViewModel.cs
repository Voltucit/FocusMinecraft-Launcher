using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

internal sealed class ManualPackFileRow(PackFile file) : ObservableObject
{
    public PackFile File { get; } = file;
    public string SourcePath { get; private set; } = "";
    public string Status { get; private set; } = "请选择对应版本的文件";
    public bool IsReady => SourcePath.Length > 0;
    public bool HasError { get; private set; }
    public void Select(string path, string error = "")
    {
        SourcePath = error.Length == 0 ? path : "";
        HasError = error.Length > 0;
        Status = HasError ? error : "已校验";
        Changed(nameof(SourcePath)); Changed(nameof(Status)); Changed(nameof(IsReady)); Changed(nameof(HasError));
    }
}

internal sealed class ManualPackFilesViewModel : ObservableObject, IDisposable
{
    private readonly Window _owner;
    private readonly CancellationTokenSource _lifetime;
    private bool _busy;
    private string _notice = "";
    public IReadOnlyList<ManualPackFileRow> Rows { get; }
    public string Summary => _busy ? "正在校验文件" : $"{Rows.Count(row => row.IsReady)}/{Rows.Count} 项已校验";
    public bool CanContinue => !_busy && Rows.All(row => row.IsReady);
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public IReadOnlyDictionary<string, string> Sources => Rows.ToDictionary(row => row.File.Path, row => row.SourcePath, StringComparer.OrdinalIgnoreCase);
    public UiCommand OpenPage { get; }
    public UiCommand SelectFile { get; }
    public UiCommand SelectMany { get; }

    public ManualPackFilesViewModel(Window owner, IReadOnlyList<PackFile> files, CancellationToken token)
    {
        _owner = owner;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Rows = files.Select(file => new ManualPackFileRow(file)).ToArray();
        OpenPage = new(item => Open((ManualPackFileRow)item!), item => item is ManualPackFileRow row && ModpackArchive.IsCurseForgePage(row.File.DownloadPage));
        SelectFile = new(async item => await ChooseOneAsync((ManualPackFileRow)item!), item => !_busy && item is ManualPackFileRow);
        SelectMany = new(async _ => await ChooseManyAsync(), _ => !_busy);
    }

    private void Open(ManualPackFileRow row)
    {
        try { Process.Start(new ProcessStartInfo(row.File.DownloadPage) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        { Notice = "无法打开官网：" + RuntimeSupport.Error(error); }
    }

    private static OpenFileDialog Picker(bool multiple) => new()
    {
        Title = multiple ? "选择已下载的整合包资源" : "选择指定版本的资源文件",
        Filter = "整合包资源|*.jar;*.zip|所有文件|*.*", Multiselect = multiple, CheckFileExists = true
    };

    private async Task ChooseOneAsync(ManualPackFileRow row)
    {
        var picker = Picker(false);
        if (picker.ShowDialog(_owner) != true) return;
        var token = _lifetime.Token;
        SetBusy(true);
        try
        {
            var matches = await Task.Run(() => ModpackArchive.MatchesFileAsync(row.File, picker.FileName, token), token);
            token.ThrowIfCancellationRequested();
            row.Select(picker.FileName, matches ? "" : "文件与指定版本不符，请重新选择");
            Notice = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { row.Select("", "无法读取文件：" + RuntimeSupport.Error(error)); }
        finally { SetBusy(false); }
    }

    private async Task ChooseManyAsync()
    {
        var picker = Picker(true);
        if (picker.ShowDialog(_owner) != true) return;
        var token = _lifetime.Token;
        SetBusy(true);
        var matched = 0;
        var unmatched = 0;
        try
        {
            foreach (var path in picker.FileNames)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var size = new FileInfo(path).Length;
                    var candidates = Rows.Where(row => !row.IsReady && row.File.Size == size).ToArray();
                    if (candidates.Length == 0) { unmatched++; continue; }
                    var hash = await Task.Run(async () =>
                    {
                        await using var stream = File.OpenRead(path);
                        return Convert.ToHexString(await SHA1.HashDataAsync(stream, token));
                    }, token);
                    var found = false;
                    foreach (var row in candidates.Where(row => row.File.Sha1.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!await Task.Run(() => ModpackArchive.MatchesFileAsync(row.File, path, token), token)) continue;
                        token.ThrowIfCancellationRequested();
                        row.Select(path); matched++; found = true;
                    }
                    if (!found) unmatched++;
                    RefreshState();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { unmatched++; Notice = "部分文件无法读取：" + RuntimeSupport.Error(error); }
            }
            Notice = unmatched > 0 ? $"已匹配 {matched} 项，另有 {unmatched} 个文件未匹配所需版本。" : $"已匹配 {matched} 项文件。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value) { _busy = value; RefreshState(); }
    private void RefreshState()
    {
        Changed(nameof(Summary)); Changed(nameof(CanContinue));
        CommandManager.InvalidateRequerySuggested();
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
