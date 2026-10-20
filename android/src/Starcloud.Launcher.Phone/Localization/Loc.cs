// 本地化服务（设计：localization-design.md §3.2/§3.3）
//
// 源语言即键：中文原文本身就是 key，因此 zh-CN 不需要词典文件，缺词条时回退原文。
// 只需维护一份英文词典 Strings/en-US.json（嵌入式资源）。
// 不走 x:Uid/.resw/PrimaryLanguageOverride：非打包应用下那套机制不持久化、无法清空、
// ResourceLoader 还有不遵循 override 的历史问题（WindowsAppSDK#5335 / #2806）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Starcloud.Launcher.Phone.Views.Controls;
using System.Text;
using System.Text.Json;

namespace Starcloud.Launcher.Phone.Localization;

public static class Loc
{
    private const string DefaultLang = "zh-CN";
    private const string EnUs = "en-US";

    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private static readonly List<Entry> _entries = [];
    private static readonly List<Template> _templates = [];

    /// <summary>当前界面语言：zh-CN（默认）或 en-US</summary>
    public static string Current { get; private set; } = DefaultLang;

    /// <summary>语言切换后触发；XAML 附加属性靠它重放，实现即时生效</summary>
    public static event Action? LanguageChanged;

    private sealed class Entry
    {
        public WeakReference<DependencyObject> Target = null!;
        public Action<DependencyObject> Apply = null!;
    }

    // ── 生命周期 ──────────────────────────────────────────────

    /// <summary>App 构造最早期调用（必须在 MainWindow 之前），确定本次会话语言。</summary>
    public static void Initialize(string? lang)
    {
        Current = Normalize(lang);
        Load();
    }

    /// <summary>即时切换界面语言：换词典 → 重放全部已本地化元素 → 通知订阅者。</summary>
    public static void SetLanguage(string? lang)
    {
        var next = Normalize(lang);
        if (next == Current && _map.Count > 0) return;
        Current = next;
        Load();
        ReplayAll();
        try { LanguageChanged?.Invoke(); } catch { /* 订阅者异常不阻断 */ }
    }

    private static string Normalize(string? lang) =>
        string.Equals(lang?.Trim(), EnUs, StringComparison.OrdinalIgnoreCase) ? EnUs : DefaultLang;

    private static void Load()
    {
        if (Current == DefaultLang)
        {
            _map = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        else
        {
            try
            {
                // 嵌入资源名跟随 Uno 工程根命名空间（Starcloud.Launcher.Phone.Strings/en-US.json）；
                // 资源未打包时流为 null → 空词典，全部回退中文
                using var s = typeof(Loc).Assembly
                    .GetManifestResourceStream($"Starcloud.Launcher.Phone.Strings.{Current}.json");
                if (s is null)
                {
                    _map = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                else
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
                    _map = dict is null
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : new Dictionary<string, string>(dict, StringComparer.Ordinal);
                }
            }
            catch
            {
                // 词典缺失/损坏 → 空词典，全部文案回退中文，绝不阻断启动
                _map = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
        BuildTemplates();
    }

    // ── 取词 ─────────────────────────────────────────────────

    public static string T(string key) => _map.TryGetValue(key, out var v) ? v : key;

    public static string T(string key, params object?[] args)
    {
        var tpl = _map.TryGetValue(key, out var v) ? v : key;
        try { return string.Format(tpl, args); }
        catch { return tpl; }
    }

    /// <summary>
    /// 本地化 Core 层产出的中文文案（源语言即键）。
    /// Core 不引用本类，且多数文案在 Core 内已用运行时值格式化（如「退出码 1」），
    /// 故此处除精确匹配外，还按词典里的 {0}/{1} 模板做占位符反解。
    /// </summary>
    public static string TCore(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (Current == DefaultLang) return text;
        if (_map.TryGetValue(text, out var exact)) return exact;
        foreach (var t in _templates)
        {
            var hit = t.TryApply(text);
            if (hit is not null) return hit;
        }
        return text;
    }

    /// <summary>含占位符的词典条目（如「退出码 {0}」），用于反解 Core 已格式化的文案</summary>
    private sealed class Template(string value, string[] literals)
    {
        public string? TryApply(string text)
        {
            if (text.Length < literals[0].Length) return null;
            if (!text.StartsWith(literals[0], StringComparison.Ordinal)) return null;
            var args = new string[literals.Length - 1];
            var pos = literals[0].Length;
            for (var i = 1; i < literals.Length; i++)
            {
                var lit = literals[i];
                int end;
                if (i == literals.Length - 1)
                {
                    if (text.Length - lit.Length < pos) return null;
                    if (!text.EndsWith(lit, StringComparison.Ordinal)) return null;
                    end = text.Length - lit.Length;
                }
                else
                {
                    end = text.IndexOf(lit, pos, StringComparison.Ordinal);
                    if (end < 0) return null;
                }
                args[i - 1] = text[pos..end];
                pos = end + lit.Length;
            }
            try { return string.Format(value, args); }
            catch { return null; }
        }

        /// <summary>把「前缀{0}中缀{1}后缀」切成字面量段；非法占位符返回 null</summary>
        public static string[]? SplitLiterals(string key)
        {
            var parts = new List<string>();
            var start = 0;
            for (var i = 0; i < key.Length; i++)
            {
                if (key[i] != '{') continue;
                var j = key.IndexOf('}', i + 1);
                if (j < 0) return null;
                var inner = key.AsSpan(i + 1, j - i - 1);
                var digits = 0;
                while (digits < inner.Length && char.IsAsciiDigit(inner[digits])) digits++;
                if (digits == 0) return null;                 // {name} 之类不参与反解
                if (digits < inner.Length && inner[digits] != ':') return null;
                parts.Add(key[start..i]);
                start = j + 1;
                i = j;
            }
            if (parts.Count == 0) return null;
            parts.Add(key[start..]);
            return [.. parts];
        }
    }

    private static void BuildTemplates()
    {
        _templates.Clear();
        foreach (var kv in _map)
        {
            if (kv.Key.IndexOf('{') < 0) continue;
            var literals = Template.SplitLiterals(kv.Key);
            if (literals is null || literals.Length < 2) continue;
            _templates.Add(new Template(kv.Value, literals));
        }
    }

    // ── 附加属性 ──────────────────────────────────────────────

    public static readonly DependencyProperty TextProperty = Reg("Text", ApplyText);
    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);

    public static readonly DependencyProperty HeaderProperty = Reg("Header", ApplyHeader);
    public static string GetHeader(DependencyObject d) => (string)d.GetValue(HeaderProperty);
    public static void SetHeader(DependencyObject d, string v) => d.SetValue(HeaderProperty, v);

    public static readonly DependencyProperty PlaceholderProperty = Reg("Placeholder", ApplyPlaceholder);
    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string v) => d.SetValue(PlaceholderProperty, v);

    public static readonly DependencyProperty TipProperty = Reg("Tip", ApplyTip);
    public static string GetTip(DependencyObject d) => (string)d.GetValue(TipProperty);
    public static void SetTip(DependencyObject d, string v) => d.SetValue(TipProperty, v);

    public static readonly DependencyProperty NameProperty = Reg("Name", ApplyName);
    public static string GetName(DependencyObject d) => (string)d.GetValue(NameProperty);
    public static void SetName(DependencyObject d, string v) => d.SetValue(NameProperty, v);

    public static readonly DependencyProperty TitleProperty = Reg("Title", ApplyTitle);
    public static string GetTitle(DependencyObject d) => (string)d.GetValue(TitleProperty);
    public static void SetTitle(DependencyObject d, string v) => d.SetValue(TitleProperty, v);

    public static readonly DependencyProperty MessageProperty = Reg("Message", ApplyMessage);
    public static string GetMessage(DependencyObject d) => (string)d.GetValue(MessageProperty);
    public static void SetMessage(DependencyObject d, string v) => d.SetValue(MessageProperty, v);

    public static readonly DependencyProperty OnContentProperty = Reg("OnContent", ApplyOnContent);
    public static string GetOnContent(DependencyObject d) => (string)d.GetValue(OnContentProperty);
    public static void SetOnContent(DependencyObject d, string v) => d.SetValue(OnContentProperty, v);

    public static readonly DependencyProperty OffContentProperty = Reg("OffContent", ApplyOffContent);
    public static string GetOffContent(DependencyObject d) => (string)d.GetValue(OffContentProperty);
    public static void SetOffContent(DependencyObject d, string v) => d.SetValue(OffContentProperty, v);

    public static readonly DependencyProperty SubtitleProperty = Reg("Subtitle", ApplySubtitle);
    public static string GetSubtitle(DependencyObject d) => (string)d.GetValue(SubtitleProperty);
    public static void SetSubtitle(DependencyObject d, string v) => d.SetValue(SubtitleProperty, v);

    public static readonly DependencyProperty ActionTextProperty = Reg("ActionText", ApplyActionText);
    public static string GetActionText(DependencyObject d) => (string)d.GetValue(ActionTextProperty);
    public static void SetActionText(DependencyObject d, string v) => d.SetValue(ActionTextProperty, v);

    private static DependencyProperty Reg(string name, Action<DependencyObject, string> apply)
    {
        DependencyProperty prop = null!;
        prop = DependencyProperty.RegisterAttached(
            name, typeof(string), typeof(Loc),
            new PropertyMetadata(null, (d, e) =>
            {
                if (e.NewValue is not string s || s.Length == 0) return;
                apply(d, s);
                Track(d, x => apply(x, (string)x.GetValue(prop)));
            }));
        return prop;
    }

    private static void Track(DependencyObject d, Action<DependencyObject> apply)
    {
        foreach (var e in _entries)
        {
            if (e.Target.TryGetTarget(out var t) && ReferenceEquals(t, d))
            {
                e.Apply = apply;
                return;
            }
        }
        _entries.Add(new Entry { Target = new WeakReference<DependencyObject>(d), Apply = apply });
    }

    private static void ReplayAll()
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (!_entries[i].Target.TryGetTarget(out var t))
            {
                _entries.RemoveAt(i);
                continue;
            }
            try { _entries[i].Apply(t); } catch { /* 单个元素失败不影响其余 */ }
        }
    }

    // ── 属性写入分派 ──────────────────────────────────────────

    private static void ApplyText(DependencyObject d, string s)
    {
        switch (d)
        {
            case TextBlock tb: tb.Text = s; break;
            case TextBox tbx: tbx.Text = s; break;
            case AutoSuggestBox asb: asb.Text = s; break;
            case ComboBoxItem ci: ci.Content = s; break;
            case MenuFlyoutItem mfi: mfi.Text = s; break;
            case EmptyStateView es: es.Title = s; break;
            case ButtonBase bb: bb.Content = s; break;
            case ContentControl cc when d is not UserControl: cc.Content = s; break;
        }
    }

    private static void ApplySubtitle(DependencyObject d, string s)
    {
        if (d is EmptyStateView es) es.Subtitle = s;
    }

    private static void ApplyActionText(DependencyObject d, string s)
    {
        if (d is EmptyStateView es) es.ActionText = s;
    }

    private static void ApplyHeader(DependencyObject d, string s)
    {
        switch (d)
        {
            case ToggleSwitch ts: ts.Header = s; break;
            case Expander ex: ex.Header = s; break;
        }
    }

    private static void ApplyPlaceholder(DependencyObject d, string s)
    {
        switch (d)
        {
            case TextBox tb: tb.PlaceholderText = s; break;
            // Uno 上 PasswordBox 继承自 TextBox，前一个 case 已覆盖（WinUI 里两者并列）
            case AutoSuggestBox ab: ab.PlaceholderText = s; break;
        }
    }

    private static void ApplyTip(DependencyObject d, string s)
    {
        ToolTipService.SetToolTip(d, s);
    }

    private static void ApplyName(DependencyObject d, string s)
    {
        AutomationProperties.SetName(d, s);
    }

    private static void ApplyTitle(DependencyObject d, string s)
    {
        switch (d)
        {
            case ContentDialog cd: cd.Title = s; break;
            case InfoBar ib: ib.Title = s; break;
            case EmptyStateView es: es.Title = s; break;
        }
    }

    private static void ApplyMessage(DependencyObject d, string s)
    {
        if (d is InfoBar ib) ib.Message = s;
    }

    private static void ApplyOnContent(DependencyObject d, string s)
    {
        if (d is ToggleSwitch ts) ts.OnContent = s;
    }

    private static void ApplyOffContent(DependencyObject d, string s)
    {
        if (d is ToggleSwitch ts) ts.OffContent = s;
    }
}
