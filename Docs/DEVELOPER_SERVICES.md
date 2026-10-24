# 开发者公共服务配置

## Microsoft 正版登录

Developer/services.development.json 与 services.production.json 仅包含 MicrosoftPublicClientId。Debug 读取 development，Release 读取 production；通过嵌入资源随构建发布。当前两环境使用所有者提供的同一个 FMLauncher 公共应用 ID。以后若使用不同应用，在发布构建前分别修改并审阅这两个文件。

公共 Client ID 不是秘密。不得在文件、源码或客户端添加 Client Secret。普通设置不提供输入框，也不要求玩家注册应用。DeveloperServices 只检查 ID 格式；应用所有权、许可、设备码流程及游戏资格仍需开发者在 Microsoft 门户确认并人工验证，不将编译成功视为授权可用。

配置缺失或 ID 无效时，登录入口显示“此版本暂未配置 Microsoft 登录服务，请联系启动器开发者”；离线、原版安装和其他能力不受影响。既有 settings.v2.json 中 MicrosoftClientId 仅保留兼容，认证明确忽略它，不通过旧用户配置覆盖开发者标识。

官方依据：[Microsoft 公共和机密客户端说明](https://learn.microsoft.com/en-us/entra/identity-platform/msal-client-applications)。桌面公共客户端不能可靠保存客户端秘密。

## CurseForge

所有者明确当前不提供 CurseForge 在线服务。本轮从搜索来源与普通设置移除 Key 输入 / 保存 / 删除，ResourceAdapter 仅启用 Modrinth。对旧调用传入的其他来源返回明确不可用提示，不发送 CurseForge 请求，不读取或删除已有加密 Key。

本地 CurseForge 整合包清单只读解析不需要在线 Key，仍可使用；在线搜索、安装不因页面文案而视为已提供。

以后若接入，先询问所有者并确认正式授权及安全架构。不得把私人 Key 嵌入 EXE，不使用混淆代替密钥保护，也不假设任意代理自动符合条款。官方 [REST API 文档](https://docs.curseforge.com/rest-api/) 与 [第三方 API 条款](https://support.curseforge.com/support/solutions/articles/9000207405-curseforge-3rd-party-api-terms-and-conditions) 是接入审核依据。

## 用户自定义壁纸

“设置 → 个性化 → 壁纸 → 自定义 API”仍为用户主动配置的可选功能。其请求头 / 参数 / Key 继续由原 SecretStore 加密保存，与上述开发者公共配置和已关闭的 CurseForge 服务分开。
