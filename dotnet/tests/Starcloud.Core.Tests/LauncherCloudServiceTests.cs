// 云服务契约解析测试：服务端 snake_case ↔ 客户端 PascalCase 的字段绑定。
// 回归背景：CheckinStatus 漏了 JsonPropertyName，snake_case 全绑不上，
// 界面显示「+0 token，连续 0 天」（服务端会话 2026-11-01 反馈）。

using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class LauncherCloudServiceTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void 签到状态_snake_case能绑定()
    {
        const string json = """{"signed_today":true,"reward_tokens":100000,"streak":3,"month_days":5}""";
        var status = JsonSerializer.Deserialize<LauncherCloudService.CheckinStatus>(json, Opts);
        Assert.NotNull(status);
        Assert.True(status!.SignedToday);
        Assert.Equal(100000, status.RewardTokens);
        Assert.Equal(3, status.Streak);
        Assert.Equal(5, status.MonthDays);
        Assert.Contains("+100000 token", status.Summary);
    }

    [Fact]
    public void 签到状态_未签时摘要是今日未签()
    {
        const string json = """{"signed_today":false,"reward_tokens":0,"streak":0,"month_days":0}""";
        var status = JsonSerializer.Deserialize<LauncherCloudService.CheckinStatus>(json, Opts);
        Assert.NotNull(status);
        Assert.False(status!.SignedToday);
        Assert.Equal("今天还没签到", status.Summary);
    }
}
