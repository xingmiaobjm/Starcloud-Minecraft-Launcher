# 插件导航项（navItems）设计

> 状态：已实现（`PluginHost.RegisterContributedNavItems` + `PluginNavPage` + `MainWindow.RebuildPluginNavGroup`）
> 关联：`docs/plugin-spec-v2.md` §6.2 / §6.4、`docs/plugin-sdk.md`、`docs/plans/2026-09-29-plugin-nav-items-plan.md`

## 1. 背景与目标

插件 2.0 的 `contributes` 目前有 `panels`（侧栏「插件」页里的面板）/ `slots`（页面挂载区）/
`versionSections` / `homeCards` / `contextMenu`(⏳) / `settings`(⏳)。

重插件（服务器浏览、实例管理这类）只有一个藏在「插件」页里的面板，入口太深。
本设计增加扩展点 `contributes.navItems`，让插件在**左侧导航栏**拿到一个一级入口。

定位很明确：**数量极少**。导航栏是全局常驻的一级信息结构，插件挤进来比首页卡片更「抢地盘」——
首页卡片最多挤掉一行高度，导航项会直接改变启动器的信息结构。所以上限卡得很死（见 §4）。

## 2. 位置：尾部新分组（方案 A）

导航栏现状：

```
首页 / 版本 / 下载 / 存档 / 联机 / 运行状态      ← 主功能
───────────────────────────────────────────
账户 / 插件 / Nova                              ← 个人与扩展
───────────────────────────────────────────
▸ 服务器浏览  ▸ 实例管理                        ← 本设计：插件组（最多 3 项）
───────────────────────────────────────────
启动器日志 / 设置                               ← FooterMenuItems
```

选择理由：

- 不打断内置分组，Nova 这个品牌位保持在扩展组末位；
- 一条分隔线把「第三方提供的能力」明确隔出来，用户一眼知道这不是启动器自带功能；
- 放在 Footer（与日志/设置同组）会让插件能力被当成系统项忽略，放在「插件」与「Nova」之间
  会把 Nova 往后挤 —— 都不如独立成组。

实现位置：`NavigationView.MenuItems` 末尾（Nova 之后）追加 `NavigationViewItemSeparator` + N 个
`NavigationViewItem`。整组在没有任何插件导航项时**整体不出现**（连分隔线一起），
所以没装插件的用户看到的导航栏与从前完全一致。

## 3. 页面形态：独立一级页

点导航项 → 宿主页 `PluginNavPage`，按 `(插件 id, 导航 id)` 渲染该插件的声明式视图。

- 复用 `PluginViewRenderer.RenderFile`（与挂载区、首页卡片带同一份解析 + 渲染 + 错误兜底）
  与同一套能力层，动作结果条与 `PluginPanelPage` 同口径。
- **没有返回按钮**：它是一级页面，导航栏本身就是返回；`PluginPanelPage` 那个返回按钮是
  「从插件列表点进来」的二级页才需要的。
- 页面标题用插件声明的 `title`（不参与宿主本地化链，见 §4）。
- 插件已卸载 / 视图解析失败 → 页面内显示原因文字 + 一个回「插件」页的按钮，
  与挂载区失败时的兜底同口径，不让首页崩或导航失灵。

不采用「只作快捷方式」（点导航项跳到「插件」页并选中该插件的面板）：语义别扭（一级导航的落点
是一个列表页里的某一项），且「插件」页要额外维护「从导航进来」的选中态与返回逻辑。

## 4. 清单与规则

```json
"contributes": {
  "navItems": [
    { "id": "servers", "title": "服务器浏览", "icon": "server", "view": "views/servers.json" }
  ]
}
```

| 项 | 口径 | 说明 |
| --- | --- | --- |
| 字段 | `id` / `title` / `icon` / `view` | `view` 走已有的包内相对路径校验；`title`、`icon` 必填 |
| 每插件数量 | **最多 1 个**，超过即**清单校验失败** | 一级位置是稀缺的全局资源，schema 就要写明；早报早改，不搞「写了 3 个只有 1 个生效」 |
| 跨插件上限 | **合计最多 3 个** | 宿主展示策略（不是清单错误）：按插件加载顺序取前 3，其余不显示并写一行 `launcher.log`，与 homeCards 最多 3 张同一套 |
| 图标 | 只能填**宿主内置图标名**（白名单，8 个） | 名字不在表里 → 退回默认图标 + 写日志，**不整项拒绝**（不为一个图标名让插件功能不可达）。不允许自带图片 / SVG，否则插件能往全局 UI 塞任意资源 |
| 权限 | 沿用 `ui:panel` | 与 `panels` / `slots` / `homeCards` 一致；没有则整批不注册并记日志 |
| 标题本地化 | **不参与**宿主本地化链 | 直接显示插件给的字符串。插件侧目前没有 i18n 机制，写进规范免得有歧义 |
| 启停 | 跟随插件启停热增删 | 订阅已有的 `PluginHost.ContributionsChanged` 重建整组 |
| 路由 tag | `plugin:<插件 id>:<导航 id>` | 前缀保证与内置 tag（`home` / `versions` / …）不可能撞；拼/解由 Core 的 `PluginExtensionPoints.PluginNavTag` / `TryParseNavTag` 提供（纯函数、可单测），宿主只调用、不自己拼字符串 |

**图标白名单**（Core 的 `PluginExtensionPoints.NavIcons`，名字 → 字形；字形复用导航栏里
**已经在用、渲染已验证**的那批 HarmonyOS 图标，不新挑字形，避免缺字风险。
表放 Core 是为了让白名单与回落规则能被单测覆盖，App 只负责把字形挂到 `IconFont.Family` 上渲染）：

| 名字 | 语义 | 字形 |
| --- | --- | --- |
| `home` | 首页 / 概览 | `\uEC77`（首页） |
| `instance` | 版本 / 实例 | `\uEA94`（版本） |
| `download` | 下载 / 资源 | `\uE90C`（下载） |
| `saves` | 存档 / 世界 | `\uEA90`（存档） |
| `server` | 联机 / 服务器 | `\uEB91`（联机） |
| `account` | 账户 / 身份 | `\uEB8F`（账户） |
| `extension` | 扩展 / 插件（**默认**） | `\uEABC`（插件） |
| `log` | 日志 / 记录 | `\uEC64`（启动器日志） |

`PluginManifest.Validate` 仍**只校验 `icon` 非空**，不因名字未命中而拒绝整项（fail-soft）：
命中与否在装配导航栏时用 `TryResolveNavIcon` 判定，未命中回落默认图标（`extension`）并写一行日志。

**当前页被摘掉时的回退**：停用插件导致正在看的导航项消失 → 回到「插件」页（不留在空页面上）。

## 5. 数据流与改动清单

```
manifest.json  contributes.navItems[{id,title,icon,view}]
      │  PluginManifest.Validate（id/title/icon 非空、view 包内相对路径、数量 ≤1）
      ▼
PluginHost.RegisterContributedNavItems()    ← 要求 ui:panel，否则不注册并记日志
      │  _navItems: NavItemRegistration[]
      ▼
PluginHost.GetNavItems()  +  ContributionsChanged
      ▼
MainWindow：重建「插件导航组」（分隔线 + N 项，按加载顺序取前 3）
      ▼
点项 → 路由 tag plugin:<pid>:<nid> → ContentFrame.Navigate(PluginNavPage, (pid, nid))
      ▼
PluginNavPage → PluginViewRenderer.RenderFile 渲染声明式视图
```

| 层 | 文件 | 改动 |
| --- | --- | --- |
| 清单模型 | `Starcloud.Core/Plugins/PluginManifest.cs` | `Contributes` 加 `NavItems`（`PluginNavRef`: Id/Title/Icon/View）；`Validate` 加 id/title/icon 与数量校验；`PluginExtensionPoints` 加导航 tag 拼/解 + 图标白名单与回落 |
| Core | `Starcloud.Core/Plugins/PluginHost.cs` | 新增 `NavItemRegistration`、`_navItems`、`GetNavItems()`、`RegisterContributedNavItems()`、`Stop` 里摘除 |
| App | `Starcloud.App/Views/PluginNavPage.xaml(.cs)`（新建） | 一级页：标题 + 声明式视图 + 动作结果条 + 失败兜底 |
| App | `Starcloud.App/MainWindow.xaml(.cs)` | 订阅 `ContributionsChanged` 重建插件导航组；路由从 `tag switch` 改成「内置表 + `plugin:` 前缀解析」；当前项被摘掉时回落「插件」页 |
| 文档 | `docs/plugin-spec-v2.md`、`docs/plugin-sdk.md` | §6.2 表格加一行 + 新增 §6.4 章节；SDK 补用法 |

## 6. 明确不做（YAGNI）

- 插件自定义排序权重（顺序 = 插件加载顺序）
- 导航项下的二级子项 / 折叠分组
- 插件自带页面外壳、标题栏、返回按钮
- 导航项角标、未读数、运行中状态点
- 带参数路由（导航项的 view 不接受参数）
- 自带图片 / SVG 图标

## 7. 验证

- 单测：清单声明 navItems → 注册出条目；每插件 >1 条被 `Validate` 拒绝；跨插件 >3 条截断且不报错；
  图标名未命中 → 退回默认图标；无 `ui:panel` → 不注册；`Stop` 摘除；路由 tag 解析（含非法 tag）。
- 全解决方案 build 0 错 0 警；测试全绿。
- 实机复验（截图）：演示插件加一个 navItem → 导航栏尾部出现独立分组项、点开渲染正常；
  停用插件 → 项与分隔线一起消失、当前页回落「插件」页；无任何插件导航项时导航栏与从前逐像素一致。
