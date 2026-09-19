# Starcloud 启动器插件 SDK 开发指南（plugin-sdk）

> 本插件规范仅提供游戏启动参数扩展、UI 扩展能力。插件开发者独立承担插件全部法律责任。
> 启动器本体不提供、不支持、不接管任何账号身份验证功能。第三方插件行为与启动器本体开发主体无关。

对应规范：[plugin-spec.md](plugin-spec.md)（manifest / 权限 / 安全规则以规范为准）。

## 1. 最小可用插件（JS）

目录结构：

```
com.example.hello/
├─ manifest.json
└─ main.js
```

manifest.json：

```json
{
  "id": "com.example.hello",
  "name": "Hello 插件",
  "version": "1.0.0",
  "apiVersion": "1.0",
  "author": "你的名字",
  "description": "演示：追加 JVM 参数 + 通知 + 面板",
  "permissions": [
    "launcher:game_arguments",
    "launcher:notification",
    "launcher:ui_panel"
  ],
  "compatibility": { "minLauncherVersion": "0.2.4" }
}
```

main.js：

```javascript
// 插件加载：注册一个面板（assets/panel.html）
function onLoad(ctx) {
    ctx.logger.info("hello 插件已加载，启动器 " + ctx.launcher.version);
    ctx.ui.registerPanel("main", "Hello 面板", "assets/panel.html");
}

// 卸载清理（可选）
function onUnload(ctx) {
    ctx.logger.info("hello 插件已卸载");
}

// 游戏启动前：仅可追加参数
function beforeGameLaunch(ctx) {
    ctx.args.append("-Xmx4G");           // 追加游戏参数
    ctx.args.appendJvm("-XX:+UseG1GC");  // 追加 JVM 参数
    ctx.args.removeKey("-XX:+UnlockExperimentalVMOptions"); // 仅对插件增量生效
    ctx.ui.notify("Hello", "参数注入完成"); // launcher:notification
}
```

打包：把目录压成 zip，改后缀 `.mclplug`，在启动器「插件」页安装。

## 2. Python 插件（main.py）

```
com.example.pysample/
├─ manifest.json     # 同上，可选 permissions
└─ main.py
```

```python
def onLoad(ctx):
    ctx.logger.info("python 插件已加载")

def beforeGameLaunch(ctx):
    if ctx.args is not None:        # 未声明 game_arguments 权限时为 None
        ctx.args.append("-Xmx4G")
```

需要本机安装 Python 3.8+。启动器以进程外子进程方式运行（协议见
`Assets/pluginsdk/starcloud_host.py`）；技术限制：Python 插件暂不开放 `ctx.files`。

## 3. ctx 能力速查

| 成员 | 权限 | 方法 |
| --- | --- | --- |
| `ctx.launcher` | 无 | 只读属性 `{version, apiVersion, officialBuild}` |
| `ctx.args` | `launcher:game_arguments` | `append(s)` / `appendJvm(s)` / `removeKey(key)` |
| `ctx.logger` | 无 | `info` / `warn` / `error` |
| `ctx.files` | `launcher:file_access` | `read(name)` / `write(name, text)` / `exists(name)` / `list()` |
| `ctx.ui` | `launcher:ui_panel` / `launcher:notification` | `registerPanel(id, title, entry)` / `notify(title, message)` |

**红线**：`ctx.args.append` 命中身份参数（`--username` / `--uuid` / `--accessToken` / `--userType` /
`--clientId` / `--authlib-injector` 等）会被本体直接拦截丢弃并记录日志；该类权限
（`launcher:account_verify` / `launcher:auth_override` / `launcher:uuid_override`）不存在申请通道。

## 4. 官方签名（可选）

官方渠道发布的插件可在 manifest 中携带 `signature`：对 `manifest.json` 文件字节做
SHA256 → ECDSA P-256 签名 → base64。启动器内置公钥验证通过后展示「官方」徽标，
否则展示【第三方未验证】并在启用时强制免责确认。

## 5. 示例与测试

- 示例插件：`examples/plugins/hello-panel/`（本仓库内，可直接打包安装）。
- 调试：插件日志写入 `%APPDATA%/starcloud-launcher/plugins/logs/<id>.log`；
  启动器统一日志分类 `plugin` / `launch`（「启动器日志」页可见参数拦截记录）。
