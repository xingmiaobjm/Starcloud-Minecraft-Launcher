// 默认皮肤清单（账户详情页「默认皮肤」区的那 9 张卡）。
//
// 皮肤本体是**游戏官方默认皮肤**，随启动器发布在
// dotnet/src/Starcloud.App/Assets/Skins/default/ 下（见该目录的 README 说明来源）。
// 客户端 jar 里每个角色都有 wide / slim 两套纹理，但一个角色官方只用其中一套，
// 所以这里把「角色 → 版型」钉成清单（依据见下），App 侧按 File 取对应文件。
//
// 版型依据（手臂宽度，Minecraft Wiki「皮肤 § 默认皮肤」）：
//   Steve 4px、Ari 4px、Kai 4px、Sunny 4px、Zuri 4px  → classic（粗手臂）
//   Alex 3px、Efe 3px、Makena 3px、Noor 3px           → slim（细手臂）
// 这 9 条是"官方事实"，改错会让游戏里手臂粗细不对，所以有单元测试盯着。

namespace Starcloud.Core.Rendering;

/// <summary>官方默认皮肤：角色名 + 资产文件名 + 版型</summary>
public sealed record SkinPreset(string Id, string Name, string File, bool Slim);

public static class SkinPresets
{
    /// <summary>皮肤资产所在目录（相对启动器程序目录）</summary>
    public const string AssetDir = "Assets/Skins/default";

    /// <summary>默认皮肤清单（顺序即界面顺序，与游戏内的列举顺序一致）</summary>
    public static readonly IReadOnlyList<SkinPreset> All =
    [
        new("steve", "Steve", "steve.png", false),
        new("alex", "Alex", "alex.png", true),
        new("ari", "Ari", "ari.png", false),
        new("efe", "Efe", "efe.png", true),
        new("kai", "Kai", "kai.png", false),
        new("makena", "Makena", "makena.png", true),
        new("noor", "Noor", "noor.png", true),
        new("sunny", "Sunny", "sunny.png", false),
        new("zuri", "Zuri", "zuri.png", false),
    ];
}
