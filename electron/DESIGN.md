# Starcloud Launcher — Electron / Fluent UI v9 设计规范

> **来源（ground truth）**：本规范所有色值、尺寸、时长均逐条抄自 WinUI 3 桌面端的资源与主题定义，
> 未在代码中找到的一律标注「代码中未定义」，不做臆造。
>
> | 源文件 | 作用 |
> | --- | --- |
> | `dotnet/src/Starcloud.App/App.xaml` | 主题字典（Light/Dark）、圆角令牌、字体、全局样式与控件模板 |
> | `dotnet/src/Starcloud.App/MainWindow.xaml(.cs)` | 三段式窗口骨架、标题栏、导航窗格、亚克力配方、动效等级 |
> | `dotnet/src/Starcloud.App/Views/HomePage.xaml(.cs)` | 首页双栏布局（设计基准） |
> | `dotnet/src/Starcloud.App/Views/VersionsPage.xaml` / `DownloadPage.xaml` / `SettingsPage.xaml` | 列表行、分段控件、设置页卡片与左分类导航 |
> | `dotnet/src/Starcloud.App/Services/MotionFx.cs` / `MotionService.cs` | 全部时长与缓动曲线 |
> | `dotnet/src/Starcloud.App/Views/Behaviors/*.cs` | 悬停 / 按下 / 卡片 / 行的反馈参数 |
> | `dotnet/src/Starcloud.App/Views/Controls/*.xaml` | 空状态、对话气泡、确认清单卡 |
>
> **迁移对象**：`electron/`（React 18 + `@fluentui/react-components` ^9.60）。

---

## 0. 设计世界与三条铁律

来自根 `DESIGN.md` §1：把启动器做成「Windows 11 的第 17 个系统应用」，工艺基准为
**Microsoft Store + Windows 11 设置**。明确拒绝深色电竞风、霓虹、大面积毛玻璃、WebView 假原生。

落到实现的三条约束（贯穿本文档每一节）：

1. **强调色只出现在三处 + 主按钮**：小节标题的 3px 脊柱、元信息强调芯片、分段控件指示器、
   强调色主按钮。其余一律中性。品牌黄亮度高，**所有 accent 面上强制黑字**。
2. **强调色之上不放卡片色**：任何需要置顶的浮层（面板 / 抽屉 / 空态图标）都用 `GlassCardBrush`
   或实色面，不用品牌色铺底。
3. **一切动效过 `MotionService` 闸门**：系统「显示动画」关闭 → 全部瞬时到位、零开销。

---

## 1. 色彩体系

### 1.1 品牌与强调色（双主题同值）

| 语义名 | 代码键 | Light | Dark | 用途 |
| --- | --- | --- | --- | --- |
| 品牌主色 | `SystemAccentColor` | `#FFD322` | `#FFD322` | 图标 / 品牌图形 / 强调色面 |
| Accent L1 | `SystemAccentColorLight1` | `#FFDD4D` | `#FFDD4D` | 派生色阶（代码中未直接使用） |
| Accent L2 | `SystemAccentColorLight2` | `#FFE780` | `#FFE780` | 派生色阶（代码中未直接使用） |
| Accent L3 | `SystemAccentColorLight3` | `#FFF0B3` | `#FFF0B3` | 派生色阶（代码中未直接使用） |
| Accent D1（悬浮） | `SystemAccentColorDark1` | `#FFBC00` | `#FFBC00` | 主按钮 hover 底色 |
| Accent D2（按下） | `SystemAccentColorDark2` | `#D9A000` | `#D9A000` | 派生色阶 |
| Accent D3 | `SystemAccentColorDark3` | `#B38500` | `#B38500` | 派生色阶 / 浅色主题的 accent 文字色 |
| AccentFill Default | `AccentFillColorDefaultBrush` | `#FFD322` | `#FFD322` | 主按钮底、脊柱、强调芯片、头像底、进度条、状态点 |
| AccentFill Secondary | `AccentFillColorSecondaryBrush` | `#FFBC00` | `#FFBC00` | 悬浮态 |
| AccentFill Tertiary | `AccentFillColorTertiaryBrush` | `#E6A800` | `#E6A800` | 按下态 / 「缓存」徽标底 |
| AccentFill Disabled | `AccentFillColorDisabledBrush` | `#99FFD322` | `#99FFD322` | 禁用主按钮底（60% 不透明） |
| Accent 文字 | `AccentTextFillColorPrimaryBrush` | `#B38500` | `#FFD322` | 深色底上的品牌色文字用品牌黄 |
| Accent 文字 Secondary | `AccentTextFillColorSecondaryBrush` | `#CCB38500` | `#CCFFD322` | 80% 不透明 |
| Accent 文字 Disabled | `AccentTextFillColorDisabledBrush` | `#5CB38500` | `#5CFFD322` | 36% 不透明 |
| 品牌蓝（旧名） | `BrandBlueBrush` | `#FFD322` | `#FFD322` | 图标着色（实测仍是黄，不是蓝） |
| accent 面文字 | `TextOnAccentFillColorPrimaryBrush` | `#E61A1A1A` | `#E61A1A1A` | 主按钮、分段指示器、头像首字母上的文字 |
| accent 面文字 Secondary | `TextOnAccentFillColorSecondaryBrush` | `#B31A1A1A` | `#B31A1A1A` | 70% 不透明 |
| accent 面文字 Disabled | `TextOnAccentFillColorDisabledBrush` | `#5C1A1A1A` | `#5C1A1A1A` | 36% 不透明 |

**主按钮四态**（`App.xaml` 直接覆盖框架 `AccentButton*` 别名，Light/Dark 同值）：

| 态 | 底色 | 文字 |
| --- | --- | --- |
| Normal | `#FFD322` | `#E61A1A1A` |
| PointerOver | `#FFBC00` | `#E61A1A1A` |
| Pressed | `#E6A800` | `#B31A1A1A` |
| Disabled | `#99FFD322` | `#5C1A1A1A` |

> ⚠️ **与根 `DESIGN.md` §2 的差异**：根文档写「深色主题品牌色 `#3DD322`（绿）」，
> 而当前 `App.xaml` 已统一为黄（`#FFD322` / `#FFBC00` 起）。以 `App.xaml` 为准。

CSS 落地：

```css
:root {
  --sc-accent:            #FFD322;
  --sc-accent-hover:      #FFBC00;
  --sc-accent-pressed:    #E6A800;
  --sc-accent-disabled:   #99FFD322;
  --sc-accent-text:       #E61A1A1A;   /* accent 面上的文字 */
  --sc-accent-text-press: #B31A1A1A;
}
```

### 1.2 背景层级（窗口壳）

| 语义 | 代码键 | Light | Dark | CSS |
| --- | --- | --- | --- | --- |
| 窗口亚克力 Tint | `TransparentAcrylicBackdrop` | `#FCFCFC` @ TintOpacity 0.25 / Luminosity 0.18 | `#141414` @ 0.45 / 0.30 | `backdrop-filter: blur(...)` + 遮罩 |
| 亚克力回退色 | 同上 `FallbackColor` | `#F3F3F3` | `#111111` | 无 backdrop-filter 时的纯色底 |
| 标题栏 / 侧栏底 | `AppPaneBackgroundBrush` | `#F6F6F6` | `#161616` | 不透明实色 |
| NavigationView 窗格底 | `NavigationViewDefaultPaneBackground` / `NavigationViewExpandedPaneBackground`（在 `MainWindow.xaml` 的控件级资源覆盖） | `#F6F6F6` | `#161616` | 同上 |
| 内容区底 | `NavigationViewContentBackground`（**保持框架默认** `LayerFillColorDefaultBrush`） | 半透明 | 半透明 | 必须透明，否则整窗毛玻璃消失 |

亚克力遮罩不透明度由设置项「毛玻璃强度」驱动，默认值与主题同源
（`AppearanceService.DefaultMaskOpacity`：Light 0.25 / Dark 0.45，钳制范围 0.02–0.95）。
自定义背景图 `Opacity="0.7"`（`MainWindow.xaml` 的 `BackgroundImage`）。

### 1.3 表面填充（卡片 / 层）

| 语义 | 代码键 | Light | Dark | 用途 |
| --- | --- | --- | --- | --- |
| 毛玻璃卡片 | `GlassCardBrush`（Acrylic） | Tint `#FFFFFF` @ 0.72，Fallback `#F9F9F9` | Tint `#2B2B2B` @ 0.75，Fallback `#1F1F1F` | 全站卡片、抽屉头、顶栏内层方块、浮层横幅 |
| 表面层 | `LayerFillColorDefaultBrush`（框架） | 未覆盖 | 未覆盖 | 空态图标底、任务卡底（代码中未自定义色值） |
| 实色基面 | `SolidBackgroundFillColorBaseBrush`（框架） | 未覆盖 | 未覆盖 | Nova 面板底 |
| 次填充 | `ControlFillColorSecondaryBrush`（框架） | 未覆盖 | 未覆盖 | 分段控件外框、标题栏版本徽标、右侧折叠按钮 |
| 弱填充 | `SubtleFillColorSecondaryBrush`（框架） | 未覆盖 | 未覆盖 | 32px 版本方块图标底 |
| 弱填充三级 | `SubtleFillColorTertiaryBrush`（框架） | 未覆盖 | 未覆盖 | 默认元信息芯片 `ScChip` 底 |
| 卡片式按钮悬停层 | `SubtleFillColorSecondaryBrush`（模板内 `HoverLayer`） | — | — | 卡片按钮悬停/按下微色 |

**毛玻璃卡片的 CSS 落地**（Acrylic → CSS 只能退化为半透明+模糊）：

```css
/* Light */
--sc-surface:        rgba(255, 255, 255, 0.72);
--sc-surface-solid:  #F9F9F9;
/* Dark */
--sc-surface:        rgba(43, 43, 43, 0.75);
--sc-surface-solid:  #1F1F1F;
/* 使用 */
.sc-card {
  background: var(--sc-surface);
  backdrop-filter: blur(30px) saturate(125%);
  border: 1px solid var(--sc-stroke);
  border-radius: 16px;
  padding: 16px;
}
```

### 1.4 边框 / 描边

| 语义 | 代码键 | Light | Dark |
| --- | --- | --- | --- |
| 卡片描边 | `CardStrokeColorDefaultBrush` | `#E2E2E2` | **代码中未定义**（沿用框架深色值） |
| 控件描边 | `ControlStrokeColorDefaultBrush`（框架） | 未覆盖 | 未覆盖 |
| 抽屉左边线 | `CardStrokeColorDefaultBrush`，`BorderThickness="1,0,0,0"` | — | — |
| 导航窗格 1px 描边 | `NavigationViewBorderThickness` / `NavigationViewContentGridBorderThickness` 覆写为 `0` | — | — |
| 内容区左上圆角 | `NavigationViewContentGridCornerRadius` 覆写为 `0` | — | — |
| 聚焦环 | `FocusVisualPrimaryBrush` / `FocusVisualPrimaryThickness=2` / `FocusVisualSecondary=Transparent` | `#B38500` / 2px | `#FFD322` / 2px |
| 行「压暗底」 | `ScRowShadeBrush` | `#0A000000`（黑 4%） | `#38000000`（黑 22%） |

> `ScRowShadeBrush` 的深浅差异是刻意的：两个主题都必须是**压暗**，深色下若用 Fluent 的半透明白叠加，
> 整行会变成白框，与黑色径向光斑方向相反，看着就是「阴影是白的」。

聚焦环 CSS：

```css
:focus-visible {
  outline: 2px solid var(--sc-focus);   /* Light #B38500 / Dark #FFD322 */
  outline-offset: 0;
  border-radius: inherit;
}
```

### 1.5 文本三级色 + 禁用

| 语义 | 代码键 | Light | Dark | 用量排名（全仓统计） |
| --- | --- | --- | --- | --- |
| Primary | `TextFillColorPrimaryBrush` | `#1A1A1A` | `#F5F5F5` | 8 处显式 + 大量默认继承 |
| Secondary | `TextFillColorSecondaryBrush` | `#666666` | `#A0A0A0` | **159 处**（最高频语义色） |
| Tertiary | `TextFillColorTertiaryBrush` | `#99666666`（60%） | `#87A0A0A0`（53%） | 24 处 |
| Disabled | `TextFillColorDisabledBrush` | `#5C666666`（36%） | `#57A0A0A0`（34%） | 少用 |

```css
:root {
  --sc-text-1: #1A1A1A;   --sc-text-2: #666666;
  --sc-text-3: #99666666; --sc-text-disabled: #5C666666;
}
[data-theme='dark'] {
  --sc-text-1: #F5F5F5;   --sc-text-2: #A0A0A0;
  --sc-text-3: #87A0A0A0; --sc-text-disabled: #57A0A0A0;
}
```

### 1.6 语义状态色

| 语义 | 代码键 | Light | Dark | CSS |
| --- | --- | --- | --- | --- |
| 成功 | `SystemFillColorSuccessBrush` | `#3DDC84` | `#3DDC84` | `#3DDC84` |
| 成功背景 | `SystemFillColorSuccessBackgroundBrush`（框架） | 未覆盖 | 未覆盖 | InfoBar Success 底 |
| 危险 / 错误 | `SystemFillColorCriticalBrush` | `#F44336` | `#F44336` | `#F44336` |
| 危险（别名） | `DangerBrush` | `#F44336` | `#F44336` | 同上，危险操作文案与删除按钮 |
| 成功（别名） | `SuccessBrush` | `#3DDC84` | `#3DDC84` | 同上 |
| 警告（Attention） | `SystemFillColorAttentionBrush`（框架） | 未覆盖 | 未覆盖 | InfoBar Warning 走框架默认 |
| 警告（Caution） | `SystemFillColorCautionBrush`（框架） | 未覆盖 | 未覆盖 | 历史通知「警告」级别 |
| 提示芯片底 | `CautionChipBackgroundBrush` | `#1FF59E0B`（12% 琥珀） | `#2EF59E0B`（18% 琥珀） | |
| 提示芯片文字 | `CautionChipTextBrush` | `#B45309` | `#FBBF24` | |
| 调度中心遮罩 | `TaskCenterScrim`（`MainWindow.xaml` 内联） | `#5A000000` | 同值 | 抽屉/遮罩共用 |

**信息色**：代码中未定义独立的 brand info 蓝。框架 `SystemFillColorAttentionBrush`（蓝）
由 Fluent 默认承担，对应 InfoBar 的 `Informational` / `Warning` 两个 Severity。

### 1.7 Nova 专属色（仅 AI 助手）

| 语义 | 代码键 | 色值 | 含义 |
| --- | --- | --- | --- |
| 待命 | `NovaStateIdleBrush` | `#71717A` | 中性灰 |
| 思考 | `NovaStateThinkingBrush` | `#7C3AED` | 星云紫 |
| 有提案 | `NovaStatePlanBrush` | `#F59E0B` | 琥珀 |
| 观察中 | `NovaStateObservingBrush` | `#10B981` | 翠绿 |
| 头像 Orb | `NovaOrbBrush`（仅 Dark 定义） | `#A78BFA` | 深色星云紫亮调 |
| 提案小圆点 | `NovaOrbDot`（`MainWindow.xaml` 内联） | `#F59E0B` 8px 圆点 | 右上角角标 |

---

## 2. 圆角与阴影

### 2.1 圆角令牌（`App.xaml` 顶部，全局）

| 令牌 | 值 | 用途 |
| --- | --- | --- |
| `ControlCornerRadius` | **12** | 控件（按钮 / 输入框） |
| `CardCornerRadius` | **20** | 卡片 |
| `OverlayCornerRadius` | **16** | 弹出层 |

> 现状提醒：这三个令牌在全仓 XAML 中**实际引用为 0**，页面里圆角是硬编码的
> （设计稿 `2026-09-11-ui-polish-motion-design.md` §1 已记录这一事实）。
> 下表的"实际"列才是可以直接照抄的值。

### 2.2 实际圆角（按使用频次，全仓统计）

| 圆角 | 出现次数 | 典型用途（代码实例） |
| --- | --- | --- |
| **8** | 65 | 版本行、32px 方块图标、分类导航项、启动自检卡、版本卡投影环内层、Nova 确认清单卡内层 |
| **16** | 24 | 首页两张主卡（`VersionCard` / `AccountCard`）、`ScCard`、Nova 面板、抽屉头 |
| **4** | 18 | `ScChip` 元信息芯片、`ScSectionSpine` 脊柱、进度条 |
| **10** | 17 | `ScGlowCardButtonStyle` 抽屉头按钮、`ScCategoryNavItemStyle` 左分类导航项、分段控件外框（HomePage） |
| **12** | 7 | `ScHoverCard`、Nova 确认清单卡、下载对话框资源卡、浮层横幅 |
| **7** | 6 | 分段控件的内段与指示器（HomePage 三段） |
| **6 / 9 / 14 / 24 / 20 / 32 / 28 / 11 / 5 / 2** | 各 1–3 | 头像块圆角 12、启动/折叠按钮 9、设置左分类卡 8、"你知道吗"卡 8、投影环 20/24/28/32、下载页分段外框 11、版本数徽标 5、脊柱 2 |

**按层归纳**：

```css
:root {
  /* 令牌（设计意图） */
  --sc-radius-control: 12px;   /* 控件默认 */
  --sc-radius-card:    20px;   /* 卡片令牌 */
  --sc-radius-overlay: 16px;   /* 弹出层 */

  /* 实际取值（照抄代码） */
  --sc-radius-main-card:    16px;  /* 首页两张主卡、静态卡 ScCard、Nova 面板 */
  --sc-radius-card-hover:   12px;  /* ScHoverCard（可悬浮卡） */
  --sc-radius-row:           8px;  /* 列表行、图标块、内层卡 */
  --sc-radius-control:       8px;  /* TextBox 全局样式 */
  --sc-radius-chip:          4px;  /* 元信息芯片 */
  --sc-radius-spine:         2px;  /* 3px 脊柱的圆角 */
  --sc-radius-avatar:       12px;  /* 72px 头像块 */
  --sc-radius-cta:           9px;  /* 60px 启动大按钮 */
  --sc-radius-segment-in:    7px;  /* 分段控件内段 */
  --sc-radius-pill:         16px;  /* 32px 圆形头像 / 40px 圆形头像 */
  --sc-radius-badge:         7px;  /* 15px 任务徽标 */
}
```

**Fluent UI v9 对照**：v9 的 `borderRadiusMedium` = 6px、`borderRadiusLarge` = 8px、`borderRadiusXLarge` = 12px、
`borderRadiusCircular` = 10000px。本项目整体**比 v9 默认更方一档**，在 v9 中需显式覆写：

```ts
const useStyles = makeStyles({
  card:  { borderRadius: '16px' },
  row:   { borderRadius: '8px' },
  input: { borderRadius: '8px', borderWidth: '1px' },   // 见 §5.2
  chip:  { borderRadius: '4px', padding: '1px 6px' },
})
```

### 2.3 阴影约定

**这个项目几乎不用 CSS `box-shadow`。** 阴影一律是「XAML 元素」：几个同心半透明圆角矩形靠
`RenderTransform` / `Margin` 外扩，`HoverShadowBehavior` 只对它的合成 `Opacity` 做动画。
（`ThemeShadow` / `CompositionProjectedShadow` / `DropShadow` 三种合成阴影实测在本机均不渲染或图层错乱。）

| 阴影载体 | 色值（浅 / 深） | 用法 |
| --- | --- | --- |
| 按钮软阴影外层 | `ScBtnShadowOuterBrush` `#06000000` / `#16000000` | `ScGlowButtonTemplate` 的 `ShadowHost`，`Margin="-2"`，两层同心矩形 |
| 按钮软阴影内层 | `ScBtnShadowInnerBrush` `#0C000000` / `#24000000` | 同上，`Margin="0,1,0,2"` |
| 版本卡投影环 | 4 条同心描边：`#0F000000` / `#0B000000` / `#08000000` / `#04000000`，各 `BorderThickness=4`，`Margin` 依次 `-4 / -8 / -12 / -16`，圆角 `20 / 24 / 28 / 32` | 只在版本卡收起态露面 |
| 跟手光斑（列表行 / 分段条，标准档） | 径向渐变 `#28000000 → #1F000000 → #16000000 → #0D000000 → #05000000 → #00000000`（Offset 0 / .18 / .40 / .62 / .82 / 1），`RadiusX/Y = 0.9` | 15 处，`CornerRadius` 随行 |
| 跟手光斑（小方块加强档） | `#3E000000 → #30000000 → #22000000 → #14000000 → #08000000 → #00000000` | 顶栏按钮内阴影、窗口按钮光斑（6 处） |
| 跟手光斑（左分类导航项） | `#18000000 → #12000000 → #0D000000 → #08000000 → #04000000 → #00000000` | 分类导航项 `Glow` 层 |
| 抽屉遮罩 | `#5A000000`（35% 黑） | 调度中心抽屉 |

**跟手光斑的 CSS 等价**（保留全部渐变档位）：

```css
.sc-pointer-glow {
  position: absolute;
  inset: 0;
  border-radius: inherit;
  opacity: 0;
  pointer-events: none;
  background: radial-gradient(
    ellipse 90% 90% at var(--px) 50%,
    rgba(0,0,0,0.157) 0%,    /* #28 = 15.7% */
    rgba(0,0,0,0.122) 18%,
    rgba(0,0,0,0.086) 40%,
    rgba(0,0,0,0.051) 62%,
    rgba(0,0,0,0.020) 82%,
    rgba(0,0,0,0)     100%
  );
  transition: opacity 300ms cubic-bezier(0.33, 0, 0.67, 1);
}
.sc-pointer-glow[data-active='true'] { opacity: 1; }
```

**边缘光（液态玻璃缘）**：`1px` 内描边径向渐变，`BorderThickness="1"`，
渐变档位 `#4DFFFFFF`(30%) → `#1FFFFFFF`(12%) → `#00FFFFFF`(0%)，Offset 0 / .42 / .82，
`RadiusX/Y = 0.85`，悬停最高 **0.5** 不透明度（`PointerLightBehavior` 注释；
常量 `EdgeMaxOpacity = 0.62`、`EdgeBaseOpacity = 0.20`）。**浅色主题下这套白色边缘光不生效观感**，
Electron 端建议按主题跳过。

**光斑半径**：`HoverShadowBehavior.GlowRadius` 默认 0 = 按载体对角线自动。
显式指定的场景：标题栏/顶栏按钮 `16`、分段条 `30`、窗口按钮 `40`（半径必须 > 方形半对角线 28.3，
否则渐变在四个角之前就淡到 0，视觉上只剩一个内切圆）。

---

## 3. 间距与尺寸

### 3.1 页面骨架

| 项 | 值 | 来源 |
| --- | --- | --- |
| 标题栏高 | **48** | `MainWindow.xaml` `RowDefinition Height="48"` |
| 内容区内边距 | **24（四边统一）** | `<Grid Padding="24">` 包裹 `ContentFrame` |
| 版心最大宽 | **1280** | 各页根容器 `MaxWidth="1280"` |
| 窗口最小尺寸 | **900 × 620** | `OverlappedPresenter.PreferredMinimumWidth/Height` |
| 左栏距右栏 | **24** | 首页左列 `Margin="0,0,24,0"` |
| 设置页两栏 | 左 **180** + 间距 **16** | `SettingsPage.xaml` |

> 根 `DESIGN.md` §3 写的 `Padding="32,12,24,24"` 已过时，代码为四边统一 24。

### 3.2 卡片内边距

| 卡片 | 内边距 | 圆角 |
| --- | --- | --- |
| `ScCard`（静态卡） | **16** | 16 |
| `ScHoverCard`（可悬浮卡） | **16** | 12 |
| 首页 `VersionCard` / `AccountCard` | **12** | 16 |
| 启动自检卡 `CheckCard` | **16** | 8 |
| Nova 确认清单卡 | **16,12** | 12 |
| Nova 面板 `NovaPanelHost` | **16** | 20 |
| 「你知道吗」提示卡 | **12,10** | 8 |
| 下载对话框资源卡 | **12** | 12 |
| 设置左分类卡 | **8** | 8 |
| 调度中心抽屉内容 | `Margin="20,16,14,12"` + `RowSpacing="10"` | — |

### 3.3 控件高度

| 控件 | 尺寸 | 圆角 | 来源 |
| --- | --- | --- | --- |
| **启动大按钮**（主 CTA） | **高 60**，通栏 | **9** | `HomePage` `LaunchBtn` |
| 折叠/展开按钮 | **32 × 60** | 9 | `HomePage` `CollapseBtn` |
| 分段控件外框 | 高 40（1 边框 + 2 内缩 + 34 段 + 2 + 1） | 10 | `HomePage` `ModeBar` |
| 分段控件内段 | 高 **34** | 7 | `HomePage` 三个 `ToggleButton` |
| 分段指示器 | 高 **34** | 7 | `ModeIndicator` |
| 下载页分段外框 | `Padding="3"`，段 88 × **30** | 外 11 / 段 8 / 指示器 8 | `DownloadPage` |
| **输入框** | 高 **32**（下载页搜索框显式） | 8 + 1px 描边 | 全局 `TextBox` 样式 + `DownloadPage` |
| 中等按钮 / 次级按钮 | 高 **30** | 8 | `AccountsPage` `ScLogout` 等 |
| 图标按钮（透明底） | `Padding="6"`，最小 28 × 28 | 8 | 自检卡重检按钮 |
| 版本行（首页） | 行 `MinHeight="60"`，内容 `Padding="10,8"`，`Margin="0,1"` | 8 | `HomePage` `ItemContainerStyle` |
| 版本行（版本页） | 行 `MinHeight="64"`，`Padding="12,8"` | 8 | `VersionsPage` |
| 左分类导航项 | 行 `MinHeight="40"`，控件 `Margin="0,2"`，内容 `Padding="8,0"`，图标-文字 `Spacing="10"` | 10 | `SettingsPage` + `App.xaml` 模板 |
| 顶栏入口按钮 | **44 × 36**（外层），内层图形 **32 × 32** | 8 | `MainWindow.xaml` |
| 头像按钮 | 44 × 36，内层 `Ellipse` 32 直径 | 圆形 | `AccountButton` |
| **窗口按钮** | **46 × 48**（点击热区），点亮区 34 × 36（`TriggerInset=6`），光斑载体 40 × 40 | 8 | `CaptionButtonStyle` |
| 头像方块（首页） | **72 × 72** | 12 | `HomePage` |
| 头像方块（顶栏/Nova 面板） | **32 × 32** | 8 | `MainWindow.xaml` |
| 版本方块图标 | **32 × 32** | 8 | 全仓行模板 |
| 账户头像（账户页） | **40 × 40** | 圆形 20 / 方形 8 | `AccountsPage` |
| 下载页最新版本图标 | **36 × 36** | — | `DownloadPage` |
| 任务徽标 | `MinWidth=15, Height=15`，`Padding="3,0"` | 7 | `MainWindow.xaml` |
| 进度条 | 高 **4** | 2 | `HomePage` `LaunchProgress` / 任务卡 |
| 运行状态圆点 | **6 × 6** | — | 导航项 `RunningDot` |
| 提案角标圆点 | **8 × 8** | — | `NovaOrbDot` |
| 设备横幅圆点 | **8 × 8** | — | `DeviceBannerDot` |

**最小点击目标**：窗口按钮 46 × 48 是全局最小；图标按钮最小 28 × 28（自检卡重检）；
行目标 ≥ 40（分类导航）至 60（版本行）。**规范下限 28 × 28 DIP**。

### 3.4 间距刻度（代码实测）

`4 / 6 / 8 / 10 / 12 / 14 / 16 / 20 / 24 / 32 / 340 / 432(600) / 720`。

| 值 | 典型用途 |
| --- | --- |
| 2 | `ScChip` 垂直内边距、行 `Margin="0,1~0,2"`、分类导航项 `Margin="0,2"` |
| 4 | 脊柱宽、提示卡 `Padding="12,10"`、抽屉头 `Margin="2,2"` |
| 6 | 芯片水平内边距 `Padding="6,1"`、图标按钮 `Padding="6"`、chevron 路径内缩 |
| 8 | **主间距单位**——卡片间、Segment 间距、卡头 `Margin="0,0,0,8"`、`Spacing="8"` |
| 9 | 抽屉标题行图标-文字 `ColumnSpacing="9"`、`LineHeight="18"` |
| 10 | 分类导航图标-文字、分段条 `GlowRadius=30`、`RowSpacing="10"` |
| 12 | 卡片紧凑内边距、左右分栏间距（设置页） |
| 14 | 状态行 `Margin="0,14,0,0"` |
| 16 | 主卡内边距、`Grid ColumnSpacing="16"` |
| 20 | 抽屉内边距、toast host `right/bottom: 20px` |
| 24 | 页面内边距、两列间距 |
| 32 | 头像首字母行 `Padding="32,0,0,0"`（下载搜索框左内缩） |

**注意**：列表行的行间距走元素级 `Margin`（`0,1` / `0,2`），**不用 `RowSpacing`**——
`RowSpacing` 按行边界收取，某行收起成 `Collapsed`、行高为 0 时它照收一份间距，
会把后面的内容推下去一截。Electron 端等价做法：条件渲染时连 `margin` 一起去掉，不要留空占位。

---

## 4. 排版

### 4.1 字体

| 语义 | 代码键 | 值 |
| --- | --- | --- |
| UI 字体 | `ScUiFontFamily` / `ContentControlThemeFontFamily` | **HarmonyOS Sans SC** |
| 图标字体 | `ScIconFont` | `ms-appx:///Assets/Fonts/HarmonyOSIcons.ttf#harmonyos` |
| 等宽（日志/代码） | — | `Consolas`（`LaunchLogText`）、`Cascadia Code`（根 DESIGN.md §5） |

**字重铁律**：HarmonyOS Sans SC 只装了 Medium / Bold / Black 三档，没有 Regular。
正文与标题**一律 Medium（500）**；只有靠字重表达状态的切换控件在选中时提到 Bold。
全仓统计：`FontWeight="Medium"` 122 处，`SemiBold` 4 处（离群，已在收敛计划里），`Bold` 2 处。

### 4.2 字号层级（按全仓实测频次反推）

| 层级 | 字号 | 字重 | 色 | 频次 | 使用场景 |
| --- | --- | --- | --- | --- | --- |
| Micro | **9** | Medium | accent 面文字 | 4 | 仅任务徽标数字 |
| Mini | **10** | — | — | 1 | 极少量 |
| **Caption / Meta Label** | **11** | Medium | `TextFillColorTertiaryBrush` | **61** | 行尾标签、`ScMetaLabel`、`ScChipText`、`ScCardHint`、时间戳、状态行、调试日志 |
| **Caption / Meta Value** | **12** | Medium | `TextFillColorSecondaryBrush` | **139（最高频）** | `ScMetaValue`、副标题、空态副标题、说明文字、状态文案、按钮内文 |
| **Body Small** | **13** | Medium | primary / secondary | **43** | 行主标题、设置项标签（`PclLabel`）、分段控件内文、自检行文案 |
| **Body / Base** | **14** | Medium | primary | **57** | **`ScText` 基样式字号**、小节标题 `ScSectionTitle`、分类导航项、卡片标题、列表项标题 |
| **Subtitle** | **15** | Medium | primary | 7 | 卡片头标题（「已安装版本」「启动自检」） |
| **Subtitle Strong** | **16** | Medium | 跟随按钮 Foreground | 7 | 启动按钮主标题 |
| **Title** | **17** | Medium | primary | 6 | 抽屉标题（「调度中心」）、账户名 |
| Body+ | 18 | — | — | 4 | 次要 |
| Icon / 空态 | 20 / 22 | — | tertiary | 5 | 空态图标 28、抽屉队列空态图标 22 |
| **Display L** | **24** | Medium | primary | 3 | 启动页阶段标题、插件面板标题 |
| Display XL | 28 / 30 / 32 | Medium | — | 6 | 头像首字母 28、页面主标题 30、投放对话框大数字 32 |

```css
:root {
  --sc-fs-9:  9px;   --sc-fs-11: 11px;  --sc-fs-12: 12px;
  --sc-fs-13: 13px;  --sc-fs-14: 14px;  --sc-fs-15: 15px;
  --sc-fs-16: 16px;  --sc-fs-17: 17px;  --sc-fs-24: 24px;
  --sc-fw:     500;  /* Medium 是全站默认字重 */
}
body { font-family: 'HarmonyOS Sans SC', 'Segoe UI Variable', system-ui, sans-serif; font-weight: 500; }
```

**语义色配对**：`ScText` 基样式**不设 Foreground**——强调色按钮里的黑字是靠
`ContentPresenter` 从按钮继承下来的。一旦在基样式写死前景色，按钮里文字变黑而图标还是白。
Electron 端注意：不要在 body/全局 p 标签上设 `color`，让 accent 组件的文字继承自己的前景色。

### 4.3 行高 / 截断约定

| 约定 | 值 |
| --- | --- |
| 「你知道吗」正文行高 | `LineHeight="18"` @ 12px |
| 时间戳 | 11px + `Opacity="0.65"` |
| 工具调用状态行 | 12px + `Opacity="0.75"` |
| 启动按钮副标题 | 12px + `Opacity="0.72"` |
| 长文本 | `TextTrimming="CharacterEllipsis"`（省略号）；说明文字用 `TextWrapping="Wrap"` |
| 启动错误文案 | `MaxLines="2"` |

---

## 5. 组件规范

### 5.1 按钮

| 变体 | 对应 WinUI 资源 | 背景 | 文字 | 圆角 | 最小高 |
| --- | --- | --- | --- | --- | --- |
| **Primary（强调）** | `ScAccentButtonStyle`（覆写 `AccentButtonStyle`） | `#FFD322` → hover `#FFBC00` → press `#E6A800` | `#E61A1A1A` | 9（主 CTA）/ 12（常规） | 32（常规）/ 60（主 CTA） |
| **Secondary（默认）** | 隐式 `Button` → `ScLightButtonStyle` | 框架 `ButtonBackground*` 四态 + 跟手光斑 | `TextFillColorPrimaryBrush` | 8–12 | 30–32 |
| **Subtle / 图标按钮** | 调用点写 `Background="Transparent" BorderThickness="0"` | 透明 | primary 或 secondary | 8 | 28 |
| **危险** | 无专用样式；用 `DangerBrush` `#F44336` 作前景，删除类操作必须经 `ContentDialog` 二次确认 | — | `#F44336` | — | — |
| **Card（卡片式）** | `ScGlowCardButtonStyle` / `ScGlowCardButtonTemplate` | `GlassCardBrush`，悬停**不换色**（否则从深卡突变成浅卡），改由 `HoverLayer` 铺 `SubtleFillColorSecondaryBrush` 到 1 | — | 10 | — |
| **Static（静态瓦片）** | `ScStaticCardButtonStyle` | 同上 | — | — | **零悬停反馈**（不换色、不光斑、不上浮）——本身已是张卡，再叠光斑会被染暗并抬起，比完全不动更糟 |

主 CTA（`LaunchBtn`）结构：竖排两行，上行「播放/停止轮廓 + 启动游戏」16px Medium，
下行当前版本名 12px `Opacity=0.72`；两行的 Foreground 都 `{Binding Foreground, ElementName=LaunchBtn}`。

Fluent v9 落地：

```ts
<Button appearance="primary" className={useStyles.cta}>启动游戏</Button>

const useStyles = makeStyles({
  cta: {
    height: '60px',
    minWidth: '128px',
    borderRadius: '9px',
    fontSize: '16px',
    fontWeight: 500,
    // 覆写 v9 的 primary 色（v9 默认跟随品牌 ramp 的 brand80）
    color: 'var(--colorNeutralForegroundOnBrand)',
    backgroundColor: 'var(--sc-accent)',
    ':hover':     { backgroundColor: 'var(--sc-accent-hover)' },
    ':active':    { backgroundColor: 'var(--sc-accent-pressed)' },
    ':disabled':  { backgroundColor: 'var(--sc-accent-disabled)', color: '#5C1A1A1A' },
  },
  subtle: {                                        // 透明底图标按钮
    minWidth: '28px', minHeight: '28px',
    padding: '6px', borderRadius: '8px',
    backgroundColor: 'transparent', borderWidth: '0',
    ':hover': { backgroundColor: 'transparent' },   // 悬停反馈交给跟手光斑，不换底色
  },
})
```

**每个按钮都要挂跟手光斑**（`HoverShadowBehavior.IsEnabled=True`，Electron 端即 §2.3 的 `.sc-pointer-glow`）。
`GlowButton` 的光斑圆角用 `{TemplateBinding CornerRadius}` 与按钮表面读同一个值——
Electron 端请写 `border-radius: inherit`，否则会出现「光斑比按钮更方、四个角溢出一圈」。

按钮模板内的 `BrushTransition Duration="0:0:0.083"`（83ms）只作用于原生悬停/按下换色。

### 5.2 输入框

全局隐式 `TextBox` 样式（`App.xaml`）：

| 属性 | 值 | 理由（代码注释） |
| --- | --- | --- |
| `BorderThickness` | **1** | WinUI 默认只有一条底边线，落在白卡片上几乎看不出边界 |
| `CornerRadius` | **8** | 与按钮/卡片共用同一套边界语言 |
| `FontFamily` / `FontWeight` | HarmonyOS Sans SC / Medium | 与全站一致 |

聚焦态仍由框架的强调色底边表达（未覆写）。
下载页搜索框额外：`Height="32"`、`Padding="32,5,10,5"`、`VerticalContentAlignment="Center"`、
前置 14px 放大镜图标（`Margin="12,0,0,0"`，secondary 色，不可命中）。

```ts
input: {
  borderRadius: '8px',
  borderWidth: '1px',
  borderStyle: 'solid',
  borderColor: 'var(--colorNeutralStroke2)',
  height: '32px',
  padding: '5px 10px',
  ':focus-visible': { borderBottomColor: 'var(--sc-accent)', borderBottomWidth: '2px' },
}
```

其它带文字控件（`ComboBox` / `ToggleSwitch` / `CheckBox` / `RadioButton`）在 `App.xaml`
统一提到 Medium 字重，高度代码中未统一指定（沿用框架默认）。

### 5.3 列表项

**关键约定：框架 `ListViewItem` 的矩形底色一律关掉**，悬停/选中反馈全部由自绘模板表达
——框架给的是硬边直角矩形（默认圆角 4），压上圆角光斑后四个角会互相啃掉一块。

需在每个列表的 `ListView.Resources` 中置空（Electron 端对应：不要给 `<li>` 写原生选中底）：

```
ListViewItemBackground / PointerOver / Pressed / Selected
                       / SelectedPointerOver / SelectedPressed  →  全部 Transparent
```

| 项 | 值 | 来源 |
| --- | --- | --- |
| 行圆角 | `ListViewItemCornerRadius` 覆写为 **8** | `HomePage` 列表资源（框架默认 4，会从角上漏光） |
| 行的水平/垂直对齐 | `Stretch` / `Stretch` | 垂直必须 Stretch：默认 Center 会把 48 DIP 内容在 60 DIP 行内居中，上下各留 6 DIP 死区，光标掠过交界处光斑不出现 |
| 首页行 | `MinHeight="60"`、`Padding="0"`、`Margin="0,1"` | |
| 版本页行 | `MinHeight="64"`、`Padding="0"` | |
| 分类导航行 | `MinHeight="40"`、`Padding="0"`、`Margin="0"` | |

**行内部结构（五段式）**——这是跨页面复用的「结构语言」之一：

```
[32×32 方块图标] ← 12 → [名称 固定列宽 260] → [元信息芯片 × N, Spacing 6] → …… → [行尾数据列]
                                                                                     ↑ 标签 11 tertiary / 数值 12 secondary，右对齐
```

行内 `Padding="10,8"`、`ColumnSpacing="12"`。行数据列两行两列：
标签列右对齐 + 数值列固定 64 右对齐，于是宽窗口下跨行对齐成列，而不是「左边一坨、右边一坨」。

**行的光斑竖向内缩 4**（`Margin="0,4"`）：光斑不贴到行的上下边缘，相邻两行之间留出可见留白，
不会连成一片暗带。

**悬停/选中态的三层语言**：

| 态 | 表现 |
| --- | --- |
| Normal | 无底色 |
| Hover | 跟手径向光斑淡入（300ms）+ 上浮 2px（`RowHoverBehavior.HoverLift = -2`） |
| Press | 下压 1px（`PressedDrop = 1`），100ms |
| Selected | 与 hover 同一层压暗底 `ScRowShadeBrush`（浅色黑 4% / 深色黑 22%） |

```css
.sc-row {
  position: relative;
  min-height: 60px;
  padding: 8px 10px;
  border-radius: 8px;
  transition: transform 300ms cubic-bezier(0.33, 0, 0.67, 1);
}
.sc-row:hover { transform: translateY(-2px); }
.sc-row:active { transform: translateY(1px); transition-duration: 100ms; }
.sc-row[data-selected='true']::before {
  content: ''; position: absolute; inset: 0; border-radius: inherit;
  background: rgba(0, 0, 0, 0.04);   /* Dark: rgba(0,0,0,0.22) */
}
```

> 判定「指针是否邻着某张卡片」用**外扩 8px** 的范围（`NearMargin = 8`），
> 避免光标慢速划过行间 2px 缝隙时先灭再亮、看着像闪断。

### 5.4 卡片

| 变体 | 资源 | 背景 | 描边 | 圆角 | 内边距 | 悬停 |
| --- | --- | --- | --- | --- | --- | --- |
| 静态卡 | `ScCard` | `GlassCardBrush` | `CardStrokeColorDefaultBrush` 1px | 16 | 16 | 无（不是可点目标） |
| 可悬浮卡 | `ScHoverCard` | `GlassCardBrush` | 同上 1px | **12** | 16 | `Scale 1.012` / 按下 `0.985` |
| 首页主卡 | 内联 | `GlassCardBrush` | 1px | 16 | 12 | 无（由内部行/按钮表达） |
| Nova 确认清单卡 | 内联 | `GlassCardBrush` | 1px | 12 | 16,12 | 无 |

`CardHoverBehavior` 参数：`HoverScale = 1.012`、`PressedScale = 0.985`、
时长 = `HoverFadeInMs` = 300ms、按压 = **100ms**（按下去必须立刻有反应）。

```css
.sc-hover-card {
  background: var(--sc-surface);
  border: 1px solid var(--sc-stroke);
  border-radius: 12px;
  padding: 16px;
  transition: transform 300ms cubic-bezier(0.33, 0, 0.67, 1);
}
.sc-hover-card:hover  { transform: scale(1.012); }
.sc-hover-card:active { transform: scale(0.985); transition-duration: 100ms; }
```

### 5.5 标签 / 芯片 / 徽章

**全站唯一的"结构化标签"形态**（不再各自用彩色文字或描边小框）。全仓使用 71 处。

| 变体 | 资源 | 背景 | 文字 | 内边距 | 圆角 |
| --- | --- | --- | --- | --- | --- |
| 默认芯片 | `ScChip` / `ScChipText` | `SubtleFillColorTertiaryBrush` | `TextFillColorSecondaryBrush` 11px | **6,1** | **4** |
| 强调芯片 | `ScChipAccent` / `ScChipAccentText` | `AccentFillColorDefaultBrush` | `TextOnAccentFillColorPrimaryBrush`（黑） | 6,1 | 4 |
| 提示芯片 | `ScChipCaution` / `ScChipCautionText` | `CautionChipBackgroundBrush` | `CautionChipTextBrush` | 6,1 | 4 |

其它徽标：

| 徽标 | 尺寸 / 内边距 | 圆角 | 背景 | 文字 |
| --- | --- | --- | --- | --- |
| 版本数徽标 | `Padding="6,1"` | **5** | `SubtleFillColorSecondaryBrush` | 11px secondary |
| 标题栏版本号 | `Padding="6,2"` | 6 | `ControlFillColorSecondaryBrush` | 11px secondary |
| 任务徽标 | `MinWidth 15 × Height 15`，`Padding="3,0"` | 7 | `AccentFillColorDefaultBrush` | **9px** Medium + 黑字 |
| 「缓存」徽标 | `Padding="8,3"` | 6 | `AccentFillColorTertiaryBrush` | 11px + 黑字 |

```css
.sc-chip {
  display: inline-flex; align-items: center;
  padding: 1px 6px; border-radius: 4px;
  background: var(--colorNeutralBackgroundL2);   /* = SubtleFillColorTertiary */
  color: var(--sc-text-2); font-size: 11px; font-weight: 500;
  white-space: nowrap;
}
.sc-chip--accent { background: var(--sc-accent); color: #E61A1A1A; }
.sc-chip--caution { background: rgba(245,158,11,0.12); color: #B45309; }
/* Dark: rgba(245,158,11,0.18) / #FBBF24 */
```

### 5.6 小节标题（脊柱 + 中性文字）

三样结构语言之一，全仓使用 53 处。早期版本把整行标题染成品牌色，
一屏十来个标题都在喊、层级被拉平，强调色收回给脊柱后「这组从这里开始」依然一眼可见。

```xml
<!-- 脊柱：3 × 14，圆角 2，强调色 -->
<!-- 标题：14px Medium，TextFillColorPrimaryBrush -->
<StackPanel Orientation="Horizontal" Spacing="8">
  <span className={st.spine} />
  <span>游戏</span>
</StackPanel>
```

```css
.sc-section-head { display: flex; align-items: center; gap: 8px; }
.sc-spine { width: 3px; height: 14px; border-radius: 2px; background: var(--sc-accent); }
.sc-section-title { font-size: 14px; font-weight: 500; color: var(--sc-text-1); }
```

### 5.7 Toast / InfoBar

`InfoBar` 是全站唯一的就地提示形态（错误 / 警告 / 成功），Floating 的
`TeachingTip` 用于「一句话提醒」（`NovaToastTip`，`PreferredPlacement="Bottom"`）。
插件通知是右下角 `InfoBar`，`Margin="0,0,24,24"`、`MaxWidth="420"`、`Severity="Informational"`。

| Severity | 用途 | 实测落点 |
| --- | --- | --- |
| `Error` | 启动出错 / 操作失败 | 全站 6 处，`IsClosable="True"`，标题「启动出错」「操作失败」 |
| `Warning` | 提示 | 首页 `WarnBar` 1 处 |
| `Success` | 完成 | 5 处，标题「完成」「已完成」 |
| `Informational` | 进行中（带 ActionButton） | 存档页 `RunBar`、`IsClosable="False"` |

同类资源的其它提示：设备上下线横幅（右上角滑入、3 秒后滑出、`Margin="0,56,16,0"`、
`Padding="14,10"`、圆角 12）、设备横幅状态点 8px success 色。

Fluent v9 落地（`MessageBar`，`ui.tsx` 已有封装）：

```ts
<MessageBar intent="error"     title="操作失败" />   // = InfoBar Severity="Error"
<MessageBar intent="warning"   title="提示" />
<MessageBar intent="success"   title="完成" />
<MessageBar intent="info"      title="…" />
```

> `InfoBar` 的横幅条宽度、圆角与边框代码中未覆写，跟随框架默认。

### 5.8 空状态

`EmptyStateView` 是全站唯一的空态组件（6 处替换：首页版本列表 / 版本页 / 存档页 /
账户页 / Nova 页 / 下载搜索结果），竖排 `Spacing="6"`：

```
[图标 28px, TextFillColorTertiaryBrush]      ← 品牌标记类图标走 IconImage（28×28 Image）
[标题 14px Medium, primary, 居中, Wrap]
[副标题 12px, secondary, 居中, Wrap]
[操作按钮（可选）AccentButtonStyle, Margin="0,4,0,0", 居中]
```

```css
.sc-empty {
  display: flex; flex-direction: column; align-items: center;
  gap: 6px; min-height: 200px; justify-content: center;
}
.sc-empty__icon    { font-size: 28px; color: var(--sc-text-3); }
.sc-empty__title   { font-size: 14px; font-weight: 500; color: var(--sc-text-1); text-align: center; }
.sc-empty__subtitle{ font-size: 12px; color: var(--sc-text-2); text-align: center; }
.sc-empty__action  { margin-top: 4px; }
```

**极简空态**（抽屉里空间不足时，调度中心队列用）：图标 22 tertiary + 「无」13 secondary，
居中 `Margin="0,30,0,26"`。**不做骨架屏**（设计稿 §2 明确排除）。

### 5.9 分段控件（工具条）

两处同款，是「卡片整宽 + 品牌色药丸 + 黑字 + 指示器平移」的统一语汇。

| 参数 | 首页账户模式 | 下载页版本类型 |
| --- | --- | --- |
| 外框 | `ControlFillColorSecondaryBrush` + `CardStrokeColorDefaultBrush` 1px，圆角 **10**，`Padding="2"`（内缩 2） | 同款，圆角 **11**，`Padding="3"`（内缩 3） |
| 段 | 高 **34**，圆角 7，`FontSize 13`，`Foreground = secondary` | 88 × **30**，圆角 8，`FontSize 13` |
| 指示器 | `AccentFillColorDefaultBrush` 药丸，高 34，圆角 7，`Grid.ColumnSpan="3"` 横跨全列防裁剪 | 88 × 30，圆角 8 |
| 选中文字 | `#E61A1A1A`（黑） | 同 |
| 悬停/按下底刷 | 全部置空（反馈交给条内跟手光斑 `GlowRadius="30"`） | 同 |
| 指示器时长 | `MotionFx.Scale(260)` = **390ms** / `ControlEase` | 同 |

```css
.sc-segmented {
  display: inline-flex; padding: 2px; gap: 0;
  background: var(--colorNeutralBackgroundL1);   /* ControlFillColorSecondaryBrush */
  border: 1px solid var(--sc-stroke);
  border-radius: 10px;
  position: relative;
}
.sc-segmented__seg {
  position: relative; z-index: 1;
  height: 34px; padding: 0; border: 0; background: transparent;
  border-radius: 7px; font-size: 13px; font-weight: 500;
  color: var(--sc-text-2);
}
.sc-segmented__indicator {
  position: absolute; z-index: 0; top: 2px; left: 2px;
  height: 34px; border-radius: 7px; background: var(--sc-accent);
  transition: transform 390ms cubic-bezier(0.16, 1, 0.3, 1), width 390ms cubic-bezier(0.16, 1, 0.3, 1);
}
.sc-segmented__seg[aria-selected='true'] { color: #E61A1A1A; }
```

### 5.10 对话气泡（Nova）

| 项 | 值 |
| --- | --- |
| 气泡底 / 圆角 / 内边距 | 由数据绑定（`Bubble` / `Corners`），`Padding="12,9"` |
| 消息容器 | `MaxWidth="720"`，`Margin="0,2"`，左右对齐由 `Align` 决定 |
| 气泡内 `Spacing="2"` |
| 时间戳 | 11px `Opacity="0.65"` |
| 打字三点 | `Ellipse 7×7`，`Opacity="0.3"`，`Spacing="5"`，`Margin="2,2,2,1"` |
| 工具状态行 | 气泡外的一行小字，`Spacing="6"`，`ProgressRing 13×13` + 12px `Opacity 0.75` |
| 列表转场 | `AddDeleteThemeTransition` + `RepositionThemeTransition`（新气泡淡入并让下方内容平滑让位） |
| 对话卡外壳 | `ScCard`（圆角 16 / padding 16），内层 `ListView Padding="4"` |

> 气泡的确切底色/圆角由 `NovaChatMessage.Bubble` / `Corners` 在代码后端计算，**XAML 中未定义具体色值**。

---

## 6. 动效

### 6.1 全局节奏系数

`MotionFx.Pace = 1.5`——**项目刻意比 Fluent 标准档再从容一档**（时长整体 ×1.5），
只放大时长、不改曲线形状。所有零散时长必须走 `MotionFx.Scale()`，不写魔数。

### 6.2 缓动曲线（`MotionFx`）

| 名称 | CSS | 用途 |
| --- | --- | --- |
| **进场 Fast Out, Slow In** | `cubic-bezier(0.1, 0.9, 0.2, 1)` | 内容进入、淡入上移、抽屉展开 |
| **退场 Slow Out, Fast In** | `cubic-bezier(0.7, 0, 1, 0.5)` | 内容退场 |
| **Control Fast Out Slow In** | `cubic-bezier(0.16, 1, 0.3, 1)` | 小控件 / 指示器位移（要比表面更快收尾） |
| **Fade（匀称缓入缓出）** | `cubic-bezier(0.33, 0, 0.67, 1)` | **纯明暗渐变**——悬停阴影、高亮、光斑 |

> 为什么不把进/退场曲线用在阴影上：Fluent 的 Enter/Exit 样条是「起步极快」型
> （30% 时间走完 87%），套在纯明暗渐变上会退化成「啪一下没了」——这正是悬停阴影显得生硬的原因。

**明确不使用弹簧回弹**（Fluent 明确避免回荡）。

### 6.3 时长表（`MotionFx` 常量 × Pace 1.5 后的**实际值**）

| 场景 | 常量 | 代码基准 | **实际** | 曲线 |
| --- | --- | --- | --- | --- |
| 内容进入（列表项、面板切换） | `EnterMs` | 250ms | **375ms** | 进场 |
| 内容退场 | `ExitMs` | 140ms | **210ms** | 退场 |
| **悬停淡入** | `HoverFadeInMs` | 200ms | **300ms** | Fade |
| **悬停淡出** | `HoverFadeOutMs` | 300ms | **450ms** | Fade |
| 按压反馈 | 硬编码 | 100ms | **100ms** | Fade |
| 框架 PointerOver 参照 | `PointerOverMs` | 167ms | **251ms** | — |
| 抽屉展开 | `DrawerExpandMs` | 280ms | **420ms** | 进场 |
| 抽屉收起 | `DrawerCollapseMs` | 200ms | **300ms** | 进场 |
| 抽屉内容淡出 | `DrawerFadeMs` | 160ms | **240ms** | 进场 |
| chevron 旋转 | `ChevronMs` | 220ms | **330ms** | Control |
| 列表错峰步进 | `StaggerIn stepMs` | 26ms | **39ms** × 最多 9 级 | 进场 |
| 分段指示器平移 | `Scale(260)` | 260ms | **390ms** | Control |
| 模板 `BrushTransition` | 内联 | 83ms | **83ms**（不受 Pace 影响） | — |

> `DrawerFadeMs` **必须短于** `DrawerCollapseMs`：淡出若晚于高度归零，内容会在还留着残影时被整体移除，
> 看上去就是「收起卡一下」。

### 6.4 动效等级（四档，`MotionService`）

| 档 | 键 | 内容 | 门控属性 |
| --- | --- | --- | --- |
| **极少** Minimal | `minimal` | 只有最基本的页面切换转场 | `Transitions` |
| **较少** Lite | `lite` | 极少 + 跟手光斑 + 毛玻璃 + 悬浮/入场微反馈 | `Enabled`（`Level >= Lite`） |
| **均衡** Balanced | `full` 之外，默认 | 较少 + 全局鼠标光照 + 阴影跟手 + 边缘引力形变 | `LightFx`（`Level >= Balanced`） |
| **全部** Full | `full` | 均衡 + 弹簧形变 + 窗口环境光斑 | `FullFx`（`Level == Full`） |

额外总闸：系统 `UISettings.AnimationsEnabled == false` → 一切动效抑制（无障碍）。
Electron 端对应 `@media (prefers-reduced-motion: reduce)`。

### 6.5 页面切换（`MotionService`）

| 场景 | 实现 |
| --- | --- |
| 顶层切页 / 进首页 | `EntranceNavigationTransitionInfo` |
| 进版本详情（层级下钻） | `DrillInNavigationTransitionInfo` |
| 返回上一层 | `SlideNavigationTransitionInfo { Effect = FromLeft }`（从左侧滑入，与 drill-in 互补） |
| 关闭动效 | `SuppressNavigationTransitionInfo` |

Fluent v9 落地：`<div role="tabpanel">` + 每个页面根节点挂
`animation: enter 375ms cubic-bezier(0.1, 0.9, 0.2, 1)`；
下钻层级用 `DrillIn` 的「放大 + 淡入」，返回用 `FromLeft` 的横向滑动。

### 6.6 反馈动效汇总（来自 `Views/Behaviors/*.cs`）

| 行为 | 悬停 | 按下 | 时长 |
| --- | --- | --- | --- |
| `CardHoverBehavior` | `Scale 1.012` | `Scale 0.985` | 300ms / **100ms** |
| `RowHoverBehavior` | 上浮 **-2px** | 下压 **+1px** | 300ms 进 / 450ms 出 / 100ms 按 |
| `HoverShadowBehavior`（通用） | 上浮 **-1px** + 光斑淡入 | — | 300ms 进 / 450ms 出 |
| `PointerLightBehavior` 引力形变 | 按下 `Scale 0.982` | 同 | 按下 100ms，回弹 210ms |
| `ExpandBehavior`（抽屉） | 高度 `0 ↔ 自然高度` + 内容淡入上移 12px + 箭头旋转 | — | 420ms / 300ms / 240ms |

**悬停退场的两种情形**（`HoverShadowBehavior.Evaluate`，Electron 端必须照做）：

1. 光标从一张卡片移到**另一张卡片** → **450ms 交叉淡出**，两张卡片的暗晕交叠成连续过渡；
2. 光标移到**任何卡片之外**（工具栏 / 空白 / 窗口外）→ **立刻灭**（无接力对象，
   拖淡出尾巴只会看着像阴影卡住）。

### 6.7 全局鼠标光照（`PointerLightBehavior`，均衡档起）

| 常量 | 值 | 含义 |
| --- | --- | --- |
| `EdgeBaseOpacity` | 0.20 | 边缘光常亮底值（1px 玻璃缘平时隐约可见） |
| `EdgeMaxOpacity` | 0.62 | 边缘光最亮值（到六成、仍不压过原生悬停底色） |
| `EdgeNearDist` | 150px | 光标多近开始把边缘光推到最亮 |
| `ActiveDist` | 220px | 超出此距离不再逐帧更新 |
| `ShadowActiveDist` | 420px | 阴影只在此距离内跟随 |
| `ShadowMaxShift` | 2.2px（均衡）/ 3.0px（全部） | 阴影最大位移（立体感刻意压低） |
| `ShadowRestY` | 0.8px | 阴影静置位：光不在时影子永远略向下坠（有重力） |
| `ShadowGain` | 0.045 | 位移随距离的增益 |

Electron 端：这些是「均衡档起」的增强，**默认档（Lite）可以只做光斑 + 悬停底色**。

---

## 7. 布局骨架

### 7.1 主窗口三段式

```
┌──────────────────────────────────────────────────────────────┐
│ 标题栏 48px  AppPaneBackgroundBrush（不透明实色）              │  ← Row 0
│ [品牌标记 22] [Starcloud 14 Medium] [版本徽标]  ·····  [Orb 44×36]
│                                        [任务 44×36][头像 44×36]
│                                        [—][□][×]  各 46×48      │
├──────────────────────────────────────────────────────────────┤
│ NavigationView LeftCompact, IsPaneOpen=False,                 │  ← Row 1
│  ┌────┬─────────────────────────────────────────────────┐    │
│  │ 48 │  Grid Padding="24"                              │    │
│  │ 48 │    MaxWidth="1280" 居中铺满                       │    │
│  │ 条 │    Frame（内容区半透明，亚克力可透出）             │    │
│  │ 232│                                                 │    │
│  │ 展开│                                                │    │
│  └────┴─────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────┘
↑ 浮层：调度中心抽屉（600 宽）/ Nova 面板（440）/ InfoBar / 横幅
```

| 项 | 值 | 来源 |
| --- | --- | --- |
| 标题栏高 | 48 | `RowDefinition` |
| 标题栏底 | `AppPaneBackgroundBrush` **不透明实色**，不透视背景 | |
| 系统 caption | 隐藏，自绘三键（`ExtendsContentIntoTitleBar` + `SetTitleBar`） | |
| 窗口按钮 | 46 × 48，圆角 8，`Padding=0`，`BorderThickness=0`，透明底 | `CaptionButtonStyle` |
| 顶栏入口 | 三颗 44 × 36 透明按钮（NovaOrb / 任务中心 / 头像），`Margin="0,0,6~8,0"` | |
| 内层图形 | 32 × 32，`CornerRadius="8"`（头像是 `Ellipse` 32 直径） | |
| 导航模式 | `PaneDisplayMode="LeftCompact"`，`IsPaneOpen="False"`，`OpenPaneLength="232"` | |
| 无返回按钮 | `IsBackButtonVisible="Collapsed"` | |
| 无内嵌设置 | `IsSettingsVisible="False"`（设置是独立页，位于 Footer） | |
| 标题栏自动内缩 | `IsTitleBarAutoPaddingEnabled="False"`（顶栏自绘，不用框架让位） | |
| 内容区内边距 | 24（四边统一） | |
| **状态栏** | **已移除**——底部不再有「未选择实例」条 | `MainWindow.xaml` 注释 |

**导航分组**（顺序即信息架构）：

```
主区：首页 / 版本 / 下载 /存档 / 联机 / 实验室 / 投影管理 / 运行状态
      ── NavigationViewItemSeparator ──
      账户 / 插件 / Nova
Footer：启动器日志 / 设置（设置永远在最底）
```

导航项字形全部来自 `ScIconFont`（HarmonyOS 图标库），
**不用 `Icon="Home"` 这类 SymbolIcon**——那会走 Segoe 字形，与整条导航不同源。
品牌类图标（Nova）用 `ImageIcon` + `SvgImageSource`。

### 7.2 首页双栏（设计基准）

```
MaxWidth="1280"，两列与窗口同高（根容器不用 ScrollViewer，PCL2 式）
┌────────────────────────────────┬──────────────────────┐
│ 左列 *（Margin 右 24）          │ 右列 360 固定         │
│ ┌────────────────────────────┐ │ ┌──────────────────┐ │
│ │ 通知区（Collapsed 时不占位）│ │ │ 分段控件 40 高    │ │
│ │  · InfoBar ×3 (Spacing 8)  │ │ ├──────────────────┤ │
│ │  · 启动自检卡 (圆角 8)     │ │ │ 头像 72×72 圆角12 │ │
│ ├────────────────────────────┤ │ │ 账户名 17px      │ │
│ │ 插件挂载区 (home.top)      │ │ │ 切换账号按钮 32 高 │ │
│ ├────────────────────────────┤ ├──────────────────┤ │
│ │ 插件卡片带（120 高，≤3 张） │ │ │ 启动按钮 60 高通栏 │ │
│ ├────────────────────────────┤ │ │  + 折叠键 32×60  │ │
│ │ 版本卡（撑满余下高度）       │ │ ├──────────────────┤ │
│ │  卡头：标题15 + 数量徽标     │ │ │ 状态行 12px      │ │
│ │        + 收起 + 刷新         │ │ └──────────────────┘ │
│ │  列表（卡内滚动，MinHeight60）│ │                      │
│ └────────────────────────────┘ │                      │
└────────────────────────────────┴──────────────────────┘
```

**收起态（版本卡 ↔ 账户卡同时收成窄条 Dock）**：

| 项 | 值 |
| --- | --- |
| 窄条尺寸 | **280 宽 × 88 高**（2 边框 + 24 内边距 + 62 紧凑行） |
| 版本窄条 | 贴左列左沿，紧挨启动卡左侧（间距回到 24） |
| 账户窄条 | 贴右列右沿（与左窄条镜像） |
| 底边 | 两窄条底边都落在内容区底沿，与展开态页边距相等 |
| 投影环 | 只在收起态露面，四条同心描边（见 §2.3） |
| 过渡 | `DrawerCollapseMs 300ms` / `DrawerExpandMs 420ms`，逐帧补间七段尺寸（卡宽、底部留白、右移量） |

**三张卡左右边缘对齐成一条竖线**：分段控件、账户下拉、启动按钮都是「卡片整宽」。
底部那颗刻意**不跟顶部分段条等高**（60 vs 40）——底部是主 CTA。

### 7.3 设置页（左标签右控件）

```
MaxWidth 1280 → Grid ColumnSpacing="16"
┌──────────────────┬────────────────────────────────────┐
│ 左 180            │ 右 *                                 │
│ GlassCardBrush    │ 分组卡片（ScHoverCard / ScCard）      │
│ 圆角 8 Padding 8  │  小节头（脊柱 3 + 标题 14）           │
│ ListView 行 40 高 │  行式布局：左标签固定 86px + 右控件     │
│ CategoryNavItem   │  左边距 12（与右栏 RowSpacing 对齐）   │
└──────────────────┴────────────────────────────────────┘
```

### 7.4 浮层

| 浮层 | 尺寸 / 定位 | 底色 | 圆角 |
| --- | --- | --- | --- |
| 调度中心抽屉 | **宽 600**，右对齐，`BorderThickness="1,0,0,0"` | `AppPaneBackgroundBrush` + 遮罩 `#5A000000` | 0（左上右下由外层裁） |
| Nova 面板 | **宽 440**，顶栏 Orb 下方弹出 | `SolidBackgroundFillColorBaseBrush` | **20** |
| 设备横幅 | 右上角，`Margin="0,56,16,0"`，`Padding="14,10"` | `GlassCardBrush` | 12 |
| 插件通知 InfoBar | 右下角，`Margin="0,0,24,24"`，`MaxWidth="420"` | 框架默认 | 框架默认 |

> 根 `DESIGN.md` §10 写抽屉 432px，`MainWindow.xaml` 实际为 **600px**。以代码为准。

---

## 8. 迁移检查清单

落到 Electron + Fluent UI v9 时，优先确认这 10 条（本项目最容易被 v9 默认值覆盖掉的）：

1. 圆角整体比 v9 大一档（行 8 / 卡 16 / 悬浮卡 12），需显式覆写 `borderRadius`。
2. 强调色 **#FFD322 + 黑字**，覆盖 v9 的 `appearance="primary"` 默认色。
3. 强调色只出现在 4 处，其余保持中性。
4. 字重全站 **Medium (500)**，不是 v9 默认的 400。
5. TextBox **圆角 8 + 1px 描边**，不是 v9 默认的下划线式。
6. 列表项**关掉原生选中底**，反馈全部走自绘圆角层（否则四角漏光）。
7. 悬停淡出比淡入长（450 vs 300），且「移到卡片外」立刻灭。
8. 时间常数 ×1.5（进场 375ms / 退场 210ms），不是 v9 默认的 200ms。
9. 阴影不用 `box-shadow`，用同心半透明圆角矩形 + `opacity` 动画。
10. 行间距用元素级 `margin`，条件收起时连 margin 一起去掉，不用 `gap`（`RowSpacing` 按行边界收费）。

---

## 附：代码中未定义的项（勿臆造）

| 项 | 状态 |
| --- | --- |
| `CardStrokeColorDefaultBrush` 的 Dark 值 | 未在 `App.xaml` 定义，沿用框架深色默认 |
| `LayerFillColorDefaultBrush` / `SolidBackgroundFillColorBaseBrush` / `ControlFillColorSecondaryBrush` / `SubtleFillColorSecondaryBrush` / `SubtleFillColorTertiaryBrush` / `CardBackgroundFillColorSecondaryBrush` 的具体色值 | 全部未覆盖，跟随框架（Fluent v9 对应 `--colorNeutralBackground*` / `--colorNeutralBackgroundL2`） |
| `SystemFillColorAttentionBrush` / `SystemFillColorCautionBrush` / `SystemFillColorSuccessBackgroundBrush` | 未覆盖，跟随框架 |
| 独立的信息色（品牌蓝） | 代码中未定义；`BrandBlueBrush` 名字虽是 Blue，实际值 `#FFD322` |
| Nav 头像块圆角 `20`、下载对话框资源卡圆角 `10/12`、账户头像圆形 `20` | 散落硬编码，无令牌归属 |
| Nova 对话气泡的 `Bubble` / `Corners` 具体色值 | 由代码后端计算，XAML 未定义 |
| 页面级 `Scrollbar` 样式 | 未定义，跟随框架 |
| `MotionMaterials.cs` 的具体材质参数 | 未在本次范围内逐条读取 |
