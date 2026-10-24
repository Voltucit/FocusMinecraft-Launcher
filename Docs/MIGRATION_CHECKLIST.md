# 迁移清单和范围

## 输入和正式版前提

以当前本地 E:/rider/project/忘却的旋律_EP/FMLauncher 为基线，HEAD 7593d36，大量 Services / Views / Models / Diagnostics 文件未跟踪。67 份非构建文件已建立独立快照与 SHA-256 清单。没有使用 GitHub 旧源码覆盖本地，没有读取真实凭据或运行旧诊断。

基线 csproj 已改成 net10.0、FocusMinecraftCore 1.0.1、WPF-UI 4.3.0，但源码仍调用 StarLight / Panuon，还引用 .NET 11 RC 与 Newtonsoft 预览包。最终采用 net10.0-windows7.0 和上述两个稳定业务依赖。

| 前提 | 核验 |
| --- | --- |
| 稳定正式版 | GitHub v1.0.1，draft=false、prerelease=false，2026-10-22 发布；正式 NuGet 已从官方源恢复 |
| 身份与兼容 | PackageId=FocusMinecraftCore；程序集 / 命名空间 FMCore；目标 net10.0 |
| P0 | 下载、版本、Java、原版安装、账号与启动均有正式入口 |
| P1 | 实例、完整性、日志、Fabric / Forge / NeoForge / 独立 OptiFine 已提供 |
| P2 | 两平台 Mod、整合包、高级参数和显式 Runtime 安装已提供 |
| API 核对 | 正式包 XML、发布文档、独立示例及本次实际编译 |

来源：[正式发布](https://github.com/Voltucit/FocusMinecraftCore/releases/tag/v1.0.1)、[NuGet](https://www.nuget.org/packages/FocusMinecraftCore/1.0.1)、[正式版功能与边界](https://github.com/Voltucit/FocusMinecraftCore/blob/v1.0.0/README.md)。核心的历史测试不代表本次启动器通过运行验收。

## 最终目录决策

所有者明确确认：所有游戏均使用 versions/<版本>，去掉冲突的 SDK 独立实例入口。资源和整合包写入 API 也固定在 instances，因此这些入口一并移除，只保留只读查询 / 解析。不会为完成原始清单而违反后续目录要求或自写一套重复事务。

## 文件与功能映射

| 旧责任 | 最终实现 |
| --- | --- |
| HomePage 认证 / Java / 启动 / UI | HomeViewModel、Account / Java / LaunchAdapter、GameSessionService / GameHost |
| AccountService / Store / Gateway | AccountAdapter → 正式 AccountManager，多账号与 DPAPI；旧正版重授权 |
| SettingsService / 旧模型 | SettingsStore、格式 2、原子写入、加密迁移备份、SecretStore |
| DownloadCatalog / GameInstallationService / InstallFiles | VersionAdapter / InstallationAdapter，下载及安装规则完全交核心 |
| LauncherInstaller.java / class | 从运行项目移除，原文件留在迁移前快照，不重复分发安装器 |
| profile 修补 / GameLaunchLease | SDK 继承、native 和锁；LegacySessionGuard 只读旧会话；不再改 JSON |
| 下载任务 / 进度 | OperationService / DownloadProgressRelay，全局状态、取消、阶段计数及均速 |
| 大型窗口和 ResourcesPage | Shell + 按功能 Views / ViewModels |
| 原游戏 / 启动器设置 | 游戏设置、全局、Java、外观独立页面 |
| 主题和 Panuon 字典 | Appearance，WPF UI、语义资源、四预设、主题库、JSON 导入导出 |
| 旧预览 / 检查 Diagnostics | 保存在源码快照，不进入新运行项目，本次不执行 |
| 版本 / 实例界面 | Features/Versions，仅管理版本目录，不调用 InstanceManager |
| Mod / 整合包 | 两平台只读搜索、打开 versions/版本/mods；只读包清单；冲突写入入口移除 |

## 已知限制

- 正式 API 不支持自定义独立实例 / Mod / 整包目录，因此没有接入其写入功能。
- Quilt、OptiFine 混装没有对应正式实现；已有 profile 不删除，但能否启动仍待用户验证。
- 原版保持官方 ID；加载器的可选目录名称使用 SDK VersionId 参数，工作目录相同映射到 versions。
- SDK 无公开 Detach，后台宿主继续持有对象；新窗口不接管旧宿主。
- 壁纸仅公网 HTTPS 默认端口、不经代理、不自动跟随重定向；WebP 依赖系统 WIC。
- 不声称实际性能提升。账号、游戏、加载器、UI、网络壁纸均未运行验收。
- 不修改核心源码、不提交推送、不发布正式 Release、不下载或运行 Minecraft。
