# FocusMinecraft!Launcher（忘却的旋律）

Windows 10/11 x64 的 Minecraft Java 启动器，.NET 10 WPF、FocusMinecraftCore 1.0.1、WPF-UI 4.3.0。本地迁移开发版 2.0.0，实际功能由项目所有者验收。

- 正式 API 接入账号、Java、版本、原版 / 加载器安装、校验修复、启动、进程及日志。
- Fluent 导航、四主题预设、自定义主题库、每日壁纸与可配置图片 / JSON API。
- 游戏工作目录统一为 `versions/<版本>`，保留原有配置和存档位置；关闭 UI 后可由无窗口宿主保留游戏。
- 版本目录管理、Modrinth 资源搜索、mods 文件夹打开、整合包只读清单信息。

按所有者最终目录要求，移除了固定写入 instances 的 SDK 独立实例、Mod 安装更新及整包写入入口。Quilt 与 OptiFine 混装也没有虚构接口。旧数据不移动，不修改核心源码。

## 构建

```powershell
dotnet restore FMLauncher.csproj
dotnet build FMLauncher.csproj -c Debug --no-restore
dotnet build FMLauncher.csproj -c Release --no-restore
dotnet publish FMLauncher.csproj -c Release --no-restore -p:PublishProfile=FrameworkDependent
```

框架依赖输出为 artifacts/publish/framework-dependent，需要 .NET 10 Windows Desktop Runtime x64。可选自包含配置为 SelfContainedWinX64。分发应保留完整目录，不能只复制 EXE。

新目录首次运行时，在全局设置选择“从旧游戏目录导入配置”可导入原 .minecraft/LauncherSetting.json；正版通过开发者配置的公共应用 ID 重新授权，普通用户不填写 Client ID；CurseForge 在线服务当前暂未开放。

## 文档

- [迁移清单与能力边界](Docs/MIGRATION_CHECKLIST.md)
- [架构](Docs/ARCHITECTURE.md)
- [功能定位](Docs/MODULES.md)
- [维护与发布](Docs/MAINTENANCE.md)
- [配置迁移与恢复](Docs/CONFIG_MIGRATION.md)
- [人工验收](Docs/MANUAL_TESTING.md)
- [构建记录](Docs/BUILD_RESULTS.md)

本次不执行自动化或运行时功能测试。编译成功不代表实际功能通过。

开发者 Voltucit · MIT License

## 2026-10-24 外观更新

已按所有者确认完成外观、分区导航与现有功能布局重排；固定窗口、页面缓存及滑入、独立任务展示、分组设置已接入原有服务。本轮范围见 [外观交付说明](Docs/UI_REFACTOR_20261024.md)，暂缓业务见 [功能缺口清单](Docs/FEATURE_GAPS.md)。不得将本轮 UI 交付视为整合包安装、实例身份或多任务后端已完成。实际界面与功能仍由所有者验证。

## 2026-10-24 玩家体验整改

安装真正成功后通过共享 InstanceCatalog 自动重新查询并通知主页、版本选择和已安装状态；保留当前选择和下载分类。主页精简为账号、版本、启动，账户管理使用窗口内部侧面板，维护命令和日志移至“更多”。普通设置不再展示开发者服务凭据。

本轮范围见 [整改说明](Docs/EXPERIENCE_FIX_20261024.md)，配置方式见 [开发者服务](Docs/DEVELOPER_SERVICES.md)。实际运行与功能均待所有者人工验收。
