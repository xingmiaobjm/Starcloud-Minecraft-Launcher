using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core;
using Starcloud.Core.Models;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Multiplayer;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Bridge;

public static partial class Program
{

    private static async Task<object?> HandleVersionsDeleteAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            if (!InstanceLayout.IsValidVersionId(versionId))
                throw new Exception("非法版本 id");
            if (Methods.IsRunning("install.vanilla"))
                throw new Exception("安装进行中，暂不能删除版本");
            if (Sessions.Values.Any(s => s.VersionId == versionId && !s.HasExited))
                throw new Exception("该版本正在运行，请先关闭游戏");

            var versionsRoot = Path.Combine(_settings.DotMinecraft, "versions");
            var dir = Path.Combine(versionsRoot, versionId);
            if (!Directory.Exists(dir))
                throw new Exception($"版本目录不存在：{versionId}");

            // 被其它版本的 inheritsFrom 依赖 → 拒绝：删掉父版本会让加载器实例无法启动。
            foreach (var sub in Directory.EnumerateDirectories(versionsRoot))
            {
                var otherId = Path.GetFileName(sub);
                if (otherId == versionId) continue;
                var json = Path.Combine(sub, otherId + ".json");
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(json));
                    if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh) &&
                        inh.ValueKind == JsonValueKind.String &&
                        inh.GetString() == versionId)
                        throw new Exception($"版本 {otherId} 依赖该版本（inheritsFrom），不能删除");
                }
                catch (JsonException) { /* 坏 JSON 不阻塞删除 */ }
                catch (IOException) { /* 读不到就跳过 */ }
            }

            long freed = 0;
            try
            {
                freed = new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch { /* 统计失败不影响删除 */ }

            Directory.Delete(dir, recursive: true);
            var wasDefault = _settings.LastVersion == versionId;
            if (wasDefault)
            {
                _settings.LastVersion = null;
                _settings.Save();
            }
            return new { deleted = true, freedBytes = freed, wasDefault };
    }

    private static async Task<object?> HandleVersionsOpenFolderAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            if (!InstanceLayout.IsValidVersionId(versionId))
                throw new Exception("非法版本 id");
            // 文件夹入口跟随实例根（Win 同款）：隔离实例各自持有一份，共享实例落在 .minecraft 下
            var kind = p.TryGetProperty("kind", out var ofKind) && ofKind.ValueKind == JsonValueKind.String
                ? ofKind.GetString()
                : null;
            string dir;
            if (kind is "saves" or "mods")
            {
                var store = new InstanceSettingsStore(_settings.DotMinecraft);
                var isolated = InstanceLayout.IsIsolated(store.Get(versionId), _settings);
                var root = InstanceLayout.GameRoot(_settings.DotMinecraft, versionId, isolated);
                dir = Path.Combine(root, kind);
            }
            else
            {
                dir = Path.Combine(_settings.DotMinecraft, "versions", versionId);
            }
            Directory.CreateDirectory(dir);
            OpenFolder(dir);
            return new { ok = true, path = dir };
    }

    private static async Task<object?> HandleVersionsSetDefaultAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.TryGetProperty("versionId", out var v) &&
                            v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
            if (!string.IsNullOrEmpty(versionId) &&
                !Directory.Exists(Path.Combine(_settings.DotMinecraft, "versions", versionId)))
                throw new Exception($"版本目录不存在：{versionId}");
            _settings.LastVersion = string.IsNullOrEmpty(versionId) ? null : versionId;
            _settings.Save();
            return new { ok = true, lastVersion = _settings.LastVersion };
    }

    private static async Task<object?> HandleVersionsSetMarksAsync(TextWriter stdout, JsonElement p)
    {
            // 置顶 / 收藏标记（与 Win 端主页版本卡的标记口径一致：存实例档案）。
            // 两个开关都可选：传了才改，没传保持原值。
            var versionId = p.GetProperty("versionId").GetString()!;
            if (!Directory.Exists(Path.Combine(_settings.DotMinecraft, "versions", versionId)))
                throw new Exception($"版本目录不存在：{versionId}");
            var store = new InstanceSettingsStore(_settings.DotMinecraft);
            var profile = store.Get(versionId);
            if (p.TryGetProperty("pinned", out var pin) && IsBool(pin))
                profile.Pinned = pin.GetBoolean();
            if (p.TryGetProperty("favorite", out var fav) && IsBool(fav))
                profile.Favorite = fav.GetBoolean();
            store.Set(versionId, profile);
            store.Save();
            return new { ok = true, pinned = profile.Pinned, favorite = profile.Favorite };
    }

    private static async Task<object?> HandleVersionsRenameAsync(TextWriter stdout, JsonElement p)
    {
            var oldId = p.GetProperty("versionId").GetString()!;
            var newId = p.GetProperty("newName").GetString()!.Trim();
            if (!InstanceLayout.IsValidVersionId(oldId) || !InstanceLayout.IsValidVersionId(newId))
                throw new Exception("版本名不能为空，且不能包含 < > : \" / \\ | ? *");
            if (newId != oldId)
            {
                InstanceLayout.RenameInstance(_settings.DotMinecraft, oldId, newId);
                new InstanceSettingsStore(_settings.DotMinecraft).RenameKey(oldId, newId);
            }
            return new { ok = true, versionId = newId };
    }

    private static async Task<object?> HandleVersionsCopyAsync(TextWriter stdout, JsonElement p)
    {
            var srcId = p.GetProperty("versionId").GetString()!;
            var newId = p.GetProperty("newId").GetString()!.Trim();
            if (!InstanceLayout.IsValidVersionId(srcId) || !InstanceLayout.IsValidVersionId(newId))
                throw new Exception("版本名不能为空，且不能包含 < > : \" / \\ | ? *");
            var srcStore = new InstanceSettingsStore(_settings.DotMinecraft);
            var copyContent = p.TryGetProperty("copyContent", out var cc) && cc.GetBoolean();
            InstanceLayout.CopyInstance(_settings.DotMinecraft, srcId, newId, copyContent);
            // 档案深拷贝（Win 同款）：类别/备注等沿用，时间与图标从零开始
            var copy = JsonSerializer.Deserialize<VersionProfile>(
                JsonSerializer.Serialize(srcStore.Get(srcId))) ?? new VersionProfile();
            copy.LastPlayedAt = null;
            copy.IconPath = null;
            srcStore.Set(newId, copy);
            srcStore.Save();
            return new { ok = true, versionId = newId };
    }
}
