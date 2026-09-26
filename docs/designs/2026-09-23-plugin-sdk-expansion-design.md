# 插件 SDK 全面开放 + 离线登录恢复（设计说明）

日期：2026-09-23 ｜ 对应需求：「开放启动器几乎所有的 SDK（记得写文档，越详细越好），并恢复离线登录」
规范：[plugin-spec.md](../plugin-spec.md) v1.2 ｜ SDK 指南：[plugin-sdk.md](../plugin-sdk.md)

---

## 1. 目标与边界

**做什么**

1. 把启动器几乎全部能力开放给插件（版本 / 实例 / 游戏进程 / 社区资源 / 下载 / 网络 / 设置 /
   账户只读 / 日志 / 任务中心 / 存档与备份 / 联机 / 外观 / 更新查询 / 存储 / 系统 / 事件）。
2. JS 与 Python 两种运行时能力面**完全对齐**（此前 Python 只有参数 + 面板 + 通知）。
3. 文档写到"照着抄就能跑"：规范给规则与签名表，SDK 指南给逐能力可跑片段。
4. 恢复离线登录（含账户页入口、主页身份切换、启动链路）。

**不做什么（有意留白，规范 §5.3）**

- 账号身份校验 / 认证流程接管 / UUID 覆写：黑名单，声明即拒绝加载。
- 插件自管理（启停 / 安装 / 卸载其他插件）：插件不应决定同类的生死。
- 启动器自更新安装：会覆盖本体二进制，只能由用户在设置页手动触发。
- 账户写操作（移除账户 / 改默认账户）：身份相关操作留在本体界面。
- 插件拿任何令牌：accessToken / refreshToken / clientToken / xuid 都不出现在 ctx 返回值里。

## 2. 架构：权限装配 + 能力层 + 共用执行层

```
插件脚本 (main.js / main.py)
      │  ctx.*                     ▲ ctx.event / onLauncherEvent
      ▼                            │
PluginContext（按 manifest 权限装配；未声明 = null/undefined）
      │
      ├── PluginApiCore ── 权限门 / JSON 工具 / HTTP / 沙箱路径 / 宿主桥
      │
      ├── 直接实现（纯 Core 服务）：versions / instances(read) / game.status /
      │     download / http / settings / storage / accounts(只读) / logs / tasks
      │
      └── 转交 NovaToolRegistry（与 Nova 共用同一份写操作实现）：
            install_minecraft / install_loader / repair_instance / rename_version /
            delete_version / launch_game / install_content / enable_content /
            install_modpack / remove_from_library / backup_version / restore_backup /
            backup_save / delete_save / export_modpack / multiplayer_* / set_appearance /
            update_settings / update_instance_settings / cancel_task / clear_tasks / …
```

**为什么写操作走后者的复用而不是各写一份**

- Nova 的工具面已经把"装版本/补全/装资源/备份/联机"这些流程写透了（含并发、预检、会话锁、
  失败语义）。插件再实现一遍，等于给同一个动作维护两条代码路径——将来只会分叉成
  「AI 能装好、插件装不好」这类只在一条路径复现的 bug。
- 复用后：插件与 Nova 的行为天然一致，测试也只需覆盖一处业务逻辑，插件层只测"权限门 + 参数映射"。
- 代价：写操作的返回值是启动器生成的中文结论（`{ok,message}`）。对插件足够（要展示就展示，
  要判断成败就看 `ok`），不值得为此把工具层改成结构化协议。

**为什么读操作直接实现**

读操作多数是一行（`ManifestService.ListInstalled` / `TaskCenterService.Snapshot` / 读日志尾部），
直接实现可以返回结构化数据（数组 / 字典），插件拿到的就不是"给人看的文本"。
唯一例外是搜索类（`content.search` / `saves.list`），它们本来就被优化成"给模型/人看的清单"，
保持文本返回。

## 3. 权限模型

- 白名单从 4 项扩到 **24 项**（`PluginPermissions.Allowed`）。白名单制不变：未知权限拒绝加载。
- **两道门**：装配层（权限不足时成员是 `null`）+ 方法入口 `Require(permission)`。
  后者保证"绕过装配直接调用 C# 对象"也拦得住；对应单测见 `PluginSdkTests.绕过装配直接调用仍被权限门挡住`。
- 黑名单 3 项不变，另外新增 §5.3 的"不开放"清单（不在白名单里 = 声明即拒）。
- 设置读写额外做字段级过滤：主机私有键（模型密钥 / 模型地址 / CurseForge Key / 更新源地址）
  读不到也写不了，`get()` 的 `filtered` 字段明示被过滤的键名，避免插件"盲猜为什么读不到"。

## 4. 线程与同步模型

插件钩子是同步的（Jint 同步执行、Python 行走式协议），而宿主服务全是异步的（下载 / 安装 / 启动）。

- `PluginSync.Run(() => task)`：把异步宿主调用收敛成同步返回，工作体经 `Task.Run` 落到线程池，
  **绝不在 UI 线程上等待**。
- 需要 UI 的能力（确认框 / 提示框 / 剪贴板 / 开页 / 打开链接）走 `IPluginHostBridge`，
  App 实现里用 `DispatcherQueue` 切到 UI 线程；对话框用 `TaskCompletionSource` 把结果带回等待中的
  插件线程（UI 线程不阻塞，弹窗才能显示）。
- 弹窗等待上限 5 分钟，其他宿主能力 30 秒；超时按"未确认 / 失败"返回，避免插件线程永不返回。
- 事件投递在后台线程（`Task.Run`），宿主用一把闸门保证同一插件的钩子不并发执行
  （Jint 引擎不可并发使用）。

## 5. 事件机制

- 订阅：`ctx.events.on(name)`；投递：宿主 `PluginHost.RaiseEvent(name, payload)` →
  调用订阅者的 `onLauncherEvent(ctx)`，载荷放在 `ctx.event = {name, data}`。
- 事件源不走"各处埋点"，而是**统一看游戏会话锁**（`GameSessionGuard.Current`）：
  主页按钮、版本页、Nova 工具、插件自己发起的启动都会占同一把锁，所以只看锁的"有↔无"
  就能覆盖全部入口。实现在 `MainWindow.WatchGameEvents`（复用已有 300ms 调度中心定时器）。
- `launcher.ready` 在所有已启用插件加载完成后广播（插件才知道"别人也在"）。
- 不做事件队列与重放：需要历史就主动查（`ctx.game.status` / `ctx.logs.*` / `ctx.saves.*`）。

## 6. Python 能力回调（host_call）

原先 Python 只能"声明式上报"（append / panels / notifications），能力面落后 JS 一大截。
本轮加了双向通道：

```
插件 → 宿主  {"op":"host_call","id":7,"api":"instances.list","args":[]}
宿主 → 插件  {"op":"host_reply","id":7,"ok":true,"result":[…]}
```

- 宿主在等钩子响应的读循环里处理 `host_call`（`PyPluginRuntime.Request`），所以回调可以在钩子内
  任意调用，且不会与"钩子响应"串线。
- 能力名 → ctx 成员方法的映射**由反射从 `PluginContext` 生成**（`PluginHost.BuildCapabilityMap`），
  不维护第二份清单：新增 ctx 成员自动可被 Python 调用，也就不会出现"JS 有、Python 没有"。
- 参数按位置绑定（JSON 数组 → 方法形参），支持 string / int / long / double / bool / string[] / object。
- 权限校验与 JS 走同一条路径（`ArgumentBinding` → 方法内的 `Require`），错误回包是给人看的中文。
  顺带把 `ctx.files` 也带给了 Python（此前是"技术限制暂不开放"）。

## 7. 离线登录恢复

- 身份算法：`MD5("OfflinePlayer:" + name)` → 置版本位 3 / 变体位 RFC4122 → 标准带连字符 UUID，
  与 Java 的 `UUID.nameUUIDFromBytes` 完全一致（单测钉了已知向量
  `OfflinePlayer:Notch` → `b50ad385-829d-3141-a216-7e7d7539ba7f`）。这样换启动器后存档归属、
  服务器白名单仍认同一个人。
- 存储：`AccountStore.AddOrUpdateOffline(name)`；`Load()` 不再清除离线账户，
  而是把"早期只存了名字、没有 uuid"的历史条目补算出来并回写。
- 启动：`LaunchAccountService.Mode` 增加 `offline`，`ResolveAsync()` 直接返回
  `LaunchCredentials(uuid, "0", "legacy")`——离线身份**没有凭证**，也不参与任何在线验证。
- 设置里的 `last_username` 前缀从 `ms:` / `ext:` 扩到 `off:`，跨启动记住上次用的身份类型
  （`LaunchAccountService.PrefixFor` / `ModeOfLastUsername`，主页模式切换与启动收尾共用）。
- UI：账户页「添加一个游戏账户」改成三选一（正版 / 第三方 / 离线），离线弹窗内实时显示派生 UUID，
  并写明"仅单人 / 局域网"；主页账户模式加第三个「离线」开关。
- 在线身份**不降级**：正版 / 第三方模式缺会话凭证时仍然抛 `AccountLoginRequiredException` →
  就地登录弹窗，绝不会偷偷用离线身份启动（避免用户以为自己在玩正版服务器）。对应单测
  `在线模式缺凭证仍然要求就地登录`。

## 8. 测试

| 测试 | 覆盖 |
| --- | --- |
| `PluginSdkTests`（15 项） | 权限门（装配 + 方法两层）、存储沙箱、设置读过滤 / 写白名单、账户脱敏、能力回调参数绑定、事件订阅与投递、JS 端到端读实例与游戏状态 |
| `PluginSystemTests`（原有 + 新增） | 离线 UUID 已知向量、离线账户增删、离线身份解析、`last_username` 前缀推断、在线模式不降级 |
| 全量 | 454 项通过 |

## 9. 后续可做（未纳入本轮）

- 挂载区 slot 扩充（目前只有 `download.top`）、插件面板间通信。
- 事件扩充（实例安装完成 / 联机状态变化 / 更新就绪）。
- 官方插件仓库与签名分发链路。
