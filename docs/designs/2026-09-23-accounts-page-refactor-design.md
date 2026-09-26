# 账户界面重构：列表 + 二级页

> 交付版本 0.3.1。目标：把账户相关操作从「一堆弹窗」改成与下载页一致的
> **一级列表 → 二级页（左分区菜单 + 右内容）**，让每一步都有地方把话说完。

## 1. 重构前的问题

| 问题 | 具体表现 |
| --- | --- |
| 主操作链全是弹窗 | 添加账户要三连弹窗（选身份 → 填表 → 结果提示），皮肤管理又是「账户卡按钮 → 皮肤库弹窗」 |
| 弹窗里塞不下 | 皮肤库要「列表 + 3D 预览 + 型号 + 导入 + 重命名 + 删除 + 同步」，弹窗高度不够，只能挤成两栏 |
| ContentDialog 不能嵌套 | 重命名、链接导入只好做成弹窗内的行内输入，交互与其他页面不一致 |
| 一级页负担过重 | 账户卡上挂三个按钮（设为默认 / 皮肤 / 删除），右侧还飘着一个 3D 形象，一屏信息密度过高 |

## 2. 新的信息结构

```
账户（一级：AccountsPage）
├─ Starcloud ID 卡        登录 / 注册 / 会话管理都在卡内（注册从弹窗改为卡内展开）
├─ 游戏账户卡片流         点卡片任意位置 = 进详情；卡上只留一个「设为默认」快捷按钮
│
├─ 添加游戏账户（二级：AccountAddPage）
│   ├─ 左：微软账户 / 第三方账户 / 离线账户
│   └─ 右：对应表单（就地填写、就地报错、成功即返回）
│
└─ 账户详情（二级：AccountDetailPage）
    ├─ 左：形象与皮肤 / 账户信息
    ├─ 形象与皮肤：3D 预览 + 皮肤库（导入 / 链接 / 重命名 / 删除 / 应用 / 取消 / 同步）
    └─ 账户信息：类型 / 用户名 / UUID / 认证服务器 / 会话状态 + 设为默认 / 删除账户
```

导航沿用版本详情页那一套（`MainWindow.NavigateToAccountDetail` / `NavigateToAddAccount`）：
直接 `Frame.Navigate` 且 `drillIn: true`，**不占用左侧导航选中项**，返回用 `SlideNavigationTransitionEffect.FromLeft`，
方向与进入互补。

## 3. 跨页状态交接

一级页 `NavigationCacheMode="Disabled"`，返回时是**新实例**（好处：账户列表、默认徽章、
会话状态一律重新读盘，不会显示旧值）。代价是没有地方接收二级页的结果，于是用一个静态交接点：

```csharp
internal static string? PendingStatus;   // AccountsPage
```

二级页成功后写 `AccountsPage.PendingStatus = "…"` 再返回，一级页 `Loaded` 里取走显示一次并清空。
这样「登录成功」「账户已删除」这类提示不需要弹窗，也不需要额外的消息总线。

## 4. 保留弹窗的场合

只有两类，且都不是「操作流程」：

1. **不可逆操作的二次确认**：删除账户、退出 Starcloud ID、删除版本等（确认框是安全底线）；
2. **没有页面可承载的场景**：启动游戏时的「就地登录」（`AuthDialogs`）与微软内置登录窗口无法启动时的浏览器回退。

顺带把登录动作抽成公共实现 `AccountFlows.LoginExternalAsync`：账户页的内联表单与启动前的
就地登录弹窗共用同一段「解析服务器 → 换票 → 写入账户与内存会话」，避免两处实现漂移。

## 5. 相关文件

| 文件 | 职责 |
| --- | --- |
| `App/Views/AccountsPage.xaml(.cs)` | 一级：Starcloud ID 卡 + 账户卡片流 |
| `App/Views/AccountAddPage.xaml(.cs)` | 二级：左身份 / 右表单（微软 · 第三方 · 离线） |
| `App/Views/AccountDetailPage.xaml(.cs)` | 二级：形象与皮肤 / 账户信息 |
| `App/Views/Shared/AccountFlows.cs` | 第三方登录的公共实现（页面与启动前弹窗共用） |
| `App/Views/Dialogs/AuthDialogs.cs` | 仅保留「启动时就地登录」所需的微软 / 第三方流程 |
| `App/Views/Rows/RowModels.cs` | `AccountRow`（卡片行）、`SkinRow`（皮肤库行） |

## 6. 皮肤库弹窗的去向

原 `Views/Dialogs/SkinDialogs.cs` 整块删除，能力平移到 `AccountDetailPage`：
库列表要滚动、3D 预览要拖拽、导入要选文件，页面空间才够；
应用 / 取消 / 删除皮肤都就地刷新（不再需要「关掉弹窗再刷新列表」这一层）。
皮肤库目录、索引、注入与上传逻辑（Core）完全没变，见
`docs/designs/2026-09-23-skin-library-design.md`。
