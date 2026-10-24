# 模块索引

路径相对项目根目录。

| 模块 | 代码位置 / 类型 | 公开入口与职责 |
| --- | --- | --- |
| 应用 | App.xaml.cs、Infrastructure/LauncherServices.cs | 显式组合服务、初始化本地数据、定时壁纸、异步关闭 |
| 导航 | Shell/MainWindow | 分区导航、账户侧面板、FluentWindow / TitleBar、窗口生命周期 |
| 主页 | Features/Home/ViewModels/HomeViewModel | ReloadAsync、Launch / Stop / Verify / Repair、有限游戏日志 |
| 进程 | Features/Launch/Services/GameSessionService、GameHost | LaunchAsync / StopAsync / CloseAsync；后台持有 SDK |
| 账号 | Features/Accounts/ViewModels/AccountsViewModel | 多账号、离线、正版设备码、切换、默认、刷新、删除、资料 |
| 本地版本 | Features/Versions/ViewModels/VersionsViewModel | 枚举、选择版本、打开版本目录及 mods；不创建 SDK 实例 |
| 下载 | Features/Downloads | 官方版本与加载器；OperationService.RunAsync / Cancel、进度合并 |
| Java | Features/Java | Detect / Browse / Recommend / Query / Install / Use |
| Mod 资源 | Features/Mods | Modrinth 分页筛选搜索；CurseForge 暂停，打开所选版本的 mods |
| 整合包信息 | Features/Modpacks | 官方 SDK 只读解析 mrpack / CurseForge 清单 |
| 设置 | Features/Settings | 普通 / 高级设置草稿、显式保存、旧配置导入 |
| 外观 | Appearance/Themes、ViewModels、Views | ThemeService.Apply / Preset / Import / Export、主题预览与库 |
| 壁纸 | Appearance/Wallpapers | RefreshAsync / SelectHistoryAsync / ClearAsync、Provider 和缓存 |
| SDK 边界 | CoreIntegration/Adapters、Models、Mapping | Account / Version / Java / Installation / Launch / Resource / Modpack 适配 |
| 配置 | Infrastructure/Configuration | SettingsStore.Load / Save / Snapshot / UpdatePreferences / ImportFromGameRoot |
| 存储 | Infrastructure/Persistence | AtomicJson、SecretStore；用户配置与 DPAPI 凭据分开 |
| 网络 | Infrastructure/Networking/PublicImageClient | 壁纸公网 HTTPS 限制，不改变 SDK 网络实现 |
| UI 基础 | Common | ObservableObject、AsyncCommand、PageViewModel |

DTO 按主要类型分别存放。不要在视图引用 FMCore 类型，新增核心调用先经过适配层。

## 当前限制

目录约束是本次最终要求的一部分。FMCore 的独立实例、Mod 事务安装 / 更新、整包安装 / 更新 / 回滚 / 导出固定在 instances，因此没有接入；对应界面只保留实际可用的版本管理、查询和包信息读取。以后核心提供自定义游戏目录的正式公开 API 后才能恢复这些写入功能。

Quilt、OptiFine 混合加载器安装、跨 Mod 原子批量更新及单 Mod 历史回滚也不提供虚构入口。Microsoft 使用开发者配置的合法公共客户端 ID；CurseForge 在线服务未提供。

## 2026-10-24：外观模块调整

- Navigation：分区导航、返回栈与跨页面路由请求，不执行业务操作。
- Shell/MainWindow：固定窗口、顶部和侧栏、缓存页面、工作区适配及动画取消。
- Features/Downloads/Views/TasksPage：独立展示已有 OperationService，无新增队列。
- Appearance/Resources/Controls 与 ThemeService：深浅色语义资源、卡片和导航焦点。
- Common/Converters/EqualsConverter：只读导航选中状态转换。

新增业务能力的实际缺口详见 FEATURE_GAPS.md。

## 玩家体验整改模块

- Features/Instances/Services/InstanceCatalog：统一本地集合、当前版本、目录切换及真正安装成功通知。
- Features/Home/Views/DiagnosticsPage：复用既有维护命令与日志；主页不再承担诊断界面。
- Features/Accounts/Views/AccountsPage：窗口内侧面板，绑定原账号服务。
- Infrastructure/Configuration/DeveloperServices、Developer/*.json：按构建环境提供公共应用 ID。
- Infrastructure/Logging/PlayerText、DiagnosticLog：中文状态与不含敏感正文的诊断代码记录。
