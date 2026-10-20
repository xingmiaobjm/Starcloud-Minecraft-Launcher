// 搜索词翻译的文案（两处入口共用：下载页社区搜索、资源下载窗搜索）
//
// 规则：翻译**来源代号**由 Core 给出（dict / mcmod / machine / cache），
// 文案在这里本地化，避免 Core 里出现中文硬编码。

using Starcloud.Launcher.Uno.Localization;
using Starcloud.Core.Services;

namespace Starcloud.Launcher.Uno.Views.Shared;

public static class SearchTranslationHint
{
    /// <summary>来源标签；未知代号返回空串（调用方只显示搜索词）</summary>
    public static string SourceText(string? source) => source switch
    {
        "dict" => Loc.T("内置词典"),
        "mcmod" => Loc.T("MC 百科"),
        "machine" => Loc.T("机器翻译"),
        "cache" => Loc.T("缓存"),
        "latin" => Loc.T("只取英文部分"),
        _ => "",
    };

    /// <summary>「已按「Sodium」搜索（MC 百科）」；没发生翻译返回空串</summary>
    public static string Describe(TranslatedQuery translated)
    {
        if (!translated.Translated) return "";
        var source = SourceText(translated.Source);
        return source.Length == 0
            ? Loc.T("已按「{0}」搜索", translated.Effective)
            : Loc.T("已按「{0}」搜索（{1}）", translated.Effective, source);
    }
}
