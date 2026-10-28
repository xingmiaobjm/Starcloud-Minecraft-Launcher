# 主页卡片网格（可编辑主页）设计

## 1. 目标

主页不再是一块写死的两栏版面，而是一张**可编辑的卡片网格**：用户能自由摆放卡片、调整尺寸、
增删卡片，配置存在本地、跨启动保留。

对照对象：PCL2 / HMCL 的桌面布局自定义、PrismLauncher 的实例卡片墙、ATLauncher 的首页小部件。

## 2. 模型

### 2.1 格位

网格固定 **12 列 × 6 行**，主页不滚动。每张卡占一个矩形（列起点 / 行起点 / 跨列 / 跨行）。

- 12×6 正好把默认的两张卡铺满：版本卡 8×6（��� 0 行 0 列）+ 账户卡 4×6（第 8 列起）。
- 行高按可用高度均分（`StretchRows`），卡片与窗口同高 —— PCL2 式的整页版面。
- 列宽 = (可用宽 - 列间距×(列数-1)) / 列数。

### 2.2 存储

`%APPDATA%/starcloud-launcher/home-layout.json`：

```jsonc
{
  "schemaVersion": 3,
  "columns": 12,
  "rowHeight": 88,
  "toolboxOffsetX": 0,      // 工具箱相对默认位置的偏移（用户拖走后记住）
  "toolboxOffsetY": 0,
  "cards": [
    { "id": "versions", "type": "versions", "column": 0, "row": 0,
      "columnSpan": 8, "rowSpan": 6, "visible": true }
  ]
}
```

- `id` 是稳定标识，跨启动不变；配置按它对齐。
- `Normalized()` 负责把损坏配置收敛到合法范围：列数钳 4–16、跨度不小于 1、越界贴回网格、
  重复或空 id 的条目丢弃。**页面不需要再做防御**。
- 旧版本文件（schemaVersion < 3）直接回退出厂布局。

### 2.3 卡片规格

`HomeCardCatalog` 给出每类卡片的尺寸区间与档位：

| 卡片 | 最小 | 最大 | 出厂 | 档位 |
|---|---|---|---|---|
| 已安装版本 | 4×3 | 12×6 | 8×6 | 4×3 / 6×4 / 8×6 / 12×6 |
| 账户与启动 | 2×3 | 4×6 | 4×6 | 2×3 / 4×4 / 4×6 |
| 账户选择 | 2×2 | 6×4 | 4×3 | 2×2 / 4×3 / 6×4 |
| 启动按钮 | 2×1 | 6×2 | 4×2 | 2×1 / 4×2 / 6×2 |
| 收藏 / 最近 | 2×2 | 6×3 | 3×2 | 3×2 / 3×3 |
| 游玩时长 | 2×2 | 6×3 | 3×3 | 3×2 / 3×3 |

## 3. 交互

### 3.1 编辑模式

版本卡卡头的「布局」按钮进入编辑模式。此时：

- 每张卡浮起一层虚线框，右下角出现缩放把手，整卡变成可拖动面（内容不再吃点击）。
- 右缘浮出**编辑工具箱**：所有卡片类型的缩略图条目、选中卡片的尺寸档位、移除按钮、���成。
- 工具箱**浮在所有控件之上**，不参与主页布局 —— 主界面卡片的位置与大小一概不动。
- 按住工具箱标题条可把它拖到别处，位置写回 `home-layout.json`，下次进编辑模式还在那儿。

### 3.2 拖动

拖动时**卡片本体跟着指针走**（合成变换 `TranslateTransform`），原位留一个虚线占位框表示空位，
松手才吸附落位。拖动途中不重排网格 —— 实时重排会让整页抖动。

落点预览框画的是**真正会落下的位置与占格**：腾位规则可能把卡片换小一档或挪到别处，
预览必须提前反映出来，否则松手后卡片会「凭空变了位置和大小」。

### 3.3 腾位（加卡/改尺寸的落位决策）

`HomeCardPlacer.Place()` 是落位的唯一决策点，四条路依次尝试：

1. **目标格 + 出厂占格** —— 用户想要的位置，放得下就用。
2. **目标格 + 换小档位** —— 位置不动，卡片缩到放得下的最大档。
3. **整网格找空位** —— 位置让给别的卡，从大档位往小档位试。
4. **缩小已有卡片腾位（回溯重排）** —— 默认布局铺满 12×6 时，前三条都无处可放。
   把新卡放在用户指定的位置，再把其余卡片按各自尺寸重排一遍（保持相对顺序，行优先找位），
   放不下的卡片缩到下一档继续试；全部放得下才算成功。**只动必要的卡片**，并把变动如实报给用户。

为什么需要第 4 条：出厂布局正好占满整个网格，旧实现遇到重叠直接判定失败且不给提示，
用户看到的就是「从工具箱拖任何卡片都没反应」。第 4 条是唯一能让用户加卡的路。

为什么用回溯而不是贪心：贪心地把每张卡放到第一个空位，会出现「前面的卡占了位置，
后面的卡就再也放不下」的死局（8×6 + 4×6 铺满 12×6 就是如此）。网格只有 12×6、
卡片最多十来张，搜索空间很小，回溯完全够快。

### 3.4 缩略图

工具箱里每类卡片带一张示意图（`CardThumbnail`）：按类型画出内部结构 —— 标题条 / 列表行 /
圆形头像 / 主按钮药丸，与真实卡片里的元素一一对应。

不用真实控件渲染：真实卡片内容依赖运行时数据（版本列表、账户），在工具箱里渲染会把整份主页
数据都拉起来；而这里只需要示意「这张卡有哪几块内容」。

## 4. 实现要点与坑

### 4.1 指针捕获后位置被锁定（最坑的一条）

WinUI 3 里 `PointerRoutedEventArgs.GetCurrentPoint(panel).Position` 在**指针被捕获期间**
返回的是「按下那一刻」的位置，不再随光标走。拿它算拖拽位移，结果恒为 0 ——
表现为「按下去没反应」或「只有落点框在动，卡片本体不动」。

WinUI 3 的 `PointerPoint` 也**没有** `RawPosition`（那是 UWP `Windows.UI.Input.PointerPoint`
的属性），没有现成替代。

**解法**：P/Invoke `user32.dll` 的 `GetCursorPos`，拿屏幕物理坐标自己换算，
再除以 `XamlRoot.RasterizationScale` 折回 DIP（格位换算与 `RenderTransform` 都用 DIP）。

配套：

- 捕获指针的元素**不能**在拖动期间设 `IsHitTestVisible = false` —— 它一旦退出命中链，
  指针移动事件就不再路由过来、也不再冒泡到网格，拖拽彻底失效。
- WinUI 的 `Border` 透明背景（Alpha=0）**不参与命中测试**：拖动面要用 Alpha=1 的近全黑色
  才点得到。
- 自动化测试注入鼠标移动时，只用 `SetCursorPos`；`mouse_event(MOUSEEVENTF_MOVE, 0, 0, …)`
  的相对增量 0 会被当成绝对坐标 (0,0)，把光标搬到屏幕左上角。

### 4.2 自绘 Panel 而不是 Grid

`Grid` 的行列是静态定义的，卡片数量与跨度都可变时每改一次都要重建 RowDefinition；
`VariableSizedWrapGrid` 只做流式铺满，不支持「一个格一个格地移动」。这里用一个自绘 Panel
（`CardGridPanel`）：格位写在 `CardHost` 上，Arrange 时换算成像素矩形；拖动/缩放只改格位，
然后重排。

### 4.3 工具箱的层与位置

工具箱放在独立的 `Canvas` 里、用绝对坐标定位：`Canvas` 不参与 Grid 的尺寸分配，
工具箱既不会把主页内容挤窄，也不会让内容区变成 0 宽。

（把工具箱直接作为根 Grid 的兄弟节点、`HorizontalAlignment=Left` 也能叠在上层，
但 Grid 会把它算进水平分配，`HomeContent` 的 `Stretch` 被挤成 0 宽 —— 实测过一次。）

宽度取窄条（196 DIP）：主页内容区本身占满窗口宽（MaxWidth=1280 DIP 居中，右侧只剩约
100 DIP 留白），宽面板会把账户卡整块盖住。

底色用**不透明实色**（`ScFloatingPanelBrush`）：玻璃底（半透明）会把下面的账户卡透上来，
浮层压在内容上时糊成一片。

### 4.4 单一 C# 错误伪装成上百个 XAML 错误

本工程里任何一个 C# 编译错误都会让 XAML 编译器连锁报出上百个假错误
（`Unknown attachable member` / `Unknown target type` / `WMC1111 x:Bind 需要 x:DataType`）。
真实错误往往只有 1～3 条 `error CS`，埋在 170 个 XAML 错误中间。

**排查时先看 `error CS`**：

```
dotnet build dotnet/src/Starcloud.App/Starcloud.App.csproj -c Debug -v n --nologo \
  | grep -E "error CS" | sort -u
```

已踩过的具体例子：`Color` 是结构体不能 `const`（CS0283）；`CardHost` 继承 `Grid` 没有
`Elevation` 属性（CS0103）；忘了 `using Microsoft.UI.Xaml.Shapes;`（CS0246）；
自定义控件在构造函数里设 `AutomationId` 会触发 XAML 编译器内部错误 ——
`AutomationId` / `AutomationProperties.Name` 必须走 XAML 侧声明。

## 4.5 工具箱独立窗口（后续迭代）

页内浮层最终被替换为**独立顶层窗口**（`Views/Dialogs/ToolboxWindow`）。原因与做法：

- **为什么**：主页内容区占满窗口宽（MaxWidth 居中，右侧只剩约 100 DIP），任何页内浮层
  都会盖住账户卡；独立窗口拖到哪儿都行，主页一个像素不动。
- **交互约定**：工具箱窗口开着 = 主页处于编辑态；关掉即退出。主页被导航走时关掉工具箱。
- **跨窗口拖拽**：仍走「指针捕获 + Win32 `GetCursorPos` + 自己算格位」，不用 OLE 拖放。
  指针在工具箱窗口捕获，主窗口收不到事件，由工具箱窗口**主动跨窗口调用**主页网格：
  `GetCursorPos` → `mainWindow.DispatcherQueue.TryEnqueue` → `ShowDropPreviewAtScreenPoint`。
- **坐标换算**：用框架的 `XamlRoot.CoordinateConverter.ConvertScreenToLocal`
  （`Microsoft.UI.Content.ContentCoordinateConverter`，参数是 `Microsoft.UI.WindowId`——
  注意 `Windows.WindowId` 在本 SDK 下不存在），再减 `TransformToVisual(null)` 的网格偏移。
- **窗口线程**：独立窗口虽与主窗口同线程，但所有 AppWindow 操作统一走
  `DispatcherQueue.TryEnqueue`，不依赖这一事实。
- **独立窗口的坑**（全部实测）：
  - 窗口构造期间 `AppWindow.SetIcon` 等属性不可用（抛 ArgumentException），收尾要等
    `Activate()` 之后做（`FinishSetup`，由调用方显式调，`Activated` 事件在这条链路不保证触发）。
  - `AppWindow.Move` 在窗口刚显示时经常不生效（窗口停在系统默认的居中位置），
    改用 Win32 `SetWindowPos` 直接对 HWND 设位置立刻生效。
  - **一个 XAML 元素只能有一个父级**：把面板的子容器塞进第二个父级会抛
    ArgumentException（Value does not fall within the expected range）——整面板作为
    一个控件挂进窗口，不拆子元素。
  - **独立窗口不继承主窗口的 RequestedTheme**：`Appearance.ApplyTheme` 要对窗口根
    单独调一次；条目缩略图在宿主套主题**之前**就画好了（走系统主题），要监听
    `ActualThemeChanged` 重画。
  - **AcrylicBrush（应用内亚克力）跨窗口不可靠**：它取样窗口背后的内容——主窗口背后
    是壁纸层（浅色），工具箱背后是 DesktopAcrylic+桌面（用户毛玻璃强度 0 时壁纸原样
    透出，深色）。同一个画刷两个窗口渲染不一致，实测卡片发黑。缩略图卡面改用
    **主题半透明实色 + 玻璃缘**（液态玻璃的标准做法），不依赖取样。
  - 窗口背景与主窗口同语言：`AcrylicBackdropHost`（DesktopAcrylicController 手动挂载，
    必须激活后挂，见主窗口 `ApplyBackdropOnce` 注释的事故记录）+ 壁纸三层
    （`WindowBackgroundHost`，没有它 GlassCardBrush 取样不到内容）。
  - 位置记忆：设置项 `toolbox_window_x/y`（物理像素），`Closed` 时存、打开时钳进工作区。
  - 强引用：`App.Toolbox` 静态属性持有（WinUI 的 Window 不被引用会被 GC 关掉），
    `Cleanup` 时清掉。
- **降级**：独立窗口创建失败时回退到页内浮层（`HomePage.EditLayout_Click` 的 catch）。

## 5. 测试

### 5.1 单元测试（Core）

`HomeLayoutTests` 覆盖纯逻辑：越界钳制、像素吸附、空位查找、配置读写往返，
以及腾位规则的各条路径：

- 目标格放得下就用目标格且不缩尺寸。
- 网格有空位时按原档位落在空位而不是缩小。
- 网格占满时缩小已有卡片给新卡腾位（并校验被缩小的卡片确实变小）。
- 缩小腾位后新卡与所有卡片都不重叠、且都在网格内。
- 已占满且没有卡片能再缩小时才真的放不下。
- 移动自己的卡片时不把自己算作占位。
- 隐藏的卡片不占位。

### 5.2 UI 冒烟（UIA）

`dotnet/uia-toolbox-window.ps1`（独立窗口出现）、`dotnet/uia-cross-window-drag.ps1`
（从工具箱窗口拖卡到主窗口落位 + 落盘后无重叠）、`dotnet/uia-toolbox-lifecycle.ps1`
（工具箱开着=编辑态 / 关掉退出 / 位置记忆）与 `dotnet/uia-home-drag.ps1`（主页内卡片
拖动跟手；合成鼠标输入偶发丢按下事件，脚本带重试）：

- 进编辑模式前后**网格矩形必须完全一致** —— 工具箱悬浮不动主界面布局。
- 从工具箱把「收藏的版本」拖到主页 → `home-layout.json` 里 `visible=true`，
  且**所有可见卡片互不重叠**。
- 拖动已有卡片途中，卡片标题的屏幕位置必须跟着指针走（位移 > 40 px），
  否则就是「只有落点框在动」。
- 松手后落盘并给出新格位。

`dotnet/shot-window.ps1` 是配套的窗口截图脚本（前台化后按窗口矩形抓图）。
