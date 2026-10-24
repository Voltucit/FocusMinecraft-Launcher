# 当前交付构建记录：玩家体验与安装状态同步

日期：2026-10-24。工具链 Windows x64、.NET SDK 10.0.402；依赖保持 FocusMinecraftCore 1.0.1、WPF-UI 4.3.0。

| 实际执行 | 结果 |
| --- | --- |
| 工作副本 NuGet 依赖恢复 | 成功；受限网络首次出现 NU1900，获准网络上下文重新恢复成功，未关闭审计 |
| 工作副本最终 Debug / Release | 成功，0 警告、0 错误 |
| 原项目依赖恢复 | 成功 |
| 原项目 Debug / Release | 成功，0 警告、0 错误 |
| 原项目 Release 框架依赖发布 | 成功 |
| XAML / 开发者 JSON 与源码哈希核对 | 静态检查通过，回写前没有冲突 |

原项目执行：

```powershell
dotnet restore FMLauncher.csproj --force
dotnet build FMLauncher.csproj -c Debug --no-restore
dotnet build FMLauncher.csproj -c Release --no-restore
dotnet publish FMLauncher.csproj -c Release --no-restore -p:PublishProfile=FrameworkDependent
```

发布目录：artifacts/publish/framework-dependent，完整分发，需要 .NET 10 Windows Desktop Runtime x64。源码包与发布包位于 artifacts。没有构建自包含版本，也没有运行 EXE。

改前 106 个源码文件、旧发布目录和旧压缩包均备份在 `C:\Users\28046\Documents\ChatGPT\coder\fmlauncher-experience-20261024`。本轮共更新 42 个已有文件、增加 11 个文件（随后仅补充本构建记录），不删除已有源码；同步采用逐文件 SHA-256 校验。未触及真实游戏、存档、账号、SDK 包或核心源码，未提交 Git。

构建日志位于上述任务目录 logs 下的 original-restore.log、original-debug.log、original-release.log、original-publish.log。源码改动与范围见 EXPERIENCE_FIX_20261024.md；开发者配置见 DEVELOPER_SERVICES.md；已知限制见 FEATURE_GAPS.md。

本次没有执行单元 / 集成 / UI 自动化测试、程序运行、实际下载 / 安装、Java 探测、Microsoft 授权或游戏启动。功能、视觉和登录可用性全部待所有者按 MANUAL_TESTING.md 验证。编译成功不代表实际功能通过。

---

下文为此前迁移与外观阶段的历史构建记录，保留用于追溯；当前交付以上述记录为准。

# 本次构建记录

日期：2026-10-23。工具链：Windows x64、.NET SDK 10.0.402。代码以当前本地项目为基线，最终遵守所有游戏工作目录为 versions/版本的要求。

## 已执行

```powershell
dotnet restore FMLauncher.csproj --force
dotnet build FMLauncher.csproj -c Debug --no-restore
dotnet build FMLauncher.csproj -c Release --no-restore
dotnet publish FMLauncher.csproj -c Release --no-restore -p:PublishProfile=FrameworkDependent
```

官方 NuGet 恢复成功；最终 Debug / Release 编译均成功，0 警告、0 错误；框架依赖发布构建成功。另执行了 C# / XAML 排版及静态引用和目录映射检查，未运行启动器、自动化测试、真实账号认证或 Minecraft。

已回写指定 E 盘原项目，并在该目录再次恢复依赖、编译 Debug / Release 和发布框架依赖版本。源码同步逐文件核对迁移前哈希；原有源码完整备份，旧游戏和账号数据不进入同步范围。

发布目录为 artifacts/publish/framework-dependent，应完整分发。依赖 .NET 10 Windows Desktop Runtime x64。自包含配置已提供但本次未构建或运行验收。

## 最终范围

- 正式核心与 Fluent UI 迁移，按功能组织源代码，中文说明与维护文档。
- 账号、Java、版本、游戏安装、加载器、校验修复、启动、停止、后台宿主日志。
- versions/版本工作目录，主题库与本地 / API 壁纸。
- 只读 Mod 搜索及版本 mods 目录管理、整合包只读信息。

独立实例、Mod 自动安装 / 更新、整包安装 / 更新 / 回滚 / 导出因正式 API 固定 instances 路径而按所有者最新要求移除。Quilt / OptiFine 混装未虚构实现。

所有实际功能、UI 效果、性能及数据兼容性仍需所有者按 MANUAL_TESTING.md 执行。编译和发布不等于功能验收。

## 2026-10-23：初始化进度条绑定修复

用户报告窗口初始化失败：OperationService.Percent 为只读源属性，却被默认双向绑定回写。Shell/MainWindow.xaml 和 Features/Downloads/Views/DownloadsPage.xaml 两处进度值及不确定状态均明确指定 Mode=OneWay，保持状态只由下载服务更新。

原项目 Debug / Release 编译、框架依赖发布重新完成，均 0 警告、0 错误。发布目录与源码 / 发布压缩包同步更新。没有运行 UI、游戏、账号或自动化测试；实际初始化与下载页显示仍由所有者重开程序验证。

## 2026-10-24：外观与既有功能布局重构

所有者确认本轮只处理外观、导航和既有功能布局，新增后端暂缓。工作副本依赖恢复、Debug / Release 编译已完成，最终均 0 警告、0 错误。最初受沙箱网络限制的恢复出现 NU1900，随后在获准网络上下文重新恢复成功，未关闭 NuGet 审计、未变更依赖版本。

静态核对表明 CoreIntegration、业务 ViewModel、启动宿主、认证、实例目录及配置持久化源码均未改动。XAML 与原命令连接已检查；先前只读进度 OneWay 修复保留。没有运行启动器、游戏、实际安装、账号认证或自动化测试。原项目最终依赖恢复、Debug / Release 编译及框架依赖发布亦已完成，均 0 警告、0 错误；发布目录和源码 / 发布压缩包已同步更新。


本轮原项目执行命令：

```powershell
dotnet restore FMLauncher.csproj --force
dotnet build FMLauncher.csproj -c Debug --no-restore
dotnet build FMLauncher.csproj -c Release --no-restore
dotnet publish FMLauncher.csproj -c Release --no-restore -p:PublishProfile=FrameworkDependent
```

本轮工作副本及改前源码备份位于 `C:\Users\28046\Documents\ChatGPT\coder\fmlauncher-ui-20261024`。原发布文件及压缩包另行保留备份。同步前逐文件核对哈希，没有覆盖冲突。没有修改游戏目录、存档、FMCore 包或原有核心适配与业务 ViewModel。
