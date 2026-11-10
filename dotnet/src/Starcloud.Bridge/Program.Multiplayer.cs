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

    private static async Task<object?> HandleMultiplayerStateAsync(TextWriter stdout, JsonElement p)
    {
            var snap = _multiplayer.Snapshot ?? await _multiplayer.RefreshAsync();
            return MultiplayerPayload(snap);
    }

    private static async Task<object?> HandleMultiplayerSelectProviderAsync(TextWriter stdout, JsonElement p)
    {
            _multiplayer.SelectProvider(p.GetProperty("id").GetString()!);
            return MultiplayerPayload(await _multiplayer.RefreshAsync());
    }

    private static async Task<object?> HandleMultiplayerSetNicknameAsync(TextWriter stdout, JsonElement p)
    {
            _multiplayer.Nickname = p.GetProperty("name").GetString() ?? "";
            return MultiplayerPayload(await _multiplayer.RefreshAsync());
    }

    private static async Task<object?> HandleMultiplayerSetNodeRegionAsync(TextWriter stdout, JsonElement p)
    {
            _multiplayer.NodeRegion = p.GetProperty("region").GetString() ?? "auto";
            return MultiplayerPayload(await _multiplayer.RefreshAsync());
    }

    private static async Task<object?> HandleMultiplayerHostAsync(TextWriter stdout, JsonElement p)
    {
            await _multiplayer.HostAsync(CancellationToken.None);
            return MultiplayerPayload(await _multiplayer.RefreshAsync());
    }

    private static async Task<object?> HandleMultiplayerJoinAsync(TextWriter stdout, JsonElement p)
    {
            await _multiplayer.JoinAsync(
                p.GetProperty("room").GetString()!, CancellationToken.None);
            return MultiplayerPayload(await _multiplayer.RefreshAsync());
    }

    private static async Task<object?> HandleMultiplayerIdleAsync(TextWriter stdout, JsonElement p)
    {
            await _multiplayer.IdleAsync(CancellationToken.None);
            return MultiplayerPayload(await _multiplayer.RefreshAsync());
    }

    private static async Task<object?> HandleMultiplayerStopAsync(TextWriter stdout, JsonElement p)
    {
            await _multiplayer.StopAsync(CancellationToken.None);
            return MultiplayerPayload(await _multiplayer.RefreshAsync());
    }

    private static async Task<object?> HandleMultiplayerLogsAsync(TextWriter stdout, JsonElement p)
    {
            return new { logs = await _multiplayer.FetchLogsAsync(CancellationToken.None) };
    }

    private static async Task<object?> HandleMultiplayerNodesAsync(TextWriter stdout, JsonElement p)
    {
            return new { nodes = await _multiplayer.FetchNodesAsync(CancellationToken.None) };
    }

    /// <summary>联机快照 + 提供方/玩家名/节点地区，一次给全（UI 只渲染）。</summary>
    private static object MultiplayerPayload(MultiplayerSnapshot snap)
    {
        var state = snap.State;
        return new
        {
            providers = _multiplayer.Providers.Select(p => new
            {
                p.Id,
                p.DisplayName,
                p.Vendor,
                p.VendorUrl,
                p.Description,
                p.BuiltIn,
            }),
            activeId = snap.ProviderId,
            nickname = _multiplayer.Nickname,
            nodeRegion = _multiplayer.NodeRegion,
            core = new
            {
                installed = snap.Core.Installed,
                version = snap.Core.Version,
                sessionRunning = snap.Core.SessionRunning,
            },
            state = new
            {
                phase = PhaseKey(state.Phase),
                room = state.Room,
                address = state.Address,
                difficulty = state.Difficulty,
                players = state.Players,
                errorKind = state.ErrorKind,
                error = state.Error,
                isBusy = state.IsBusy,
            },
            lastError = snap.LastError,
        };
    }

    private static string PhaseKey(MultiplayerPhase phase) => phase switch
    {
        MultiplayerPhase.Idle => "idle",
        MultiplayerPhase.HostScanning => "hostScanning",
        MultiplayerPhase.HostStarting => "hostStarting",
        MultiplayerPhase.HostReady => "hostReady",
        MultiplayerPhase.GuestConnecting => "guestConnecting",
        MultiplayerPhase.GuestEntering => "guestEntering",
        MultiplayerPhase.GuestReady => "guestReady",
        _ => "failed",
    };

    private static async Task RunMultiplayerInstallAsync(TextWriter stdout, int id, JsonElement parameters)
    {
        try
        {
            var progress = new Progress<MultiplayerProgress>(p =>
                Emit(stdout, "multiplayer.progress", new
                {
                    stage = p.Stage,
                    bytes = p.Bytes,
                    total = p.Total,
                    speedBps = p.SpeedBps,
                }));
            await _multiplayer.InstallCoreAsync(progress, CancellationToken.None);
            Emit(stdout, "multiplayer.install.done", new { });
            Reply(stdout, id, ok: true, result: new { installed = true });
        }
        catch (Exception e)
        {
            Emit(stdout, "multiplayer.install.failed", new { error = e.Message });
            Reply(stdout, id, ok: false, error: e.Message);
        }
    }
}
