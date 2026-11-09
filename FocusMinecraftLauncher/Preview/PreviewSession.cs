using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Navigation;
using FocusMinecraftLauncher.Features.Home.Models;
namespace FocusMinecraftLauncher.Preview;

/// <summary>共享账号、实例目录、真实任务和持久化设置，页面之间不维护独立的业务缓存。</summary>
public sealed partial class PreviewSession : ObservableObject, IDisposable
{
    private PreviewAccount? _account;
    private PreviewAccount? _lastMicrosoftAccount, _lastOfflineAccount, _defaultAccount;
    private bool _microsoftMode;
    private PreviewInstance? _instance;
    private PreviewTask? _active, _launch;
    private string _error = "", _theme = Presentation.UiTheme.GlassThemeName;
    private bool _loading, _animations = true;
    private double _glassTransparency = 50;
    private int _tab;
    public ObservableCollection<PreviewAccount> Accounts { get; } = [];
    public ObservableCollection<PreviewInstance> Instances { get; } = [];
    public ObservableCollection<PreviewVersion> Versions { get; } = [];
    public ObservableCollection<PreviewTask> Tasks { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public PreviewSettings SavedSettings { get; private set; } = new();
    public event Action<MainPage>? NavigateRequested;
    public PreviewAccount? CurrentAccount
    {
        get => _account;
        set
        {
            if (ReferenceEquals(_account, value)) return;
            if (_account != null) _account.IsCurrent = false;
            Set(ref _account, value);
            if (!IsLaunching) _launch = null;
            if (value != null)
            {
                value.IsCurrent = true;
                _microsoftMode = value.IsOnline;
                if (value.IsOnline) _lastMicrosoftAccount = value; else _lastOfflineAccount = value;
            }
            Signal(); SaveAccountSelection();
        }
    }
    /// <summary>默认账号排在列表最前，启动器打开时优先进选它。</summary>
    public PreviewAccount? DefaultAccount => _defaultAccount;
    public void SetDefaultAccount(PreviewAccount? account)
    {
        if (account != null && !Accounts.Contains(account)) throw new InvalidOperationException("这个账号已被移除。");
        _defaultAccount = account;
        foreach (var item in Accounts) item.IsDefault = ReferenceEquals(item, account);
        if (account != null && Accounts.IndexOf(account) > 0) Accounts.Move(Accounts.IndexOf(account), 0);
        SaveAccountSelection(); Signal();
    }
    /// <summary>保存身份来源和各来源选择，不转换或重新创建账号。</summary>
    public bool MicrosoftMode
    {
        get => _microsoftMode;
        set
        {
            if (_microsoftMode == value) return;
            _microsoftMode = value;
            var remembered = value ? _lastMicrosoftAccount : _lastOfflineAccount;
            CurrentAccount = remembered != null && Accounts.Contains(remembered)
                ? remembered : Accounts.FirstOrDefault(a => a.IsMicrosoft == value);
            Signal(); SaveAccountSelection();
        }
    }
    public bool OfflineMode { get => !MicrosoftMode; set { if (value) MicrosoftMode = false; } }
    public PreviewInstance? CurrentInstance { get => _instance; set { if (Set(ref _instance, value)) { if (!IsLaunching) _launch = null; Signal(); SaveSelectedInstance(); } } }
    public PreviewTask? ActiveTask => Tasks.FirstOrDefault(task=>task.IsActive) ?? _active;
    public PreviewTask? LaunchTask => _launch;
    public bool HasTask => Tasks.Count > 0;
    public bool IsBusy => IsApplyingIsolation || IsRemovingInstance || IsInstallingLauncherUpdate || Tasks.Any(task => task.IsActive);
    /// <summary>下载、安装与补全只是后台写文件，不该锁住换账号、换实例与改设置；只有游戏运行中、删实例、切隔离与装更新才需要独占。</summary>
    public bool IsEditingBlocked => IsApplyingIsolation || IsRemovingInstance || IsInstallingLauncherUpdate || IsLaunching;
    public bool IsLaunching => _launch?.IsActive == true;
    public bool HasInstances => Instances.Count > 0;
    public bool HasAccounts => Accounts.Count > 0;
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public string InstanceError { get => _error; set => Set(ref _error, value); }
    public bool CanLaunch => CurrentInstance != null && CurrentAccount?.IsValid == true && !IsBusy && !IsLoading && string.IsNullOrEmpty(InstanceError);
    public bool AnimationsEnabled { get => _animations; set { if (Set(ref _animations, value)) { Changed(nameof(MotionAllowed)); Changed(nameof(DynamicBlurHint)); SaveAppearance(); } } }
    public bool MotionAllowed => AnimationsEnabled && System.Windows.SystemParameters.ClientAreaAnimation;
    public string Theme
    {
        get => _theme;
        set
        {
            value = Presentation.UiTheme.NormalizeTheme(value);
            if (_theme == value) return;
            _theme = value; _glassTransparency = ReadThemeTransparency(value);
            SelectThemeBackground();
            Changed(); Changed(nameof(GlassTransparency));
            Presentation.UiTheme.Apply(value, GlassTransparency, HasCustomBackgroundImage || HasWallpaper); Signal(); SaveAppearance();
        }
    }
    /// <summary>保存当前主题的玻璃透明度，其他主题的定制值分别保留。</summary>
    public double GlassTransparency
    {
        get => _glassTransparency;
        set
        {
            if (Set(ref _glassTransparency, Math.Clamp(value, 0, 100)))
            { Presentation.UiTheme.ApplyGlassTransparency(Theme, _glassTransparency, HasCustomBackgroundImage || HasWallpaper); SaveAppearance(); }
        }
    }
    public int DownloadTab { get => _tab; set => Set(ref _tab, value); }
    private ResourceFilter? _pendingResourceFilter;
    /// <summary>每次从游戏页进入“下载新 Mod”均通知下载页应用条件；读取后清空，供下次入口重新触发。</summary>
    public ResourceFilter? PendingResourceFilter
    { get => _pendingResourceFilter; set => Set(ref _pendingResourceFilter, value); }
    private ResourceDetailRequest? _pendingResourceDetail;
    /// <summary>通知已创建或首次打开的下载页读取指定资源；接收方取出后清空。</summary>
    public ResourceDetailRequest? PendingResourceDetail
    { get => _pendingResourceDetail; set => Set(ref _pendingResourceDetail, value); }
    public UiCommand Launch { get; }
    public UiCommand CancelTask { get; }
    public UiCommand RetryTask { get; }
    public UiCommand OpenTasks { get; }
    public UiCommand RefreshInstances { get; }
    public PreviewSession()
    {
        Launch = new(async _ => await BeginLaunchAsync(), _ => CanLaunch);
        CancelTask = new(p => Cancel(p as PreviewTask ?? _active), p => (p as PreviewTask ?? _active)?.IsActive == true);
        RetryTask = new(p => Retry(p as PreviewTask), p => p is PreviewTask { CanRetry: true });
        OpenTasks = new(_ => { DownloadTab = 1; Navigate(MainPage.Downloads); });
        RefreshInstances = new(async _ => await RefreshInstancesAsync(), _ => !IsLoading);
        RestoreConfiguration();
    }
    public void Navigate(MainPage page) => NavigateRequested?.Invoke(page);
    public void OpenVersions() { DownloadTab = 0; Navigate(MainPage.Downloads); }
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logBuffer = new();
    private int _logScheduled;
    public void Log(string text)
    {
        _logBuffer.Enqueue(DateTime.Now.ToString("HH:mm:ss")+"  "+text);
        if(Interlocked.Exchange(ref _logScheduled,1)==0)_ = FlushLogBufferAsync();
    }
    private async Task FlushLogBufferAsync()
    {
        try{await Task.Delay(100,_lifetime.Token);}
        catch(OperationCanceledException){return;}
        PostUi(()=>
        {
            var batch=new Queue<string>();
            while(_logBuffer.TryDequeue(out var text)){batch.Enqueue(text);if(batch.Count>1000)batch.Dequeue();}
            var tokens=Accounts.Select(a=>a.RuntimeAccount?.AccessToken).Where(t=>!string.IsNullOrEmpty(t)&&t!="0").ToArray();
            foreach(var line in batch)
            {
                var safe=line;foreach(var token in tokens)safe=safe.Replace(token!,"[已隐藏令牌]",StringComparison.Ordinal);
                Logs.Insert(0,Infrastructure.Runtime.RuntimeSupport.SafeText(safe));
            }
            while(Logs.Count>1000)Logs.RemoveAt(Logs.Count-1);
            Interlocked.Exchange(ref _logScheduled,0);
            if(!_logBuffer.IsEmpty&&Interlocked.Exchange(ref _logScheduled,1)==0)_ = FlushLogBufferAsync();
        });
    }
    public void Signal() { Refresh(); CommandManager.InvalidateRequerySuggested(); }
    public void Dispose() { _instanceWatcher?.Dispose(); DisposePersistence(); foreach (var operation in _operations.Where(pair=>!pair.Key.IsRunningGame)) operation.Value.Cancel(); }
}
public sealed class PreviewSettings
{
    public Features.Home.Models.VersionIsolationMode VersionIsolation { get; set; } = Features.Home.Models.VersionIsolationMode.All;
    public string JavaPath { get; set; } = "";
    public string GameDirectory { get; set; } = System.IO.Path.Combine(AppContext.BaseDirectory, ".minecraft");
    public int MemoryMb { get; set; } = 4096;
    /// <summary>首次启动没有配置项时默认按实例 Mod 数量自动分配。</summary>
    public bool AutomaticMemory { get; set; } = true;
    public bool OptimizeMemory { get; set; }
    public string JvmArguments { get; set; } = "";
    /// <summary>垃圾回收器：Auto、G1GC、ZGC 或 None；自动模式使用游戏或 Java 的默认选择。</summary>
    public string GcMode { get; set; } = "Auto";
    /// <summary>外置账号启动所需的 authlib-injector；留空时由启动器下载到自己的目录。</summary>
    public string AuthlibInjectorPath { get; set; } = "";
    public PreviewSettings Copy() => (PreviewSettings)MemberwiseClone();
}
