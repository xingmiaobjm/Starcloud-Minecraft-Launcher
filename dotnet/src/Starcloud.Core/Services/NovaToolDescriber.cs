// Nova 工具调用 → 人话。
//
// 确认清单以前直接把 function calling 的参数 JSON 摊给用户（{"project_id":"…","kind":"modpack"}），
// 用户根本不知道那是什么。这里按工具名 + 参数拼一句中文；没有专门映射的工具退化成
// 「中文标签：值 · …」，至少不再出现 project_id / version_id 这种字段名。

using System.Text.Json;

namespace Starcloud.Core.Services;

public static class NovaToolDescriber
{
    /// <summary>把一次工具调用翻译成一行用户能看懂的中文。</summary>
    public static string Describe(string toolName, string argumentsJson)
    {
        JsonElement args;
        try
        {
            args = string.IsNullOrWhiteSpace(argumentsJson)
                ? default
                : JsonDocument.Parse(argumentsJson).RootElement.Clone();
        }
        catch
        {
            return "参数无法解析";
        }

        string? S(string key) =>
            args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        string Tg(string key = "target_version") => S(key) ?? "当前选中版本";

        return toolName switch
        {
            "install_minecraft" => $"安装 Minecraft {S("version_id") ?? "（未指定版本）"}",
            "install_loader" => $"为 {S("mc_version") ?? "未指定版本"} 安装 {LoaderName(S("loader"))}" +
                                 (S("build") is { } b ? $"（构建 {b}）" : ""),
            "delete_version" => $"删除实例 {S("version_id")}（不可撤销）",
            "rename_version" => $"把实例 {S("version_id")} 重命名为 {S("new_id")}",
            "launch_game" => $"启动 {Tg("version_id")}",
            "repair_instance" => $"修复 {Tg("version_id")} 的缺失文件",
            "resolve_mod_conflicts" => "停用重复的旧版本模组（改名 .disabled，不删除）",
            "install_content" => $"安装{KindName(S("kind"))}{Quote(S("project_id"))} → {Tg()}",
            "install_modpack" => $"安装整合包{Quote(FileNameOf(S("path")))}",
            "enable_content" => $"把资源库里的 {S("file_name")} 装载到 {Tg()}",
            "remove_from_library" => $"从资源库删除 {S("file_name")}",
            "export_modpack" => $"把 {Tg("version_id")} 导出为整合包",
            "backup_version" => $"备份 {Tg("version_id")}" +
                                 (S("name") is { } bn ? $"（名为 {bn}）" : ""),
            "restore_backup" => $"还原备份 {S("backup_id")}",
            "delete_backup" => $"删除备份 {S("backup_id")}",
            _ => Generic(args),
        };
    }

    private static string Generic(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "无额外参数";
        var parts = new List<string>();
        foreach (var p in args.EnumerateObject())
        {
            if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)
                continue;
            var value = p.Value.ValueKind switch
            {
                JsonValueKind.True => "是",
                JsonValueKind.False => "否",
                _ => p.Value.ToString(),
            };
            parts.Add($"{Label(p.Name)}：{value}");
        }
        return parts.Count == 0 ? "无额外参数" : string.Join(" · ", parts);
    }

    private static string Label(string key) => key switch
    {
        "version_id" => "实例",
        "target_version" => "目标实例",
        "mc_version" => "MC 版本",
        "project_id" => "项目",
        "version_number" => "资源版本",
        "kind" => "类型",
        "source" => "来源",
        "loader" => "加载器",
        "build" => "构建号",
        "name" => "名称",
        "new_id" => "新名称",
        "path" => "文件",
        "file_name" => "文件",
        "backup_id" => "备份",
        "include_saves" => "包含存档",
        "query" => "关键词",
        "url" => "地址",
        _ => key,
    };

    private static string KindName(string? kind) => kind switch
    {
        "mod" => " Mod",
        "resourcepack" => "资源包",
        "shader" => "光影",
        "datapack" => "数据包",
        "modpack" => "整合包",
        _ => "资源",
    };

    private static string LoaderName(string? loader) => loader switch
    {
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        _ => loader ?? "加载器",
    };

    private static string Quote(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : $"「{text}」";

    private static string? FileNameOf(string? path) =>
        string.IsNullOrWhiteSpace(path) ? path : Path.GetFileName(path);
}
