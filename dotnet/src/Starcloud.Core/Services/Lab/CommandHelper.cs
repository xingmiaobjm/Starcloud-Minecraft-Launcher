// SC 实验室 · 指令助手：Minecraft 命令模板库 + 参数装配 + 常用值校验。
//
// 每个模板 = 一段带 {占位符} 的命令 + 参数说明（名称 / 默认值 / 常用值提示）。
// 实验室页把它渲染成表单；Nova 也注册成工具（generate_command），两条入口共用同一份模板。

using System.Text.RegularExpressions;

namespace Starcloud.Core.Services.Lab;

/// <summary>模板参数（Key 与模板里的 {Key} 对应）</summary>
public sealed record CommandParam(
    string Key,
    string Label,
    string Default = "",
    /// <summary>常用值提示（逗号分隔；空 = 自由输入）</summary>
    string Suggestions = "",
    bool Required = false);

public sealed record CommandTemplate(
    string Id,
    string Category,
    string Name,
    string Template,
    IReadOnlyList<CommandParam> Params,
    string Note = "");

public static class CommandHelper
{
    /// <summary>模板库（覆盖生存 / 建造 / 管理三类高频命令）</summary>
    public static readonly IReadOnlyList<CommandTemplate> Templates =
    [
        new("give", "物品", "给予物品",
            "give {target} {item}{nbt} {count}",
            [
                new("target", "目标", "@s", "@s,@p,@a,玩家名", true),
                new("item", "物品 ID", "minecraft:diamond", "minecraft:diamond,minecraft:stone,minecraft:golden_apple", true),
                new("count", "数量", "1", "1,16,64"),
                new("nbt", "NBT（可空）", "", "{Enchantments:[{id:\"minecraft:sharpness\",lvl:5}]}"),
            ],
            "NBT 写在物品 ID 后面，注意引号转义"),

        new("tp", "移动", "传送",
            "tp {target} {x} {y} {z}",
            [
                new("target", "目标", "@s", "@s,@p,@a", true),
                new("x", "X", "0"), new("y", "Y", "64"), new("z", "Z", "0"),
            ]),

        new("tp_to", "移动", "传送到玩家",
            "tp {target} {dest}",
            [new("target", "谁", "@s", "@s,@a", true), new("dest", "到谁那里", "@p", "@p,玩家名", true)]),

        new("gamemode", "管理", "切换游戏模式",
            "gamemode {mode} {target}",
            [
                new("mode", "模式", "survival", "survival,creative,adventure,spectator", true),
                new("target", "目标", "@s", "@s,@a"),
            ]),

        new("time", "世界", "设置时间",
            "time set {value}",
            [new("value", "时间", "day", "day,noon,night,midnight,1000", true)]),

        new("weather", "世界", "设置天气",
            "weather {kind} {duration}",
            [
                new("kind", "天气", "clear", "clear,rain,thunder", true),
                new("duration", "持续（秒）", "600", "60,600,3600"),
            ]),

        new("gamerule", "世界", "游戏规则",
            "gamerule {rule} {value}",
            [
                new("rule", "规则", "keepInventory", "keepInventory,doDaylightCycle,mobGriefing,doFireTick,randomTickSpeed", true),
                new("value", "值", "true", "true,false,0,3"),
            ]),

        new("effect", "状态", "给予效果",
            "effect give {target} {effect} {duration} {amplifier}",
            [
                new("target", "目标", "@s", "@s,@a", true),
                new("effect", "效果", "minecraft:speed", "minecraft:speed,minecraft:strength,minecraft:night_vision,minecraft:regeneration", true),
                new("duration", "时长（秒）", "60", "30,60,600"),
                new("amplifier", "等级（0 起）", "0", "0,1,2,4"),
            ]),

        new("summon", "实体", "生成实体",
            "summon {entity} {x} {y} {z}{nbt}",
            [
                new("entity", "实体", "minecraft:zombie", "minecraft:zombie,minecraft:armor_stand,minecraft:item", true),
                new("x", "X", "~"), new("y", "Y", "~"), new("z", "Z", "~"),
                new("nbt", "NBT（可空）", "", "{NoAI:1b}"),
            ]),

        new("fill", "建造", "填充区域",
            "fill {x1} {y1} {z1} {x2} {y2} {z2} {block}",
            [
                new("x1", "起点 X", "~-5"), new("y1", "起点 Y", "~-1"), new("z1", "起点 Z", "~-5"),
                new("x2", "终点 X", "~5"), new("y2", "终点 Y", "~3"), new("z2", "终点 Z", "~5"),
                new("block", "方块", "minecraft:glass", "minecraft:glass,minecraft:stone,minecraft:air", true),
            ],
            "坐标支持 ~ 相对与 ^ 局部坐标"),

        new("setblock", "建造", "放置单个方块",
            "setblock {x} {y} {z} {block} {mode}",
            [
                new("x", "X", "~"), new("y", "Y", "~"), new("z", "Z", "~"),
                new("block", "方块", "minecraft:oak_planks", "minecraft:oak_planks,minecraft:torch", true),
                new("mode", "模式", "replace", "replace,keep,destroy"),
            ]),

        new("clone", "建造", "复制区域",
            "clone {x1} {y1} {z1} {x2} {y2} {z2} {dx} {dy} {dz}",
            [
                new("x1", "起点 X", "~-5"), new("y1", "起点 Y", "~"), new("z1", "起点 Z", "~-5"),
                new("x2", "终点 X", "~5"), new("y2", "终点 Y", "~5"), new("z2", "终点 Z", "~5"),
                new("dx", "目标偏移 X", "~"), new("dy", "目标偏移 Y", "~10"), new("dz", "目标偏移 Z", "~"),
            ]),

        new("kill", "管理", "清除实体",
            "kill {target}",
            [new("target", "目标", "@e[type=minecraft:zombie,distance=..20]", "@e,@e[type=minecraft:item],@s", true)]),

        new("enchant", "物品", "附魔手持物品",
            "enchant {target} {enchantment} {level}",
            [
                new("target", "目标", "@s", "@s", true),
                new("enchantment", "附魔", "minecraft:efficiency", "minecraft:efficiency,minecraft:fortune,minecraft:sharpness", true),
                new("level", "等级", "5", "1,3,5,10"),
            ]),

        new("xp", "状态", "经验",
            "xp add {target} {amount} {unit}",
            [
                new("target", "目标", "@s", "@s,@a", true),
                new("amount", "数量", "30", "10,30,100"),
                new("unit", "单位", "levels", "levels,points"),
            ]),

        new("title", "展示", "标题文字",
            "title {target} title {json}",
            [
                new("target", "目标", "@a", "@a,@s", true),
                new("json", "文本（JSON）", "{\"text\":\"欢迎！\",\"color\":\"gold\",\"bold\":true}",
                    "{\"text\":\"欢迎！\",\"color\":\"gold\"}"),
            ],
            "用「彩色文字」工具生成 JSON 文本更方便"),

        new("scoreboard", "管理", "计分板",
            "scoreboard objectives add {name} {criteria} {displayName}",
            [
                new("name", "目标名", "deaths", "deaths,kills,mined", true),
                new("criteria", "统计项", "deathCount", "deathCount,playerKillCount,dummy", true),
                new("displayName", "显示名（JSON）", "{\"text\":\"死亡数\"}", "{\"text\":\"分数\"}"),
            ]),
    ];

    public static IEnumerable<string> Categories => Templates.Select(t => t.Category).Distinct();

    /// <summary>按模板与参数装配命令；缺失的必填参数用占位符原样保留（方便用户看哪里没填）</summary>
    public static string Build(CommandTemplate template, IReadOnlyDictionary<string, string> values)
    {
        return Regex.Replace(template.Template, @"\{(\w+)\}", match =>
        {
            var key = match.Groups[1].Value;
            if (values.TryGetValue(key, out var value) && value.Trim().Length > 0)
                return value.Trim();
            return match.Value;
        });
    }

    /// <summary>装配结果里还有没有没填的占位符</summary>
    public static bool HasUnfilled(string command) => Regex.IsMatch(command, @"\{\w+\}");

    /// <summary>简单体检：括号配对与引号配对（命令里最常见的两类手误）</summary>
    public static string? Validate(string command)
    {
        var square = 0;
        var curly = 0;
        var quotes = 0;
        foreach (var c in command)
        {
            switch (c)
            {
                case '[': square++; break;
                case ']': square--; break;
                case '{': curly++; break;
                case '}': curly--; break;
                case '"': quotes++; break;
            }
            if (square < 0 || curly < 0) return "括号不配对：出现了多余的右括号";
        }
        if (square != 0) return "方括号 [] 不配对";
        if (curly != 0) return "花括号 {} 不配对";
        if (quotes % 2 != 0) return "双引号不成对";
        return null;
    }
}
