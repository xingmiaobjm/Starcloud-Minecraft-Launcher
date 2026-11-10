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

    private static async Task<object?> HandlePingAsync(TextWriter stdout, JsonElement p)
    {
            return new { pong = true, apiVersion = "bridge.1" };
    }

    private static async Task<object?> HandleDownloadTextAsync(TextWriter stdout, JsonElement p)
    {
            var url = p.GetProperty("url").GetString()!;
            return await _download.GetStringAsync(url);
    }

    private static async Task<object?> HandleDownloadFileAsync(TextWriter stdout, JsonElement p)
    {
            var url = p.GetProperty("url").GetString()!;
            var dest = p.GetProperty("dest").GetString()!;
            string? sha1 = p.TryGetProperty("sha1", out var s1) && s1.ValueKind == JsonValueKind.String
                ? s1.GetString()
                : null;

            // 字节进度：DownloadService 的回调是**增量**（分片下载会并发回调），
            // 这里累加并按 5 Hz 节流成事件；total 未知时按契约填 -1。
            var gate = new object();
            long received = 0;
            var lastEmit = 0L;
            void OnBytes(long increment)
            {
                long snapshot = 0;
                var emit = false;
                lock (gate)
                {
                    received += increment;
                    var now = Environment.TickCount64;
                    if (now - lastEmit >= 200)
                    {
                        lastEmit = now;
                        snapshot = received;
                        emit = true;
                    }
                }
                if (emit)
                {
                    Emit(stdout, "download.progress",
                        new { url, received = snapshot, total = -1L, done = false });
                }
            }

            await _download.DownloadFileAsync(
                url, dest, sha1, _settings.DownloadMaxRetries, onBytes: OnBytes);
            long total;
            lock (gate) total = received;
            Emit(stdout, "download.progress",
                new { url, received = total, total, done = true });
            return new { ok = true, bytes = total };
    }

    private static async Task<object?> HandleShellOpenPathAsync(TextWriter stdout, JsonElement p)
    {
            // 打开目录 / 在文件夹中显示。Linux 下走 xdg-open，Windows 走 explorer。
            var target = p.GetProperty("path").GetString()!;
            if (string.IsNullOrWhiteSpace(target)) throw new Exception("路径为空");
            var full = Path.GetFullPath(target);
            var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            if (dir is null || !Directory.Exists(dir))
                throw new Exception($"路径不存在：${target}");
            OpenFolder(dir);
            return new { ok = true, path = dir };
    }

    private static async Task<object?> HandleAppPathsAsync(TextWriter stdout, JsonElement p)
    {
            return new
            {
                dataDir = LauncherPaths.DataDir,
                logDir = LauncherPaths.LogsDir,
                logFile = LauncherLog.CurrentFile,
            };
    }

}