# 项目架构

目标平台为 Windows 10/11 x64、WPF、.NET 10。业务依赖仅 FocusMinecraftCore 1.0.1（程序集 / 命名空间 FMCore）和 WPF-UI 4.3.0。没有 StarLight / Panuon 运行依赖，没有 .NET 11 预览依赖。

## 最终目录约束

按所有者最终确认，所有游戏的工作目录固定为共享根下的 `versions/<版本 ID>`。存档、游戏配置和 mods 保持在该目录。共享 libraries / assets、SDK 缓存、临时 native 和启动锁仍由核心按其正式规范管理，不属于游戏工作目录。

不调用 InstanceManager、ModrinthInstaller、CurseForgeInstaller 或 ModpackInstaller：正式包的这些写入 API 固定使用 `instances/<id>`，无法通过公开参数改为版本目录。不会修改核心、绕过内部实现、建立目录链接或复制两个目录来规避约束。保留本地版本管理、资源搜索 / mods 目录打开和整合包只读清单解析。

## 模块与依赖

```text
App → Infrastructure/LauncherServices
    → Shell/MainWindow → Features/*/Views → ViewModels
    → CoreIntegration/Adapters → FMCore 正式公开 API
    → Appearance（主题、壁纸、缓存）
    → Infrastructure（配置、原子存储、凭据、受限网络）
```

Views 通过绑定展示状态和命令，代码隐藏只处理导航、焦点或密码输入等界面行为。ViewModel 负责编排，不交换令牌、不生成底层 Minecraft 参数、不实现核心已有的下载 / 安装 / 兼容性规则。适配层返回启动器自己的 DTO。Common 只包含可复用的通知和异步命令基础代码。服务由组合根显式创建和关闭，不依靠隐藏全局单例。

## 启动与进程生命周期

1. 主页提交版本、账号 ID 和设置快照。
2. GameSessionService 启动同一 EXE 的无窗口 --game-host 模式，使用随机名称、仅当前 Windows 用户可连接的命名管道。
3. 宿主通过 AccountManager 取得指定账号身份，必要时刷新；认证失败不降级离线。管道没有 Token 或密码。
4. LaunchAdapter 明确设置 GameCore.GameDirectory=versions/<版本>，AutoInstall=false。普通启动只使用已安装文件；修复由用户显式请求；Java 不自动下载。
5. GameHost 持有 MinecraftLauncher，转发 SDK 已脱敏日志、阶段和退出结果。进程创建成功不代表进入主菜单。
6. 关闭时保留游戏：发送 detach，宿主继续等待游戏退出后导出安全日志、释放 SDK。停止游戏：显式 StopAsync 后关闭。SDK 没有公开 Detach API，因此不能提前 Dispose 后假设游戏仍运行。
7. 未收到 detach 就意外失联时取消准备 / 停止游戏。新窗口不接管已脱离的旧宿主；用户可从游戏窗口退出，SDK 锁在原宿主中维持到清理完成。

LegacySessionGuard 只读检查旧会话标记；不会改写游戏 profile。新版 SDK 本身处理继承、普通 artifact 与 native 并存、参数、兼容性与进程锁。

## 异步与收尾

大型文件任务经 OperationService 串行编排，SDK 内部仍可并发下载。DownloadProgressRelay 每 200 ms 合并到 UI，按文件及重试计下载字节，显示均速、当前阶段计数；未知总量使用不确定进度，不伪造 ETA。

宿主输出和 UI 队列有界，主页最多保留 400 行日志。应用关闭先取消并等待命令、初始化和壁纸请求，再释放共享认证管理器 / 网络客户端。命令操作以 await 的返回结果为完成依据，不能靠最后一次进度回调判定成功。

## 主题和壁纸

ThemeService 维护语义资源，页面使用 DynamicResource；四套预设共享业务和组件。支持系统 / 浅 / 深色、自定义强调色和底色、卡片及壁纸透明度、圆角、材质、模糊、布局与动画。导入为有限 JSON 数据，拒绝未知字段，不加载第三方 XAML 或脚本。

WallpaperManager 负责串行刷新、验证、缓存和失败回退。Provider 取得字节；本地文件夹按路径排序轮播，最多 500 个顶层候选，重启从首张开始。API 的请求参数、头和 Key 用当前用户 DPAPI 保存。只连接实际解析为公网 IP 的 HTTPS 默认端口，禁止自动重定向和代理，JSON 返回的图片请求不携带 API 凭据。响应、像素、解码宽度及缓存数均受限。

本次没有实际运行启动器、账号授权、网络壁纸或游戏；视觉与运行效果全部待人工验收。

## 2026-10-24：界面导航层

Navigation/NavigationService 负责分区、当前页及返回栈，NavigationActions 通过路由命令连接页面与 Shell。MainWindow 惰性缓存 UserControl，所有页面继续绑定 LauncherServices 的既有 ViewModel。动画仅作用于显示容器，下载及启动生命周期仍由原服务持有。此前纯外观阶段的范围见 UI_REFACTOR_20261024.md；后续状态同步与配置整改见 EXPERIENCE_FIX_20261024.md。

## 安装完成与共享版本状态

LauncherServices → InstanceCatalog → VersionAdapter → SDK 本地版本枚举。Home / Versions 共享一份 ReadOnlyObservableCollection 与当前版本；Downloads 订阅一次 CatalogChanged 并在 Dispose 退订，更新官方版本的已安装标记。各页不再单独长期缓存本地实例。

InstallationAdapter 成功返回 SDK 版本 ID 后，Downloads 调用 InstallationSucceededAsync，携带原提交根目录及操作 ID。刷新使用应用生命周期令牌，独立于已完成的安装取消令牌。全局异步门覆盖查询与 UI 发布；目录与切换代次防止过期结果落到另一根目录。没有 UI 控件间直接通知，也没有自行解析 / 写入核心数据。根目录改变由设置保存回调统一 ChangeRootAsync。

开发者公开标识由 DeveloperServices 读取嵌入配置，普通 SettingsStore 不提供该服务配置入口。CurseForge 在线调用关闭，Modrinth 搜索保留。账号 Current 与候选 Selected 分离；主页只读取实际 Current。
