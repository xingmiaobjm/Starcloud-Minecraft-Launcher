# 启动器自动更新 — 服务端托管指南

> 本文档面向负责服务端/网页端的同学。启动器客户端已实现自更新（`SelfUpdateService`），服务端只需要**静态托管 3 个文件**，无需任何动态接口。

## 1. 你需要托管什么

3 个静态文件，放在**同一个目录**下即可：

| 文件 | 说明 |
|---|---|
| `latest.json` | 版本清单（构建脚本自动生成，发布前手填更新说明） |
| `Starcloud Minecraft Launcher_<版本>_x64-setup-<时间戳>.exe` | NSIS 安装包 |
| `Starcloud Minecraft Launcher_<版本>_x64-portable-<时间戳>.zip` | 便携版压缩包 |

客户端逻辑：启动几秒后 GET `latest.json` → 比对版本号 → 弹窗确认 → 下载对应产物 → SHA256 校验 → 退出后静默升级并自动重启。

## 2. 发布流程（每次发新版本）

1. 修改 `dotnet/src/Starcloud.App/Starcloud.App.csproj` 中的 `<Version>`（版本号唯一来源）。
2. 运行打包：
   ```
   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
   ```
   脚本会在 `releases/` 生成两个产物 **和 `latest.json`**（内含版本号、两个产物的文件名、SHA256、字节数）。
3. **编辑 `releases/latest.json`，手填 `notes`（中文更新说明，`\n` 换行）与 `notes_en`（英文，可为 null）**。
4. 把 `latest.json` + 两个产物共 3 个文件上传到服务器同一目录。
5. 用浏览器/curl 访问 `https://<你的域名>/<路径>/latest.json` 确认可访问、内容正确。

> ⚠️ `latest.json` 里 `setup.file` / `portable.file` 只是**文件名**（不含域名）。客户端按「清单 URL 所在目录 + 文件名」拼接下载地址，所以 3 个文件必须在同一目录，上传后不要改名。

## 3. 清单格式（客户端解析契约）

```json
{
  "version": "0.2.2",
  "notes": "1. 新增自动更新\n2. 修复若干问题",
  "notes_en": "1. Self-update\n2. Bug fixes",
  "published_at": "2026-09-13T08:00:00Z",
  "setup": {
    "file": "Starcloud Minecraft Launcher_0.2.2_x64-setup-20260913-080000.exe",
    "sha256": "<64 位小写十六进制>",
    "size": 152000000
  },
  "portable": {
    "file": "Starcloud Minecraft Launcher_0.2.2_x64-portable-20260913-080000.zip",
    "sha256": "<64 位小写十六进制>",
    "size": 148000000
  }
}
```

字段约定：

| 字段 | 必填 | 说明 |
|---|---|---|
| `version` | ✅ | 语义化版本（`0.2.2` 或 `0.2.2.0`），**必须高于线上所有旧版**才会触发更新；带 `+commit` 后缀会被忽略后缀参与比较 |
| `notes` | ✅ | 中文更新说明，支持 `\n` 换行，可为空串 |
| `notes_en` | 可选 | 英文说明；客户端 UI 语言为 en-US 时优先显示 |
| `published_at` | 可选 | ISO 8601 UTC 时间，仅展示用 |
| `setup` / `portable` | ✅ | 两个产物信息；只更新其中一种形态时另一种可省略（客户端按安装形态取对应字段） |
| `file` | ✅ | 纯文件名（同目录相对路径） |
| `sha256` | ✅ | 产物 SHA256，客户端逐字节校验，不匹配会删除重下并提示失败 |
| `size` | 建议 | 字节数，客户端先校验大小再校验哈希 |

## 4. 服务器要求（重要）

1. **HTTPS 必须**：客户端只接受 `https://`（或本地测试的 `http://`）。
2. **`latest.json` 禁用强缓存**：CDN/Nginx 对该文件必须**短缓存或 no-cache**，否则新版本发布后用户长时间拿不到清单。建议响应头：
   ```
   Cache-Control: no-cache
   ```
   （或 `max-age=60` 之类的短缓存。）两个安装包产物则可以放心长缓存（文件名含时间戳，永不覆盖）。
3. **直连可访问**：客户端直连该 URL（不走 MC 下载镜像链路）。国内访问建议用国内 CDN/对象存储；不要做跳转登录、UA 拦截或防火墙墙掉非常见浏览器 UA（客户端 UA 为 `Starcloud-Launcher`）。
4. **不要覆盖历史文件**：产物文件名自带时间戳，天然不冲突；`latest.json` 是唯一会被覆盖的文件。
5. **权限**：这 3 个文件是公开可读的（启动器用户都要下载），无需鉴权。

### Nginx 参考配置

```nginx
location /launcher/ {
    alias /srv/starcloud-launcher/;
    # 清单禁缓存，产物长缓存
    location = /launcher/latest.json {
        add_header Cache-Control "no-cache";
    }
    location ~* \.(exe|zip)$ {
        add_header Cache-Control "public, max-age=31536000, immutable";
    }
}
```

### 对象存储（OSS/COS/R2 等）参考

- 上传到同一前缀目录，如 `launcher/`。
- 给 `latest.json` 单独设置 `Cache-Control: no-cache` 元数据；两个产物设长缓存。
- 绑定自定义域名 + HTTPS 后把地址交给客户端。

## 5. 客户端如何知道你的地址

- **内置默认地址**（已定）：`dotnet/src/Starcloud.Core/Services/SelfUpdateService.cs` 中的常量
  ```csharp
  public const string DefaultManifestUrl = "https://starcloudfur.cn/downloads/latest.json";
  ```
  即服务器需要把 3 个文件放在 `https://starcloudfur.cn/downloads/` 这个路径下（`latest.json` 与两个产物同目录）。
- **设置覆盖**（测试用）：设置文件 `%APPDATA%\starcloud-launcher\launcher.settings.json` 加：
  ```json
  "update_manifest_url": "http://127.0.0.1:8000/latest.json"
  ```

## 6. 联调与验收清单

本地模拟服务器（以 Python 为例）：

```
cd <存放 3 个文件的目录>
python -m http.server 8000
```

设置 `update_manifest_url` 指向 `http://127.0.0.1:8000/latest.json`，然后验收：

- [ ] `curl -i https://<线上地址>/latest.json` 返回 200、`Cache-Control` 符合要求
- [ ] 把 `version` 改高于本机版本 → 启动器启动几秒后弹「发现新版本」，说明、下载进度、重启安装全流程走通
- [ ] 故意改坏 `sha256` 一位 → 客户端提示「更新失败」且 `.part` 被清理，恢复正确值后可重试
- [ ] `version` 等于/低于本机版本 → 设置页点「检查更新」显示「已是最新版本」
- [ ] 断网/404 → 启动时静默无感，设置页手动检查报「检查失败」
- [ ] 游戏运行中启动启动器 → 不弹更新提示
- [ ] NSIS 安装版与便携版各走一遍完整更新，更新后标题栏版本号正确、`%APPDATA%\starcloud-launcher\update` 目录被清理

## 7. 安全须知

- 客户端通过 SHA256 校验产物完整性，但**传输通道本身依赖 HTTPS**，务必开启并保证证书有效。
- 服务器上传权限收敛到发布人员；`latest.json` 被篡改 = 可向所有用户推送任意 exe，托管账号安全等级要高。
- 如需后续加固，可协商清单签名（当前版本未实现，暂不阻塞上线）。
