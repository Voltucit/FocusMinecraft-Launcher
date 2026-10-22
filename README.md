
# FocusMinecraft!Launcher

![frame](https://img.shields.io/badge/.NET-9.0-blue)
![license](https://img.shields.io/badge/协议-MIT-orange)
![version](https://img.shields.io/badge/版本-1.5-green)
![status](https://img.shields.io/badge/状态-开发中-yellow)
[![bili](https://img.shields.io/badge/BILI-我的B站主页-darkturquoise.svg)](https://space.bilibili.com/38782485?spm_id_from=333.1007.0.0)


---

FocusMinecraft!Launcher（Focus Minecraft启动器）是一款由C#编写的Minecraft启动器，目前由我一人维护，如果你有建议，或是想参与开发，可以提交Issues和pr

- 基本启动功能 √

- 内存优化 √

- 正版登录 √

- 下载原版游戏及自动安装 Forge / Fabric / NeoForge / OptiFine √

- 下载mod ×

- GUI界面优化 √


## 开发与项目结构

项目采用 .NET 9 WPF，目录按 `Views`（界面）、`Models`（数据）、`Services`（服务）、`Resources`（资源）、`Diagnostics`（检查）、`docs`（文档）分类。

- [目录与职责说明](docs/STRUCTURE.md)
- [界面功能与验证方式](docs/UI.md)
- [游戏下载、组件兼容与安装说明](docs/DOWNLOADS.md)
- 顶部导航统一为「主页 / 设置 / 资源中心 / 关于」。
- 「设置」下分「游戏设置 / 启动器设置」，保留选中高亮与动画过渡。
- 使用背景图时，一级与二级导航采用半透明底色，图片可连续透出。

```powershell
dotnet build FMLauncher.csproj -c Release
.\bin\Release\net9.0-windows\FMLauncher.exe
```

玩家配置继续使用可执行文件旁的 `.minecraft/LauncherSetting.json`。
