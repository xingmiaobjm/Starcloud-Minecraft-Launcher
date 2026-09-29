# 首页卡片（homeCards）设计

> 状态：已确认，待实现
> 关联：`docs/plugin-spec-v2.md` §6.2 / §7、`docs/plugin-sdk.md`

## 1. 背景

插件 2.0 的扩展点 `contributes.homeCards` 在规范里已有名字与校验，宿主侧一直是 ⏳。
它让插件在**启动器首页首屏**放一块自己的卡片 —— 首页是用户一打开就会看到的页面，
曝光最强，也最抢地盘。

与相邻扩展点的区别：

| 扩展点 | 出现在哪 | 典型用途 |
| --- | --- | --- |
| `panels` | 侧栏「插件」里属于它自己的整页 | 服务器列表管理、资源浏览 |
| `slots` | 塞进已有页面顶部的一条 | 推荐横幅、公告条 |
| `versionSections` | 版本详情页里多一个分区 | 该实例的健康报告 |
| `homeCards` | **首页首屏**，和版本库 / 账户卡抢高度 | 今日推荐、在线人数、待办提醒 |

首页是**固定视口布局**（无滚动条、卡片与窗口同高），任何新内容都得从版本库或账户卡身上
切走一块高度，所以必须先定清楚：放哪、多高、最多几张、能不能收起。

## 2. 位置（方案 A）

首页左列新增一行，夹在 `home.top` 挂载区与版本库之间：

```
左列 Row 0  Auto   通知 / 自检（收起时不占位）
     Row 1  Auto   home.top 挂载区         ← 已有
     Row 2  Auto   home.cards 卡片带       ← 本设计
     Row 3  *       版本库（版本卡）
右列            账户卡 + 启动按钮         ← 不动
```

选择理由：复用左列已有的「自动高度行」机制；不动右栏启动卡这个主 CTA。
代价是版本列表变矮，用固定高度上限抵消（卡片带不提供收起开关，见 §3）。

行高规则与 `home.top` 一致：没有任何插件贡献时整行 `Collapsed`、高度为 0，
**没装插件的用户看到的首页与现在完全一致**。

## 3. 卡片带形态

- 固定 **120 DIP 高**的横向卡片带，等宽排列，**最多 3 张**，卡间距 12。
- 每张卡用 `ScCard` 外观（与版本卡 / 账户卡同一套亚克力底 + 1px 描边 + 圆角 16），
  内部为卡头（清单里的 `title`，15px Medium）+ 声明式视图内容；内容超出部分裁切。
- 高度由宿主写死，**插件不能要求更高** —— 固定视口下这个数字必须可预测，
  否则一张卡就能把版本列表挤没。
- 第 4 张起跳过并记日志，不做横滑 / 分页。
- 视图解析或渲染失败时，卡片内部显示原因文字，不影响首页其它部分
  （与 `PluginContributionPanel.Hint` 同口径）。
- 卡片按插件加载顺序排列；不做卡片级排序、不做拖拽。
- **不做收起**：注册了就摆着，没注册整块不占位。不想看就在插件页把那个插件停掉 ——
  停用即摘除卡片（`PluginHost.Stop`），比多一个折叠开关更符合「插件的内容归插件管」。
  （早期版本做过一条可收起的窄条，用户评估后否掉了：收起态是一条 896px 宽、只有六个字的空条，不好看。）

## 4. 数据流与改动清单

```
manifest.json  contributes.homeCards[{id,title,view}]
      │  PluginManifest.Validate（已存在，校验 id 与包内相对路径）
      ▼
PluginHost.RegisterContributedHomeCards()   ← 要求 ui:panel 能力，否则不注册并记日志
      │  _homeCards: HomeCardRegistration[]
      ▼
PluginHost.GetHomeCards()  +  ContributionsChanged 事件
      ▼
HomeCardStrip（App 层控件）→ 等宽卡片带
      │  每张卡：PluginViewRenderer 解析视图文件 → 原生控件树
      ▼
HomePage.xaml 左列 Row 2
```

| 层 | 文件 | 改动 |
| --- | --- | --- |
| 清单模型 | `Starcloud.Core/Plugins/PluginManifest.cs` | 已有 `contributes.homeCards`，不动 |
| Core | `Starcloud.Core/Plugins/PluginHost.cs` | 新增 `HomeCardRegistration` 记录、`_homeCards` 列表、`GetHomeCards()`、`RegisterContributedHomeCards()`、`Stop` 里摘除 |
| App | `Starcloud.App/Views/Controls/HomeCardStrip.cs`（新建） | 订阅 `ContributionsChanged`、取卡片、等宽布局；无卡片时整块 `Collapsed` |
| App | `Starcloud.App/Views/Plugins/PluginViewRenderer.cs` | 抽出「解析视图文件 → 渲染控件树」的共享入口，供挂载区与卡片带共用 |
| App | `Starcloud.App/Views/HomePage.xaml(.cs)` | 左列加 Row 2、版本卡下移一行、卡片内动作结果接通知条 |
| 文档 | `docs/plugin-spec-v2.md`、`docs/plugin-sdk.md` | §6.2 homeCards 改 ✅ 并补用法 |

## 5. 明确不做（YAGNI）

- 卡片带收起 / 展开（评估后否掉：收起态是一条空条，不如「不想看就停用插件」）
- 卡片自定义高度（宿主固定 120，插件不能要更高）
- 超过 3 张的横滑 / 分页 / 轮播
- 卡片排序配置、拖拽
- 卡片级右键菜单
- HTML 形态的 homeCards（只支持声明式视图）
- 卡片内的独立滚动（内容超出即裁切，插件应自己控制内容量）

## 6. 验证

- Core 单测：多张卡片注册、跨插件共存、无 `ui:panel` 能力时不注册、`Stop` 后卡片被摘除、
  已注册插件重复注册不产生重复条目。
- 全解决方案 build 0 错 0 警；测试全绿。
- 实机复验（截图）：装带 homeCards 的演示插件时卡片带出现、版本卡未被挤坏；
  禁用插件后首页与改动前逐像素一致。
