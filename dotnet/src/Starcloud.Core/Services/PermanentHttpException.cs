// 下载期「重试也没用」的错误类型。
//
// 为什么单独一个类型：404 / 410 说明文件在这个源上就是不存在，
// 再重试多少次、换多少个源都一样 —— 只能换源或直接放弃。
// 原来的实现把所有失败一律当可重试，404 会白白等满整轮退避，
// 批量下载时几百个失效文件能把进度卡很久。

namespace Starcloud.Core.Services;

/// <summary>永久性 HTTP 错误（404 / 410 等）：换源或放弃，不进重试退避。</summary>
public sealed class PermanentHttpException : IOException
{
    public PermanentHttpException(string message) : base(message) { }
}

/// <summary>Retry-After 响应头解析（支持「秒数」与「HTTP 日期」两种格式）。</summary>
public static class RetryAfterParser
{
    /// <summary>
    /// 解析 Retry-After。返回 null 表示没有这个头或格式不认识。
    /// <para>格式有两种：<c>Retry-After: 120</c>（秒）与
    /// <c>Retry-After: Wed, 21 Oct 2015 07:28:00 GMT</c>（日期）。
    /// 参照 PrismLauncher 的 Request::downloadError。</para>
    /// </summary>
    public static TimeSpan? Parse(string? headerValue, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        var text = headerValue.Trim();

        if (int.TryParse(text, out var seconds))
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

        if (DateTimeOffset.TryParse(text, out var when))
        {
            var delta = when - now;
            return delta > TimeSpan.Zero ? delta : null;
        }
        return null;
    }
}
