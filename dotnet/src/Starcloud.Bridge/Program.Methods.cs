using System.Text.Json;

namespace Starcloud.Bridge;

/// <summary>
/// 桥接方法表：**新增一个桥接方法只改这一个文件**。
///
/// 历史问题（拆分前）：方法名在三个地方各写一遍——<c>DispatchAsync</c> 的 switch、
/// <c>IsLongRunning</c> 的长任务名单、以及各 Handle 方法的实现。三处不同步就会
/// 出现「方法实现了但调不到」「该并发的方法阻塞了主循环」这类静默故障，
/// 编译器完全帮不上忙。
///
/// 现在语义（顺序 / 并发 / 单飞）与实现写在相邻的一行里，主循环只按 Kind 执行，
/// 不再持有任何方法名字符串。核对方式见 docs/bridge-extension.md。
/// </summary>
public static partial class Program
{
    /// <summary>与 Windows 端一致的启动器版本（manifest 兼容性校验用）。</summary>
    private const string LauncherVersion = "0.4.0";

    private static BridgeMethodTable? _methods;

    /// <summary>方法表（首次访问时构建；进程内单例）。</summary>
    private static BridgeMethodTable Methods => _methods ??= BuildMethodTable();

    /// <summary>
    /// 登记全部桥接方法。分组顺序 = <c>bridge.methods</c> 的输出顺序 = 文档章节顺序。
    ///
    /// 三档语义怎么选：
    /// · <c>Request</c>   毫秒级、纯内存或纯本地读，占着主循环也不影响别人；
    /// · <c>Concurrent</c> 会阻塞（网络 / 磁盘扫描 / 起进程 / 压缩解压），必须让出主循环；
    /// · <c>Streaming</c> 单飞长任务：先回 <c>{accepted:true}</c>，进度与终态走事件。
    /// </summary>
    private static BridgeMethodTable BuildMethodTable()
    {
        var t = new BridgeMethodTable();

        // ── 基础（ping / 自省 / 下载 / 外壳）────────────────────────────────
        t.Request("ping", HandlePingAsync);
        t.Request("bridge.methods", HandleBridgeMethodsAsync);
        t.Request("app.paths", HandleAppPathsAsync);
        t.Concurrent("download.text", HandleDownloadTextAsync);
        t.Concurrent("download.file", HandleDownloadFileAsync);
        t.Request("shell.openPath", HandleShellOpenPathAsync);

        // ── 单飞长任务（进度事件流）──────────────────────────────────────────
        t.Streaming("install.vanilla", "已有安装任务在进行", RunInstallAsync);
        t.Streaming("multiplayer.install", "联机核心正在安装", RunMultiplayerInstallAsync);
        t.Streaming("nova.chat", "Nova 正在回复上一条消息", RunNovaChatAsync);

        // ── 版本目录（清单 / 实例 / 设置 / 备份 / 整合包）────────────────────
        t.Request("catalog.versions", HandleCatalogVersionsAsync);
        t.Request("catalog.installed", HandleCatalogInstalledAsync);
        t.Request("versions.delete", HandleVersionsDeleteAsync);
        t.Request("versions.openFolder", HandleVersionsOpenFolderAsync);
        t.Request("versions.setDefault", HandleVersionsSetDefaultAsync);
        t.Request("versions.setMarks", HandleVersionsSetMarksAsync);
        t.Request("version.settings", HandleVersionSettingsAsync);
        t.Request("version.saveSettings", HandleVersionSaveSettingsAsync);
        t.Request("version.detail", HandleVersionDetailAsync);
        t.Request("version.toggleContent", HandleVersionToggleContentAsync);
        t.Request("version.deleteContent", HandleVersionDeleteContentAsync);
        t.Request("version.openContentFolder", HandleVersionOpenContentFolderAsync);
        t.Concurrent("version.checkUpdates", HandleVersionCheckUpdatesAsync);
        t.Concurrent("version.updateMod", HandleVersionUpdateModAsync);
        t.Concurrent("version.checkDependencies", HandleVersionCheckDependenciesAsync);
        t.Concurrent("versions.rename", HandleVersionsRenameAsync);
        t.Concurrent("versions.copy", HandleVersionsCopyAsync);
        t.Concurrent("versions.exportProbe", HandleVersionsExportProbeAsync);
        t.Concurrent("versions.exportModpack", HandleVersionsExportModpackAsync);
        t.Concurrent("versions.resolvePackLink", HandleVersionsResolvePackLinkAsync);
        t.Concurrent("versions.downloadPack", HandleVersionsDownloadPackAsync);
        t.Concurrent("versions.packPreview", HandleVersionsPackPreviewAsync);
        t.Concurrent("versions.importPack", HandleVersionsImportPackAsync);
        t.Concurrent("versions.backupList", HandleVersionsBackupListAsync);
        t.Concurrent("versions.backupCreate", HandleVersionsBackupCreateAsync);
        t.Concurrent("versions.backupRestore", HandleVersionsBackupRestoreAsync);
        t.Concurrent("versions.backupDelete", HandleVersionsBackupDeleteAsync);
        t.Concurrent("versions.backupRebuild", HandleVersionsBackupRebuildAsync);

        // ── 存档 ────────────────────────────────────────────────────────────
        t.Request("saves.icon", HandleSavesIconAsync);
        t.Request("saves.list", HandleSavesListAsync);
        t.Request("saves.openFolder", HandleSavesOpenFolderAsync);
        t.Request("saves.backup", HandleSavesBackupAsync);
        t.Request("saves.delete", HandleSavesDeleteAsync);
        t.Concurrent("saves.exportShare", HandleSavesExportShareAsync);
        t.Concurrent("saves.importShare", HandleSavesImportShareAsync);
        t.Concurrent("saves.copyToVersion", HandleSavesCopyToVersionAsync);

        // ── 外部插件（v2 沙箱）──────────────────────────────────────────────
        t.Request("plugins.list", HandlePluginsListAsync);
        t.Request("plugins.install", HandlePluginsInstallAsync);
        t.Request("plugins.uninstall", HandlePluginsUninstallAsync);
        t.Request("plugins.setEnabled", HandlePluginsSetEnabledAsync);
        t.Request("plugins.panels", HandlePluginsPanelsAsync);
        t.Request("plugins.view", HandlePluginsViewAsync);
        t.Request("plugins.openFolder", HandlePluginsOpenFolderAsync);
        t.Concurrent("plugins.invoke", HandlePluginsInvokeAsync);

        // ── SC 实验室 / 投影工坊 ────────────────────────────────────────────
        t.Request("lab.palette", HandleLabPaletteAsync);
        t.Concurrent("lab.load", HandleLabLoadAsync);
        t.Concurrent("lab.assets", HandleLabAssetsAsync);
        t.Concurrent("lab.worldEater", HandleLabWorldEaterAsync);
        t.Concurrent("lab.colorText", HandleLabColorTextAsync);
        t.Concurrent("projections.scan", HandleProjectionsScanAsync);
        t.Concurrent("projections.copy", HandleProjectionsCopyAsync);
        t.Concurrent("projections.move", HandleProjectionsMoveAsync);
        t.Concurrent("projections.delete", HandleProjectionsDeleteAsync);

        // ── 内容库（模组 / 皮肤 / 翻译）─────────────────────────────────────
        t.Request("content.translate", HandleContentTranslateAsync);
        t.Request("content.installModpack", HandleContentInstallModpackAsync);
        t.Concurrent("content.search", HandleContentSearchAsync);
        t.Concurrent("content.install", HandleContentInstallAsync);
        t.Request("skins.list", HandleSkinsListAsync);
        t.Request("skins.importPath", HandleSkinsImportPathAsync);
        t.Request("skins.rename", HandleSkinsRenameAsync);
        t.Request("skins.setModel", HandleSkinsSetModelAsync);
        t.Request("skins.delete", HandleSkinsDeleteAsync);
        t.Request("skins.bs6.status", HandleSkinsBs6StatusAsync);
        t.Concurrent("skins.importUrl", HandleSkinsImportUrlAsync);
        t.Concurrent("skins.station.list", HandleSkinsStationListAsync);
        t.Concurrent("skins.station.apply", HandleSkinsStationApplyAsync);
        t.Concurrent("skins.bs6.authorize", HandleSkinsBs6AuthorizeAsync);
        t.Concurrent("skins.bs6.closet", HandleSkinsBs6ClosetAsync);
        t.Concurrent("skins.bs6.apply", HandleSkinsBs6ApplyAsync);

        // ── Nova 助手 ───────────────────────────────────────────────────────
        t.Request("nova.config.get", HandleNovaConfigGetAsync);
        t.Request("nova.config.set", HandleNovaConfigSetAsync);
        t.Request("nova.plan.discard", HandleNovaPlanDiscardAsync);
        t.Concurrent("nova.plan.execute", HandleNovaPlanExecuteAsync);

        // ── 联机（陶瓦）─────────────────────────────────────────────────────
        t.Request("multiplayer.state", HandleMultiplayerStateAsync);
        t.Request("multiplayer.selectProvider", HandleMultiplayerSelectProviderAsync);
        t.Request("multiplayer.setNickname", HandleMultiplayerSetNicknameAsync);
        t.Request("multiplayer.setNodeRegion", HandleMultiplayerSetNodeRegionAsync);
        t.Request("multiplayer.idle", HandleMultiplayerIdleAsync);
        t.Concurrent("multiplayer.host", HandleMultiplayerHostAsync);
        t.Concurrent("multiplayer.join", HandleMultiplayerJoinAsync);
        t.Concurrent("multiplayer.stop", HandleMultiplayerStopAsync);
        t.Concurrent("multiplayer.logs", HandleMultiplayerLogsAsync);
        t.Concurrent("multiplayer.nodes", HandleMultiplayerNodesAsync);

        // ── 启动 / 运行态 ───────────────────────────────────────────────────
        t.Request("game.kill", HandleGameKillAsync);
        t.Request("runtime.state", HandleRuntimeStateAsync);
        t.Request("runtime.select", HandleRuntimeSelectAsync);
        t.Request("runtime.interval", HandleRuntimeIntervalAsync);
        t.Request("runtime.gpu", HandleRuntimeGpuAsync);
        t.Request("runtime.kill", HandleRuntimeKillAsync);
        t.Request("runtime.logs", HandleRuntimeLogsAsync);
        t.Concurrent("java.list", HandleJavaListAsync);
        t.Concurrent("launch.start", HandleLaunchStartAsync);
        t.Concurrent("runtime.diagnose", HandleRuntimeDiagnoseAsync);

        // ── 账户（本地 / 微软 / 第三方 / Starcloud ID）──────────────────────
        t.Request("accounts.list", HandleAccountsListAsync);
        t.Request("accounts.addOffline", HandleAccountsAddOfflineAsync);
        t.Request("accounts.setCurrent", HandleAccountsSetCurrentAsync);
        t.Request("accounts.remove", HandleAccountsRemoveAsync);
        t.Request("accounts.setSkin", HandleAccountsSetSkinAsync);
        t.Request("auth.microsoft.start", HandleAuthMicrosoftStartAsync);
        t.Request("auth.microsoft.poll", HandleAuthMicrosoftPollAsync);
        t.Request("auth.yggdrasil.login", HandleAuthYggdrasilLoginAsync);
        t.Request("starcloud.state", HandleStarcloudStateAsync);
        t.Request("starcloud.logout", HandleStarcloudLogoutAsync);
        t.Concurrent("starcloud.login", HandleStarcloudLoginAsync);
        t.Concurrent("starcloud.sendCode", HandleStarcloudSendCodeAsync);
        t.Concurrent("starcloud.register", HandleStarcloudRegisterAsync);

        // ── 设置 ────────────────────────────────────────────────────────────
        t.Request("settings.get", HandleSettingsGetAsync);
        t.Request("settings.apply", HandleSettingsApplyAsync);
        t.Request("settings.clearCache", HandleSettingsClearCacheAsync);
        t.Concurrent("settings.checkUpdate", HandleSettingsCheckUpdateAsync);

        // ── 好友 / 群组 ─────────────────────────────────────────────────────
        t.Request("friends.acceptRequest", HandleFriendsAcceptRequestAsync);
        t.Request("friends.rejectRequest", HandleFriendsRejectRequestAsync);
        t.Request("friends.cancelRequest", HandleFriendsCancelRequestAsync);
        t.Request("friends.remove", HandleFriendsRemoveAsync);
        t.Request("friends.acceptInvite", HandleFriendsAcceptInviteAsync);
        t.Request("friends.declineInvite", HandleFriendsDeclineInviteAsync);
        t.Request("friends.cancelInvite", HandleFriendsCancelInviteAsync);
        t.Request("friends.groups", HandleFriendsGroupsAsync);
        t.Request("friends.groupDetail", HandleFriendsGroupDetailAsync);
        t.Request("friends.groupMessages", HandleFriendsGroupMessagesAsync);
        t.Request("friends.groupSend", HandleFriendsGroupSendAsync);
        t.Request("friends.groupMarkRead", HandleFriendsGroupMarkReadAsync);
        t.Request("friends.groupCreate", HandleFriendsGroupCreateAsync);
        t.Request("friends.groupAddMembers", HandleFriendsGroupAddMembersAsync);
        t.Request("friends.groupDissolve", HandleFriendsGroupDissolveAsync);
        t.Request("friends.uploadFile", HandleFriendsUploadFileAsync);
        t.Request("friends.downloadFile", HandleFriendsDownloadFileAsync);
        t.Concurrent("friends.list", HandleFriendsListAsync);
        t.Concurrent("friends.requests", HandleFriendsRequestsAsync);
        t.Concurrent("friends.search", HandleFriendsSearchAsync);
        t.Concurrent("friends.sendRequest", HandleFriendsSendRequestAsync);
        t.Concurrent("friends.invites", HandleFriendsInvitesAsync);
        t.Concurrent("friends.createInvite", HandleFriendsCreateInviteAsync);
        t.Concurrent("friends.conversation", HandleFriendsConversationAsync);
        t.Concurrent("friends.send", HandleFriendsSendAsync);
        t.Concurrent("friends.markRead", HandleFriendsMarkReadAsync);

        return t;
    }

    /// <summary>
    /// 方法表自省：壳端与 CI 用它核对「TS 侧声明的方法」与「C# 侧实现的方法」是否一致，
    /// 不必再靠人工比对 141 行 switch。
    /// </summary>
    private static async Task<object?> HandleBridgeMethodsAsync(TextWriter stdout, JsonElement p)
    {
        await Task.CompletedTask;
        return new
        {
            apiVersion = "bridge.1",
            count = Methods.Count,
            methods = Methods.All.Select(m => new
            {
                m.Name,
                kind = m.Kind switch
                {
                    BridgeMethodKind.Request => "request",
                    BridgeMethodKind.Concurrent => "concurrent",
                    _ => "streaming",
                },
            }),
        };
    }
}
