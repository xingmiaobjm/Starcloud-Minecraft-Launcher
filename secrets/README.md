# secrets/ —— 本地构建密钥（整个目录不入库）

把 CurseForge 官方 API Key 写成一行纯文本放进本目录的 `curseforge.key`，
官方分发版本的构建就会把它作为**内置密钥**编进星云核心：

```
secrets/curseforge.key  →  AssemblyMetadata("CurseForgeApiKey", …)
```

本目录除本说明外都被 `.gitignore` 忽略（`secrets/*` + `!secrets/README.md`），
密钥不会进 GitHub。也可以不落文件，改用构建参数或环境变量：

```powershell
dotnet build dotnet\Starcloud.slnx -p:CurseForgeApiKey=<key>
$env:STARCLOUD_CF_KEY = "<key>"     # 环境变量方式
```

三者优先级：**MSBuild 属性 > 环境变量 > 本目录文件**。
都没有时不会内置密钥，运行期按「用户自备密钥 → MCIM 镜像」走（不会报错、不会降级功能）。

为什么需要它、以及 CurseForge CDN 从 2026-07-16 起强制要求 key 的背景，
见 `dotnet/src/Starcloud.Core/Services/CurseForgeKey.cs` 顶部注释。
