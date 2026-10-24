# 配置迁移与恢复

## 位置和数据保护

旧版在 EXE 旁 .minecraft/LauncherSetting.json 保存配置；较新旧版单账号保存在 %LOCALAPPDATA%/FMLauncher/account.dat，早期 JSON 可能含明文 Token。

新版在同一用户的 LocalAppData/FMLauncher 保存：

- settings.v2.json：格式 2；不含 Token / API Key；.bak 为上次配置。
- accounts-v2：由 FMCore 管理的多账号资料和 DPAPI 凭据。
- secrets：壁纸请求参数、头及 Key，均为当前用户 DPAPI。以前保存的 CurseForge Key 文件保留，但当前版本不再读取。
- migration-backups/legacy-<时间>-<随机后缀>.dpapi：旧配置完整加密备份，额外熵为 null。
- wallpapers、themes、logs：有限壁纸缓存、命名主题、SDK 已脱敏日志。

原配置、account.dat、游戏文件和存档不删除。新增备份不明文复制旧令牌；旧文件中既有历史明文内容仍由用户自行保管。

## 字段对应

| 旧字段 | 新字段或处理 |
| --- | --- |
| GameVersion | VersionId |
| JavaPath | JavaPath |
| Memory（GiB） | MaxMemoryMb，乘 1024；MinMemoryMb 默认 0 |
| LoginMode=1、Playername | 首次初始化通过 SDK 创建离线账号，成功后清除 LegacyOfflineName |
| 正版资料、AccessToken / RefreshToken / 过期字段 | 不伪造转换为 SDK 凭据，保留旧文件和加密备份，用户重新授权 |
| Appearance.DarkMode | Light / Dark 模式 |
| 旧 Preset / UseCustomColors / 自定义色 | 映射预设强调色或自定义强调色 / 底色 |
| BackgroundPath / Opacity / Blur | 本地壁纸路径 / WallpaperOpacity / Blur |
| AnimationsEnabled | Appearance.Animations |

## 自动与显式导入

没有新版配置时先检查 EXE 旁旧配置。输出 TFM 变化时，只在 net9.0-windows / net9.0 / net10.0 / net10.0-windows 兄弟输出中存在唯一旧配置时自动采用。不扫描整盘，不在多个旧目录间擅自选择。

发布到新目录或多个候选时，在全局设置点击“从旧游戏目录导入配置”，选择含 LauncherSetting.json 的原 .minecraft。先验证大小、加密备份，再映射非机密字段并原子写入；重启完成旧离线名迁移和壁纸草稿刷新。已有新版配置不会每次启动反复自动导入旧文件。非法离线名保留迁移字段并报错，可手动添加合法离线账号。

游戏工作目录始终为 `<GameRoot>/versions/<版本 ID>`，包含存档、游戏配置和 mods。共享库及 assets 保持共享。没有切换到 instances，没有创建或收编 SDK 独立实例，也不移动旧文件。根目录修改只改变后续操作目标，不搬迁目录。

## 恢复

1. 关闭程序及相关游戏，备份整个用户 FMLauncher 目录和实际游戏根目录。
2. 仅恢复新版设置时，将 settings.v2.json.bak 复制回 settings.v2.json，核对 GameRoot。
3. 回退旧程序使用原配置、原 account.dat 和原游戏目录；新版凭据不保证可供旧核心读取。
4. 解密 .dpapi 必须使用创建它的同一 Windows 用户。使用 ProtectedData.Unprotect(bytes, null, CurrentUser) 将字节写入独立恢复文件，不把内容输出到终端或版本控制。
5. 源码回滚使用交付目录 baseline 或 applied-backup；代码部署不覆盖 .git、bin、obj、artifacts 或游戏数据。

换 Windows 用户 / 系统后，凭据通常需要重新输入或授权。主题导入导出不包含壁纸地址、请求头、参数或 Key。

## 开发者标识与旧用户字段

MicrosoftClientId 旧字段原样保留，不作为认证配置；DeveloperServices 按构建读取公共应用 ID，见 DEVELOPER_SERVICES.md。不更改账号存储格式，不导入私人服务 Key。根目录保存后共享本地列表立即重新查询，保留新根目录已有选择策略；游戏数据不搬迁。
