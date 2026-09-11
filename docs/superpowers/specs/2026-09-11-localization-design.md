# 多语言与无障碍（D3）设计

> 目标：让 Starcloud 启动器在 **简体中文（zh-CN）** 与 **English（en-US）** 之间切换，并补齐关键无障碍信息；默认 zh-CN，行为与文案零回归。

## 1. 背景与现状侦察

- 项目此前**没有任何本地化基础设施**：无 `*.resw`、无 `*.resx`、无 `ResourceLoader` / `x:Uid` / `ResourceContext` 使用。
- 源语言为中文，硬编码规模：
  - XAML 属性（`Text` / `Content` / `Header` / `PlaceholderText` / `Title` / `Description` / `OffContent` / `OnContent` / `AutomationProperties.Name` / `ToolTipService.ToolTip`）：**453 处 / 12 文件**。
  - `.cs` 中文字符串字面量：**761 处 / 21 文件**（含对话框、InfoBar、Toast、错误提示等用户可见文案）。
- 已有 `LauncherSettings.Language`（默认 `zh_CN`）是**游戏语言**（透传给 Minecraft 的 `--lang`），与本次「启动器界面语言」无关，**不得复用**。

## 2. 方案选型

| 方案 | 说明 | 结论 |
| --- | --- | --- |
| A. `x:Uid` + `Strings/<lang>/Resources.resw` + `Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride` | WinUI 官方路径 | 放弃 |
| B. 自建 JSON 词典 + 附加属性 + `Loc.T()` | 资源由自己加载与查询 | **采用** |

放弃方案 A 的原因（均已在 WinAppSDK 1.8 上查证）：

1. 非打包应用下 `PrimaryLanguageOverride` **不跨会话持久化**，必须在资源加载前每次赋值；且该属性**一旦设置无法清空**（设 `""`/`null` 抛异常，microsoft/WindowsAppSDK#5335）。
2. 非打包应用下 `ResourceLoader` 历史上有**不遵循 override**（改用系统显示语言）的问题（#2806），须绕道 `ResourceManager` + `ResourceContext`。
3. `x:Uid` 需要为每个元素规划唯一 Uid，且同一元素的 `Text`/`ToolTip`/`AutomationProperties.Name` 各占一条带属性后缀的键，**453 处就要 500+ 条键**，与方案 B 的改造量相当，但多出 PRI/makepri 与语言上下文的运行时不确定性。

方案 B 的关键取舍：

- **源语言即键**：词条键 = 中文原文本身（如 `Loc.T("暂无已安装版本")`）。因此 zh-CN **不需要词典文件**，缺词条时直接回退原文 → 天然「零回归」。
- 只需要维护**一份英文词典** `en-US.json`，新增文案不会导致中文界面出现空白或 key 泄漏。
- 完全由我们掌控语言解析 → 支持**即时切换**（无需重启、无需 `PrimaryLanguageOverride`）。

## 3. 设计

### 3.1 资源组织

```
dotnet/src/Starcloud.App/Strings/en-US.json      # 嵌入式资源 EmbeddedResource
```

- 形如 `{ "暂无已安装版本": "No versions installed yet", "已下载 {0} 个文件": "Downloaded {0} files" }` 的扁平 `string → string` 映射。
- 以 `EmbeddedResource` 打包进 `Starcloud.App.dll`（逻辑名 `Starcloud.App.Strings.en-US.json`），避免非打包应用的路径/发布差异。
- `zh-CN` 无文件：`Loc` 查不到键时返回键本身。

### 3.2 `Loc` 服务（`Localization/Loc.cs`）

```csharp
public static class Loc
{
    public static string Current { get; }            // "zh-CN" | "en-US"
    public static event Action? LanguageChanged;     // 语言切换后触发（用于重放 XAML 附加属性）
    public static void Initialize(string? lang);     // App 构造最早期调用；钳制非法值 → zh-CN
    public static void SetLanguage(string? lang);    // 即时切换：换词典 + 触发 LanguageChanged
    public static string T(string key);              // 无参取词
    public static string T(string key, params object?[] args); // 取词后 string.Format
}
```

- 词典加载失败（文件缺失/JSON 损坏）→ 空词典 + 静默，全部文案回退中文，**不抛异常、不阻断启动**。
- `T` 的任何异常一律吞掉并返回 `key`。
- `Initialize` 必须在 `App` 构造函数最早期（`InitializeComponent()` 之前）调用，保证 `MainWindow` 之前语言已确定。

### 3.3 XAML 附加属性

XAML 无自定义标记扩展（`IMarkupExtension` 是 WPF 专有），因此用**附加属性**完成「声明式取词」：

| 附加属性 | 写入的目标属性 |
| --- | --- |
| `Loc.Text` | `TextBlock.Text`；`ButtonBase` / `ContentControl`（`Button` / `HyperlinkButton` / `AppBarButton`）的 `Content`；`ComboBoxItem` / `MenuFlyoutItem` / `ToggleMenuFlyoutItem` 的 `Content`/`Text` |
| `Loc.Header` | `ToggleSwitch.Header`、`Expander.Header`、`HeaderContentControl.Header` |
| `Loc.Placeholder` | `TextBox.PlaceholderText`、`AutoSuggestBox.PlaceholderText`、`PasswordBox.PlaceholderText` |
| `Loc.Tip` | `ToolTipService.ToolTip` |
| `Loc.Name` | `AutomationProperties.Name`（无障碍） |
| `Loc.Title` | `ContentDialog.Title` |
| `Loc.Description` | `Expander` / 自定义 `Description` 属性（存在则写，不存在忽略） |
| `Loc.OnContent` / `Loc.OffContent` | `ToggleSwitch.OnContent` / `OffContent` |

用法：

```xml
xmlns:loc="using:Starcloud.App.Localization"

<TextBlock loc:Loc.Text="暂无已安装版本" />
<Button loc:Loc.Text="添加账户" loc:Loc.Tip="添加一个离线或微软账户" />
```

`Loc` 内部用 `ConditionalWeakTable<DependencyObject, List<Entry>>` 记录每个元素被设置了哪些属性；`LanguageChanged` 时**重放**这些 setter（弱引用，元素释放不泄漏），实现**即时切换**。

### 3.4 即时切换的生效范围

- 语言变化 → `Loc.SetLanguage` → `LanguageChanged` → 已存在的 XAML 元素**立即**换文案。
- C# 侧在页面 `OnNavigatedTo` / 事件里临时取的词条：在**下次进入该页/下次触发**时使用新语言（不强制重建页面）。
- 设置页顶部 `InfoBar` 提示「界面语言已切换」，无需重启。

### 3.5 设置模型

`LauncherSettings` 新增（与游戏语言 `language` 严格区分）：

```csharp
[JsonPropertyName("ui_language")]
public string UiLanguage { get; set; } = "zh-CN";
```

- 读写时钳制到 `zh-CN` / `en-US`，非法值一律回落 `zh-CN`（无迁移框架，靠默认值 + 静默忽略未知字段）。
- `App` 构造函数最早期：`Loc.Initialize(AppServices.Instance.Settings.UiLanguage)`。

### 3.6 设置入口

「设置 → 通用 → 主题外观」卡片内新增「界面语言」下拉（`zh-CN` 显示「简体中文」，`en-US` 显示「English」），**即改即存**（`Save()` + `Loc.SetLanguage`），失败静默不阻断。

### 3.7 无障碍补强

- 所有**仅图标无文字**的交互控件（刷新 / 打开文件夹 / 删除 / 更多 / 关闭 / 折叠）补 `AutomationProperties.Name`（走 `Loc.Name`）。
- 图标按钮补 `ToolTipService.ToolTip`，与 `AutomationProperties.Name` 同源词条。
- 图片/图标容器 `AccessibilityView="Raw"`，避免读屏器朗读无意义内容（仅对纯装饰元素）。
- 表单控件保证 `Header`/`PlaceholderText` 成对存在。

### 3.8 非目标

- 不改变游戏语言（`--lang`）链路。
- 不引入第三种语言；不引入语言包热更新。
- 不本地化内部异常消息（`InvalidOperationException` 等，非用户可见）。
- 不引入 `x:Uid` / `.resw`。

## 4. 验收标准

1. `Starcloud.App` 与 `Starcloud.Core` 双构建 **0 警告 0 错误**。
2. 默认（`ui_language` 缺省）界面文案与改造前**逐字一致**。
3. 设置为 `en-US` 后重启：导航项、页面标题、按钮、空状态、对话框、InfoBar、错误提示均为英文；切回 `zh-CN` 恢复中文。
4. 设置页切换语言**即时生效**（当前页文案立刻变化）。
5. `en-US.json` 损坏或缺失时应用**正常启动**，界面为中文。
6. 关键图标按钮均有 `AutomationProperties.Name`。
