# 图标系统：改用 HarmonyOS 图标库

## 背景

界面图标此前直接用 Segoe Fluent Icons（MDL2）的私有码位，例如
`<FontIcon Glyph="&#xE896;" />`。这套字形和 HarmonyOS Sans 的中文排版混在一起时
笔画重量、圆角风格都不一致（字形端点偏方、线宽偏细）。本轮把整套应用图标换成
**HarmonyOS 图标库**，和 HarmonyOS Sans 保持同一套设计语言。

## 落地方式

| 项 | 位置 |
| --- | --- |
| 字体文件 | `dotnet/src/Starcloud.App/Assets/Fonts/HarmonyOSIcons.ttf`（家族名 `harmonyos`，随输出目录发布） |
| 字体资源 | `App.xaml` 的 `ScIconFont`（`ms-appx:///Assets/Fonts/HarmonyOSIcons.ttf#harmonyos`） |
| XAML 用法 | `<FontIcon FontFamily="{StaticResource ScIconFont}" Glyph="&#xXXXX;" />` |
| 代码用法 | `new FontIcon { FontFamily = IconFont.Family, … }`（`Services/IconFont.cs`） |
| 来源与许可 | `Assets/Fonts/HarmonyOSIcons-LICENSE.txt`（[IKKI2000/harmonyos-icons](https://github.com/IKKI2000/harmonyos-icons)，MIT，字形取自 HarmonyOS Design 图标库） |

字号与 `FontSize` 的用法完全不变：图标字体是同一 em 网格，`FontSize="14"` 的视觉
大小与原先的 Segoe 字形基本一致，所以各处的尺寸属性一律没动。

### 为什么不用隐式样式

看起来更省事的做法是给 `FontIcon` 挂一条隐式样式统一指定字体，但框架自己的模板
（ComboBox 下拉箭头、InfoBar 关闭、NavigationView 展开箭头、AutoSuggestBox 清除键…）
内部也是 `FontIcon`，隐式样式会把它们的 Segoe 码位丢给图标字体 → 全部变缺字方框。
所以字体是**逐元素**指定的：应用自己的 `FontIcon` 才挂 `ScIconFont`。

## 字形映射（按语义一对一）

全部候选字形都在 56px 放大图上逐个核对过（含"骨架填充"陷阱：图标库里 `files-*`
一族是实心风格，混进线框图标里会显得又黑又重，本轮只用线框字形）。

| 旧（Segoe） | 新（HarmonyOS） | 语义 |
| --- | --- | --- |
| E99A | 品牌标记 SVG | Nova（见下方「Nova = 品牌标记」） |
| E9F5 | EC89 | 调度中心（`ic_message_allread` 消息已读：一个"处理完了"的意象） |
| E77B | EB8F | 账户 / 人物 |
| E716 | EB91 | 联机 / 好友 |
| E81C | EC68（导航与版本列表用 EA94） | 版本 / 历史 / 备份 |
| E896 | E90C | 下载 |
| E8B7 | EA90 | 存档 / 文件夹 |
| E71D | EA93（插件相关位置用 EABC） | Mod / 内容 / 插件 |
| E7C3 | EC64 | 启动器日志 |
| E713 | EC04 | 设置 |
| E711 | EA86 | 关闭 |
| E8FD | EC70 | 空队列 |
| E718 | EC20 | 原版游戏 |
| E7B8 | EC62 | 版本行 / 内容行 |
| E8A5 | EC24 | 数据包 |
| E91B | EC49 | 资源包 |
| E706 | EC0C | 光影包 |
| E7FC | EC20 | 设置 · 游戏 |
| E721 | EA6F | 搜索 |
| E76C | E904 | 展开箭头 |
| E72B | E908 | 返回 |
| E7E8 | EC62 | 图标兜底 |
| E70F | EAA3 | 编辑 |
| E768 | EBDF | 启动 / 播放 |
| E80F | EC77 | 概览 / 首页 |
| E756 | EA6D | 启动参数 |
| E73E | EA7C | 验证 / 勾选 |
| E7BA | EBA6 | 自检未通过 |
| E8AC | EA97 | 重命名 |
| E8C9 / E8C8 | EA9F | 复制版本 / 复制名称 |
| E943 | EA6C | 查看 JSON |
| E74D | EAA5 | 删除 |
| E8A9 | EBA0 | 版本详情 |
| E838 | EA90 | 打开目录 |
| E8C1 | E90A | 导出整合包 |
| E712 | EAA7 | 更多 |
| E774 | E973 | 打开 Modrinth（外链） |
| E82D | EBA2 | 打开 MC 百科 |
| E72C | E918 | 刷新 |

### 站点级例外

同一字形在不同位置语义不同，两处单独处理：

* 版本（左下导航「版本」、版本页空状态、主页版本列表空状态）：`EA94`
  （`ic_public_view_list`）——"已安装版本"就是一个列表；备份与回档仍用 `EC68`（历史）。
* 插件（左下导航「插件」、插件页、插件面板入口）：`EABC`
  （`ic_gallery_photoedit_more`）。
* 调度中心：`EC89`（`ic_message_allread`）——原先是列表图标，与「版本」撞脸；
  先试过计时器（`EC3B`），但和「历史」的意象太近，最终定为"消息已读"：调度中心的产物就是一条条处理完的任务。
* 下载页「整合包」分类：`E7B8 → ECAF`（卡片），避免和同一列表里的版本行图标撞脸。

另外两处不是 `FontIcon` 的图标一并换掉以保持同一套字形：
导航「首页」原来写的是 `Icon="Home"`（SymbolIcon）、存档页搜索框原来写
`QueryIcon="Find"`（框架符号），现在都走 `ScIconFont`。

## Nova = 品牌标记

Nova 的图标不用图标库字形，用品牌手册的小尺寸标记
`Assets/Brand/starcloud-mark-small.svg`（16–24px 单块版：抬起的黄块 + 星）。
它是多色矢量，字形画不出来，所以按用途分成三条通道：

| 位置 | 用法 |
| --- | --- |
| 标题栏 Orb、Nova 面板标头、Nova 页登录卡、账户页 Starcloud ID 行 | `<ctrl:BrandMark Small="True" Width="…" Height="…" />` |
| 导航项 / 菜单项（必须是 `IconElement`） | `<ImageIcon Source="{StaticResource NovaMarkSmall}" />` |
| 空状态（`EmptyStateView`） | `IconImage="{StaticResource NovaMarkSmall}"`（该属性为图片，与 `Glyph` 二选一） |

`NovaMarkSmall` 是 App.xaml 里的 `SvgImageSource` 资源，三处引用同一个源，
不再各处写 URI 字符串。小尺寸版两个主题同一份文件（只有黄块与星，没有随主题变的描边）。

Orb 的**五态着色**改画在外圈描边上（`NovaOrbRing.BorderBrush` /
浮窗 `OrbBtn.BorderBrush`）：图标本身是多色图片，没法再靠 `Foreground` 表达状态，
而状态提示还有右上角黄点与 tooltip 两条通道。

## 维护提示

* 换图标 = 改 `Glyph` 码位，字体文件不用动；码位对照见上游仓库的 `selection.json`。
* 新增图标前先确认字形是线框风格（本库 `files-*` 为实心）——与现有图标混排会显重。
* 该字体只含图标码位（U+E900–U+ECB6），**不能**拿它当文字字体用。
