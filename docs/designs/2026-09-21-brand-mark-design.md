# 品牌标记接入（启动器标识与深色强调色）

日期：2026-09-21
代码：`Starcloud.App/Views/Controls/BrandMark.xaml(.cs)`、`Starcloud.App/MainWindow.xaml`、
`Starcloud.App/App.xaml`、`Assets/Brand/`
设计源：`starcloud-brand/mark/export/starcloud-mark-light.svg`、`starcloud-mark-dark.svg`

## 1. 目标

- **启动器左上角**改用新的 Starcloud 品牌标记：直接渲染用户指定的两个主标记文件；
- Nova（侧栏图标 / 顶栏 Orb / 弹出面板头像 / Nova 页）保持原样，不动；
- **对外图标（exe / 任务栏 / 资源管理器 / 安装器）也换成品牌标记**；
- 深色主题里原先的绿强调色换成与浅色主题一致的品牌黄 `#FFD322`。

## 2. 标记怎么接

`Views/Controls/BrandMark.xaml` 就是一个 `Image`，源是品牌手册的两个主标记文件：

| 主题 | 文件 | 描边色 |
| --- | --- | --- |
| 浅色（默认 / 浅色） | `Assets/Brand/starcloud-mark-light.svg` | `#111418` |
| 深色 | `Assets/Brand/starcloud-mark-dark.svg` | `#E6EAF2` |

两个文件是同一套几何，只差描边色（黄面与星一致——星落在黄面上，任何底色都成立），
所以「换主题」在 code-behind 里就是换源：`ActualTheme == Dark ? dark : light`。
构造时 `ActualTheme` 还是 `Default`（跟随系统），所以真正生效的主题在 `Loaded` / `ActualThemeChanged`
时再套一次。

路径按 `AppContext.BaseDirectory` 解析（与 `AppIcon.ico`、壁纸同一套约定）：
相对路径会跟随快捷方式的「起始位置」，从开始菜单启动时可能落空。
`Assets/Brand/*.svg` 在 csproj 里登记为随输出目录发布的 Content，安装包一并带走。

尺寸给 22px：主标记的三格描边是 9/400 单位，16–24px 下已经偏细，再小就只剩黄块能读出来。

**渲染是实测过的**，不是推测：`BrandMark` 订阅了 `ImageOpened` / `ImageFailed`，
各写一行启动器日志。启动后 `%APPDATA%/starcloud-launcher/logs/launcher.log` 里出现：

```
[app] 品牌标记已渲染：starcloud-mark-light.svg
```

即 WinUI 的 `SvgImageSource` 确实解析并画出了这个文件（`<g>` 分组 + `transform` + `rect rx` + `path`
都在它支持的基础图元内）。若哪天品牌文件用了它不支持的特性，日志里会直接出现
「品牌标记渲染失败：…」，不用靠肉眼猜。

## 3. 深色主题：绿 → 黄

`App.xaml` 的 `Dark` 主题字典此前整套是绿（`#3DD322` 系），现改为与浅色同源的品牌黄：

| Key | 旧（深色） | 新（深色） |
| --- | --- | --- |
| `SystemAccentColor` | `#3DD322` | `#FFD322` |
| `SystemAccentColorLight1/2/3` | `#6BE050` / `#99E97F` / `#C6F2AD` | `#FFDD4D` / `#FFE780` / `#FFF0B3` |
| `SystemAccentColorDark1/2/3` | `#2FBD6E` / `#27A05C` / `#1F8349` | `#FFBC00` / `#D9A000` / `#B38500` |
| `AccentFillColorDefault/Secondary/Tertiary` | `#3DD322` / `#2FBD6E` / `#27A05C` | `#FFD322` / `#FFBC00` / `#E6A800` |
| `AccentTextFillColorPrimary` | `#6BE050` | `#FFD322` |
| `BrandBlueBrush` | `#3DD322` | `#FFD322` |
| `FocusVisualPrimaryBrush` | `#3DD322` | `#FFD322` |

`AccentFillColor*` 上的文字色 `TextOnAccentFillColorPrimaryBrush` 两个主题都已经是从深字
（`#E61A1A1A`），黄底黑字对比度与浅色主题一致，不需要再调。

## 4. 对外图标（exe / 任务栏 / 资源管理器 / 安装器）

`Assets/AppIcon.ico` 由 `tools/gen-app-icon.ps1` 从品牌标记生成，不再是原来的「石头方块」：

| 组成 | 取值 | 出处 |
| --- | --- | --- |
| 底板 | 圆角方块 `#101318`，圆角 104/512，白色 14% 细边 | 板色与圆角取自品牌 `starcloud-round-dark.svg` / `starcloud-icon.svg`；细边沿用品牌自己的做法，保证深色任务栏上仍有轮廓 |
| 标记 | `starcloud-mark-dark.svg`（浅线版） | 深底专用版，0.9 缩放与居中沿用品牌圆版参数 |
| 16 / 24 px | `starcloud-mark-small.svg`（单块版） | 品牌手册：该尺寸下主标记的三格描边不足一像素 |
| 32 px 起 | 主标记 | 品牌手册写明的切换点 |

每个尺寸都由 Edge 无头模式**按目标像素现画**（不是把一张大图缩小），
再按经典 32bpp DIB 打包进 ICO（16/24/32/48/64/128/256 共 7 档）。

**已实测**：从生成的 `Starcloud.App.exe` 里把图标抽出来看，是品牌标记而不是石头；
`ApplicationIcon`（exe 资源）与 `AppWindow.SetIcon`（窗口/任务栏）都指向同一个 ico，
`tools/build-installer.ps1` 的 `$icon` 也用它，所以安装包与快捷方式同步。

## 5. 明确不改的部分

- Nova 的所有标识位（侧栏 `NavigationViewItem` 图标、顶栏 `NovaOrb` 字形与五态着色、
  弹出面板头像位、Nova 页页头）保持原样，仍用 `&#xE99A;` 字形。
- 游戏内 Nova 浮窗（`Views/Overlays/NovaOverlayWindow.xaml`）同样不动。
