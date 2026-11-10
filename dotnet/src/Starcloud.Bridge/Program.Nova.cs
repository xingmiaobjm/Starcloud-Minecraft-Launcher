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

    private static async Task<object?> HandleNovaConfigGetAsync(TextWriter stdout, JsonElement p)
    {
            var profile = _settings.ActiveLlmProfile();
            return new
            {
                baseUrl = profile?.BaseUrl ?? "",
                apiKey = profile?.ApiKey ?? "",
                model = profile?.Model ?? "",
                gatewayEnabled = _settings.NovaGatewayEnabled,
            };
    }

    private static async Task<object?> HandleNovaConfigSetAsync(TextWriter stdout, JsonElement p)
    {
            var baseUrl = p.TryGetProperty("baseUrl", out var bu) &&
                          bu.ValueKind == JsonValueKind.String
                ? bu.GetString() ?? ""
                : "";
            var apiKey = p.TryGetProperty("apiKey", out var ak) &&
                         ak.ValueKind == JsonValueKind.String
                ? ak.GetString() ?? ""
                : "";
            var model = p.TryGetProperty("model", out var md) &&
                        md.ValueKind == JsonValueKind.String
                ? md.GetString() ?? ""
                : "";
            if (baseUrl.Length == 0 && apiKey.Length == 0 && model.Length == 0)
            {
                _settings.NovaLlmProfiles.Clear();
                _settings.NovaLlmActiveId = null;
            }
            else
            {
                var profile = _settings.ActiveLlmProfile()
                    ?? new NovaLlmProfile { Id = "custom", Name = "自定义模型" };
                profile.BaseUrl = baseUrl;
                profile.ApiKey = apiKey;
                profile.Model = model;
                if (string.IsNullOrWhiteSpace(profile.Name))
                    profile.Name = "自定义模型";
                _settings.NovaLlmProfiles.Clear();
                _settings.NovaLlmProfiles.Add(profile);
                _settings.NovaLlmActiveId = profile.Id;
            }
            if (p.TryGetProperty("gatewayEnabled", out var gw) &&
                gw.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _settings.NovaGatewayEnabled = gw.GetBoolean();
            _settings.Save();
            return new { baseUrl, apiKey, model, gatewayEnabled = _settings.NovaGatewayEnabled };
    }

    private static async Task<object?> HandleNovaPlanExecuteAsync(TextWriter stdout, JsonElement p)
    {
            var planId = p.GetProperty("id").GetString()!;
            if (!PendingNovaPlans.TryRemove(planId, out var plan))
                throw new Exception("确认清单已失效，请重新生成");
            var message = await plan.ExecuteAsync(CancellationToken.None);
            return new { ok = true, message };
    }

    private static async Task<object?> HandleNovaPlanDiscardAsync(TextWriter stdout, JsonElement p)
    {
            var planId = p.GetProperty("id").GetString()!;
            PendingNovaPlans.TryRemove(planId, out _);
            return new { ok = true };
    }

    private static async Task RunNovaChatAsync(
        TextWriter stdout, int id, JsonElement parameters)
    {
        try
        {
            var message = parameters.GetProperty("message").GetString() ?? "";
            var versionId = parameters.TryGetProperty("versionId", out var vid) &&
                            vid.ValueKind == JsonValueKind.String
                ? vid.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(versionId))
                _nova.SetCurrentInstance(versionId);

            var history = new List<NovaChatTurn>();
            if (parameters.TryGetProperty("history", out var h) &&
                h.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in h.EnumerateArray())
                {
                    var role = item.TryGetProperty("role", out var r) &&
                               r.ValueKind == JsonValueKind.String
                        ? r.GetString() ?? "user"
                        : "user";
                    var content = item.TryGetProperty("content", out var c) &&
                                  c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? ""
                        : "";
                    if (content.Length > 0) history.Add(new NovaChatTurn(role, content));
                }
            }

            var buffer = new StringBuilder();
            NovaReply? full = null;
            await foreach (var delta in _nova.ChatStreamAsync(
                               message, _settings, history,
                               onFullReply: reply =>
                               {
                                   full = reply;
                                   if (reply.Plan is { } plan)
                                   {
                                       if (PendingNovaPlans.Count > 20)
                                           PendingNovaPlans.Clear();
                                       PendingNovaPlans[plan.Id] = plan;
                                   }
                               },
                               onToolStarted: tool =>
                                   Emit(stdout, "nova.tool", new { tool })))
            {
                buffer.Append(delta);
                Emit(stdout, "nova.delta", new { text = delta });
            }

            var text = string.IsNullOrEmpty(full?.Text) ? buffer.ToString() : full!.Text;
            Emit(stdout, "nova.done", new
            {
                text,
                fromCloud = full?.FromCloud ?? false,
                planId = full?.Plan?.Id,
                planTitle = full?.Plan?.Title,
                plan = full?.Plan?.Items.Select(i => new { i.Title, i.Detail }),
            });
            Reply(stdout, id, ok: true, result: new { ok = true });
        }
        catch (Exception e)
        {
            Emit(stdout, "nova.failed", new { error = e.Message });
            Reply(stdout, id, ok: false, error: e.Message);
        }
    }
}
