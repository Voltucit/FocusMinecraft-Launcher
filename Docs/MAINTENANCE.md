# 维护指南

## 页面与模块

1. 在 Features/<功能>/ViewModels 新建 PageViewModel 派生类，通过 AsyncCommand 绑定操作。参考 Features/Java。
2. 在 Views 新建接收 ViewModel 的 UserControl，XAML.cs 仅做界面行为。
3. 在 Infrastructure/LauncherServices 创建服务，将有异步任务的页面加入 ShutdownAsync 的取消和等待清单。
4. 修改 NavigationService 的页面注册与 Shell/MainWindow.CreatePage 映射。
5. 更新 MODULES 和 MANUAL_TESTING，并为重要业务及公共入口添加中文说明。

本项目使用简洁 MVVM 基础实现，AsyncCommand 统一忙状态、取消和安全错误反馈。不要把耗时业务放回窗口或按钮事件。

## 核心 API 与目录

依赖固定为 FocusMinecraftCore 1.0.1。先核对包中 FMCore.xml 和对应正式发布的官方手册，再在 CoreIntegration 接入；不引用本机核心项目、不反射内部实现、不重复实现核心已有下载及事务。

游戏工作目录必须始终为 `versions/<版本 ID>`。LaunchAdapter 明确传 GameCore.GameDirectory；InstallationAdapter 在原版、加载器、校验和修复时传相同 InstallationOptions.GameDirectory。加载器支持公开 VersionId 参数，因此可使用 SDK 生成指定版本 ID，而不是手改 JSON。原版保留官方 ID。

禁止恢复基于 instances 的 SDK 独立实例、Mod 安装或整包写入入口。不能把内容安装到另一目录后复制回来，也不能通过链接或反射绕过限制。未来核心正式支持自定义目录后，先写能力映射及数据保护方案再接入。

## 启动、Java 和凭据

- LaunchSettingsMapping：内存、窗口、服务器及每行一个完整参数；不要按空格拆参数。
- LaunchAdapter：账号身份和固定版本工作目录；启动禁用自动安装。
- GameHost / GameSessionService：SDK 生命周期、当前用户管道、日志和关闭策略。
- JavaAdapter：探测、官方包查询及用户明确请求的精确安装。兼容主版本由核心判断，不自动下载。
- 古老元数据没有 Java 要求时，可在游戏设置填写 FallbackJavaMajor；不猜测默认主版本，仍由 SDK 执行兼容检查。
- AccountAdapter：Microsoft 和离线通过正式 AccountManager。旧凭据不伪造转换，失败绝不回退离线。

后台事件只做快速入队；UI 由 Dispatcher / Progress / 定时合并处理。Dispose SDK 会停止其拥有的游戏；保留游戏必须由无窗口宿主继续持有对象。停止、等待与释放的顺序不能改为并发调用。

SettingsStore 将非机密配置保存到 LocalAppData。API 请求头、参数、Key 用 SecretStore；Microsoft 凭据交给核心。不要记录完整参数、OAuth 正文或令牌。SDK 已脱敏会话日志在用户 logs；原始游戏日志仍须用户检查后再分享。

## 主题和壁纸

新增预设修改 ThemeService.Preset。新增字段同时修改 ThemeSettings、Validate、Apply、AppearancePage、导入导出说明。统一语义资源，不在页面散落硬编码主题颜色。导入文件只允许有限 JSON，不执行 XAML 或脚本。

新增 Provider 实现 IWallpaperProvider，并在 WallpaperManager 接入。网络复用 PublicImageClient，保留 DNS 连接时校验、HTTPS / 公网 / 大小限制和不转发认证头规则。图片只接受单帧 JPEG / PNG / BMP / 系统 WIC 可解码 WebP。缓存清理只操作 WallpaperCache，不能删除本地原图。

## 构建与发布

在 Windows x64、.NET 10 SDK 下：

```powershell
dotnet restore FMLauncher.csproj
dotnet build FMLauncher.csproj -c Debug --no-restore
dotnet build FMLauncher.csproj -c Release --no-restore
dotnet publish FMLauncher.csproj -c Release --no-restore -p:PublishProfile=FrameworkDependent
```

框架依赖输出：artifacts/publish/framework-dependent，需 .NET 10 Windows Desktop Runtime x64。可选自包含：`dotnet publish FMLauncher.csproj -c Release -p:PublishProfile=SelfContainedWinX64`。该可选配置已提供，本次不声称已运行验收。

Release 不分发 PDB；PublishTrimmed=false；不要只分发 EXE，应保留完整发布目录。不得把游戏、用户设置、账号或 Key 加入源码和发布包。GitHub Actions 仅 restore / build，不自动功能测试或发布正式 Release。

## 问题定位

| 问题 | 优先检查 |
| --- | --- |
| 无法初始化 | SettingsStore、settings.v2.json / .bak；不要覆盖无法解析的未来格式 |
| 旧版本或存档不可见 | 全局 GameRoot；发布到新目录时点击“从旧游戏目录导入配置” |
| Java 不兼容 | 元数据主版本要求、手动 Java 路径、JavaAdapter |
| 正版失败 | 应用 ID / 权限、官方授权页、AccountAdapter；不降级离线 |
| 安装 / 修复失败 | InstallationAdapter、OperationService、SDK 分类错误 |
| 关闭 UI 后游戏退出 | KeepGamesOnExit、detach 协议、GameHost 是否仍在；不要提前 Dispose |
| 壁纸失败 | 最终公网 HTTPS URL、JSON 路径、重定向 / 代理限制、系统解码器 |
| 找不到 Mod / 整包安装按钮 | 当前正式 API 的目录与用户要求冲突，属于已明确移除的入口 |

所有实际功能由所有者按人工指南验证，不能以编译成功代替游戏、账号、网络或 UI 验收。

## 2026-10-24：界面维护

新增导航页时，在 NavigationService 的 Pages 注册 Key、分区、标题、图标和说明，在 MainWindow.CreatePage 接入页面；ViewModel 仍由 LauncherServices 统一持有。页面使用 NavigationActions.Open / Back 发出请求，不直接创建 MainWindow，不在切页时取消任务。页面缓存只保留 UI 状态，不能把它当作业务持久化。

顶部切分区保留各自最后访问页，侧栏选择作为新返回起点，页面内部入口可返回上一页。主题预览沿用既有 ThemeService；新增颜色先增加语义资源并在 Apply 同时定义深浅色值。所有进度和只读状态的双向敏感目标显式指定 OneWay。

设置草稿首次打开时刷新，切页保留；用户可明确点击“重新读取已保存设置”并确认放弃草稿。若后续需要自动合并跨页业务修改，先询问所有者，不在 UI 中暗中改写设置优先级。

## 安装后同步与开发者服务

接入安装完成必须等待 SDK 的成功结果，随后调用 InstanceCatalog.InstallationSucceededAsync，传入任务提交时的 GameRoot、SDK 返回 ID 与本次唯一操作 ID。不要按下载 100% 发布，不从下载代码操作其他页面控件；失败和取消不发完成通知。Home / Versions 只读共享集合，新增页面复用该服务；不要再添加私有本地版本缓存。订阅由组合根创建一次，Dispose 时退订。

当前列表查询与发布串行、UI 线程更新、目录 / 代次隔离；完成后刷新失败独立反馈，不把安装成功改为安装失败。修改时重点人工核对目录切换、旧选择保留和两个任务依次完成。

开发者公共标识位于 Developer 两环境 JSON，缺失时按未配置处理；普通设置中的历史字段不能覆盖它。CurseForge 在线入口按所有者决定关闭，未来接入先沟通。详见 DEVELOPER_SERVICES.md。

中文玩家提示与诊断代码分别由 PlayerText / FailureText、DiagnosticLog 负责。添加错误类别应同时保留安全诊断代码；不得通过记录完整异常消息或请求正文排错。构建记录保留在 BUILD_RESULTS，实际功能按人工指南执行。
