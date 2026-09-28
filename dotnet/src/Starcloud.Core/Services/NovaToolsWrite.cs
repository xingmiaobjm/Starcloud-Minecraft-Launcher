// Nova 工具 · 写操作部分：装版本 / 装资源 / 改设置 / 启动游戏 / 备份 / 联机 / 录屏…
// 这些工具**不会被直接执行**：NovaAgentLoop 只拿它们的参数攒成 NovaPlan 确认清单，
// 用户在气泡里点头之后才真正跑（信任边界的第二层）。

using System.Text.Json;

namespace Starcloud.Core.Services;

public sealed partial class NovaToolRegistry
{
    private IEnumerable<NovaTool> BuildWriteTools()
    {
        // ── 版本 / 加载器 ──
        yield return new("install_minecraft", "安装原版",
            "安装一个 Minecraft 原版版本。版本号必须来自 list_versions 的官方列表。",
            """{"type":"object","properties":{"version_id":{"type":"string","description":"官方版本 ID，如 1.21.1"}},"required":["version_id"]}""",
            false, InstallMinecraftAsync);

        yield return new("install_loader", "安装加载器",
            "给指定 MC 版本安装 Forge/NeoForge/Fabric/Quilt。缺省构建号时自动选推荐版；Forge/NeoForge 会先补装原版。",
            """{"type":"object","properties":{"loader":{"type":"string","enum":["forge","neoforge","fabric","quilt"]},"mc_version":{"type":"string","description":"MC 版本，如 1.21.1"},"build":{"type":"string","description":"加载器构建号，缺省自动选推荐/最新稳定版"}},"required":["loader","mc_version"]}""",
            false, InstallLoaderAsync);

        yield return new("delete_version", "删除实例",
            "删除一个已安装实例（含它的独立 mods/saves/config 等目录与实例设置）。不可撤销，删前建议先 backup_version。",
            """{"type":"object","properties":{"version_id":{"type":"string"}},"required":["version_id"]}""",
            false, DeleteVersionAsync);

        yield return new("rename_version", "重命名实例",
            "把实例 ID 改成新的 ID（同时迁移目录与实例设置）。",
            """{"type":"object","properties":{"version_id":{"type":"string"},"new_id":{"type":"string"}},"required":["version_id","new_id"]}""",
            false, RenameVersionAsync);

        yield return new("launch_game", "启动游戏",
            "启动一个实例：先补全缺失文件再拉起。同一 .minecraft 下已有游戏在跑时会被拒绝。",
            """{"type":"object","properties":{"version_id":{"type":"string","description":"缺省用当前选中版本"}}}""",
            false, LaunchGameAsync);

        yield return new("repair_instance", "修复实例",
            "补全某实例缺失 / 损坏的文件（版本 JSON、client jar、依赖父版本、资源索引）。",
            """{"type":"object","properties":{"version_id":{"type":"string","description":"缺省用当前选中版本"}}}""",
            false, RepairInstanceAsync);

        yield return new("resolve_mod_conflicts", "处理模组冲突",
            "自动处理可判定的模组冲突：重复 modId 只留最新，其余停用为 .jar.disabled（不删文件）。",
            """{"type":"object","properties":{}}""",
            false, (_, ct) => _c.Conflicts is null
                ? Task.FromResult("当前宿主没有冲突检测服务。")
                : _c.Conflicts.ResolveAsync(DotMc, ct));

        // ── 资源 ──
        yield return new("install_content", "安装资源",
            "把 Modrinth / CurseForge 的资源（mod/资源包/光影/数据包）下载并装载到指定实例。project_id 来自搜索结果。",
            """{"type":"object","properties":{"project_id":{"type":"string","description":"项目 ID 或 slug"},"source":{"type":"string","enum":["modrinth","curseforge"],"description":"来源，默认 modrinth"},"kind":{"type":"string","enum":["mod","resourcepack","shader","datapack"],"description":"资源类型，默认 mod"},"version_number":{"type":"string","description":"指定资源版本号，缺省取最新兼容版"},"target_version":{"type":"string","description":"装载到的实例 ID，缺省为当前选中版本"}},"required":["project_id"]}""",
            false, InstallContentAsync);

        yield return new("install_modpack", "安装整合包",
            "安装本地整合包文件（.mrpack 或 CurseForge 导出 zip）：新建隔离实例、装好原版与加载器、导入包内文件。",
            """{"type":"object","properties":{"path":{"type":"string","description":"整合包文件的绝对路径"},"name":{"type":"string","description":"实例名，缺省用包内名字"},"target_version":{"type":"string","description":"目标 MC 版本，缺省用包内声明的版本"}},"required":["path"]}""",
            false, InstallModpackAsync);

        yield return new("enable_content", "装载库内资源",
            "把资源库里已有的文件装载到某个实例（不重新下载）。file_name 来自 list_content_library。",
            """{"type":"object","properties":{"file_name":{"type":"string"},"kind":{"type":"string","enum":["mod","resourcepack","shader","datapack","modpack"],"description":"默认 mod"},"target_version":{"type":"string","description":"缺省为当前选中版本"}},"required":["file_name"]}""",
            false, EnableContentAsync);

        yield return new("remove_from_library", "从资源库删除",
            "从资源库删除一份文件（已装载到实例的副本不受影响）。",
            """{"type":"object","properties":{"file_name":{"type":"string"},"kind":{"type":"string","description":"默认 mod"}},"required":["file_name"]}""",
            false, RemoveFromLibraryAsync);

        yield return new("export_modpack", "导出整合包",
            "把某个实例导出成可分享的 .mrpack，返回产物路径。",
            """{"type":"object","properties":{"version_id":{"type":"string","description":"缺省为当前选中版本"},"name":{"type":"string","description":"包名，缺省用实例名"}}}""",
            false, ExportModpackAsync);

        // ── 存档 / 备份 ──
        yield return new("backup_version", "备份实例",
            "备份一个实例（版本目录 + mods/config，可选含存档），返回备份 id。",
            """{"type":"object","properties":{"version_id":{"type":"string","description":"缺省为当前选中版本"},"name":{"type":"string"},"include_saves":{"type":"boolean","description":"是否包含存档，默认 true"}},"required":["version_id"]}""",
            false, BackupVersionAsync);

        yield return new("restore_backup", "还原备份",
            "把备份还原回它的实例（覆盖同名文件）。id 来自 list_backups。",
            """{"type":"object","properties":{"backup_id":{"type":"string"}},"required":["backup_id"]}""",
            false, RestoreBackupAsync);

        yield return new("delete_backup", "删除备份",
            "删除一条备份记录与它的 zip 文件。",
            """{"type":"object","properties":{"backup_id":{"type":"string"}},"required":["backup_id"]}""",
            false, DeleteBackupAsync);

        yield return new("backup_save", "备份存档",
            "把一个存档目录打包备份，返回备份文件路径。",
            """{"type":"object","properties":{"version_id":{"type":"string","description":"存档所属实例，缺省为当前选中版本"},"save_name":{"type":"string","description":"存档文件夹名，来自 list_saves"}},"required":["save_name"]}""",
            false, BackupSaveAsync);

        yield return new("delete_save", "删除存档",
            "删除一个存档目录（建议先 backup_save）。",
            """{"type":"object","properties":{"version_id":{"type":"string"},"save_name":{"type":"string"}},"required":["save_name"]}""",
            false, DeleteSaveAsync);

        // ── 设置 / Java ──
        yield return new("update_settings", "修改启动器设置",
            "修改全局设置（白名单字段）。只改用户明确同意的项。",
            """{"type":"object","properties":{"download_source":{"type":"string","enum":["auto","official","bmclapi","fastmirror","rainyun","custom"],"description":"下载源"},"default_min_memory_mb":{"type":"integer"},"default_max_memory_mb":{"type":"integer"},"last_version":{"type":"string","description":"切换当前选中的实例"},"version_isolated":{"type":"boolean","description":"全局版本隔离默认值"},"java_executable":{"type":"string","description":"Java 路径；传 auto 表示自动选择"},"theme":{"type":"string","enum":["light","dark","system"]},"language":{"type":"string","description":"游戏语言，如 zh_CN / en_us"},"auto_update_enabled":{"type":"boolean"},"launch_auto_retry":{"type":"boolean","description":"启动失败自动重试一次"},"show_game_log_on_launch":{"type":"boolean"},"background_mode":{"type":"string","enum":["none","builtin","file"],"description":"背景模式；file = 用自定义图片"},"background_image_opacity":{"type":"number","description":"背景不透明度 0.2~1"},"background_blur_radius":{"type":"integer","description":"背景模糊半径 0~64"},"extra_jvm_args":{"type":"array","items":{"type":"string"},"description":"全局附加 JVM 参数（整体替换）"},"global_env_vars":{"type":"object","description":"全局环境变量（整体替换），值为字符串"}}}""",
            false, UpdateSettingsAsync);

        yield return new("update_instance_settings", "修改实例设置",
            "修改某个实例的覆盖设置：内存/Java/窗口档位、自动连服、隔离、附加参数、启动前后命令、备注。",
            """{"type":"object","properties":{"version_id":{"type":"string"},"memory_mode":{"type":"string","enum":["global","auto","manual"]},"memory_max_mb":{"type":"integer"},"memory_min_mb":{"type":"integer"},"java_mode":{"type":"string","enum":["global","auto","manual"]},"java_path":{"type":"string","description":"java_mode=manual 时使用"},"window_mode":{"type":"string","enum":["global","auto","manual"]},"window_width":{"type":"integer"},"window_height":{"type":"integer"},"fullscreen":{"type":"boolean"},"isolation":{"type":"boolean","description":"是否版本隔离"},"server_address":{"type":"string","description":"自动连服地址"},"clear_server_address":{"type":"boolean"},"language_override":{"type":"string"},"extra_jvm_args":{"type":"array","items":{"type":"string"}},"pre_launch_command":{"type":"string"},"post_launch_command":{"type":"string"},"note":{"type":"string"}},"required":["version_id"]}""",
            false, UpdateInstanceSettingsAsync);

        yield return new("download_java", "下载 Java",
            "下载并安装一个 Adoptium Java 运行时（按主版本，如 8 / 17 / 21）。",
            """{"type":"object","properties":{"major_version":{"type":"integer","description":"Java 主版本，如 8、11、17、21"}},"required":["major_version"]}""",
            false, DownloadJavaAsync);

        // ── 任务 / 通知 / 账户 ──
        yield return new("cancel_task", "取消下载任务",
            "取消任务中心里正在跑的任务；不传 task_id 或 all=true 时取消全部进行中的任务。",
            """{"type":"object","properties":{"task_id":{"type":"string","description":"缺省取消全部进行中任务，id 来自 list_tasks"},"all":{"type":"boolean","description":"true = 取消全部进行中任务"}}}""",
            false, CancelTaskAsync);

        yield return new("clear_tasks", "清空任务记录",
            "清空任务中心列表（先取消进行中的任务，再清掉已完成/失败的记录）。",
            """{"type":"object","properties":{}}""",
            false, ClearTasksAsync);

        yield return new("clear_notifications", "清空通知",
            "清空启动器通知列表（错误 / 警告 / 提示）。",
            """{"type":"object","properties":{}}""",
            false, (_, _) =>
            {
                var n = NotificationCenter.Instance.Clear();
                return Task.FromResult(n == 0 ? "通知列表本来就是空的。" : $"已清空 {n} 条通知。");
            });

        yield return new("set_default_account", "切换默认账户",
            "把某个已添加的账户设为默认（启动游戏时用它）。uuid 来自 list_accounts。",
            """{"type":"object","properties":{"uuid":{"type":"string"}},"required":["uuid"]}""",
            false, SetDefaultAccountAsync);

        yield return new("remove_account", "移除游戏账户",
            "从启动器里移除一个已保存的账户（不会注销上游账号）。uuid 来自 list_accounts。",
            """{"type":"object","properties":{"uuid":{"type":"string"}},"required":["uuid"]}""",
            false, RemoveAccountAsync);

        // ── 启动器自更新 ──
        yield return new("install_update", "安装启动器更新",
            "下载最新版启动器并安装：准备完成后启动器会自动关闭重启。",
            """{"type":"object","properties":{}}""",
            false, InstallUpdateAsync);

        // ── 联机（宿主提供时才有） ──
        if (_c.Multiplayer is not null)
        {
            yield return new("install_multiplayer_core", "安装联机核心",
                "下载并安装当前提供方的联机核心（首次联机前需要）。",
                """{"type":"object","properties":{}}""",
                false, async (_, ct) =>
                {
                    await _c.Multiplayer!.InstallCoreAsync(null, ct);
                    return "联机核心已安装。";
                });

            yield return new("multiplayer_host", "开房（房主）",
                "创建联机房间（需先在游戏里「对局域网开放」）。成功后返回邀请码。",
                """{"type":"object","properties":{}}""",
                false, async (_, ct) =>
                {
                    await _c.Multiplayer!.HostAsync(ct);
                    await _c.Multiplayer.RefreshAsync(ct);
                    var room = _c.Multiplayer.Snapshot?.State.Room;
                    return room is { Length: > 0 }
                        ? $"房间已创建，邀请码：{room}。把它发给好友。"
                        : "已发起开房，房间还在启动中（游戏里先确认已「对局域网开放」）。";
                });

            yield return new("multiplayer_join", "加入房间",
                "用邀请码加入好友的房间。",
                """{"type":"object","properties":{"room":{"type":"string","description":"邀请码"}},"required":["room"]}""",
                false, async (args, ct) =>
                {
                    var room = Require(Str(args, "room"), "room");
                    await _c.Multiplayer!.JoinAsync(room, ct);
                    await _c.Multiplayer.RefreshAsync(ct);
                    return $"已加入房间 {room}；状态变成「已进入虚拟局域网」后在游戏里进房主的世界。";
                });

            yield return new("multiplayer_stop", "结束联机",
                "结束当前联机会话：idle_only=true 只把核心切回空闲，false 则断开房间。",
                """{"type":"object","properties":{"idle_only":{"type":"boolean"}}}""",
                false, async (args, ct) =>
                {
                    if (Bool(args, "idle_only") == true)
                    {
                        await _c.Multiplayer!.IdleAsync(ct);
                        return "联机核心已切回空闲（房间保持）。";
                    }
                    await _c.Multiplayer!.StopAsync(ct);
                    return "联机会话已结束，房间里的玩家会被断开。";
                });
        }

        // ── 录屏 / 插件 / 外观（宿主提供时才有） ──
        if (_c.Host is not null)
        {
            yield return new("start_recording", "开始录屏",
                "开始硬件录屏（需要用户已授权录屏许可），录到本地磁盘。",
                """{"type":"object","properties":{}}""",
                false, async (_, ct) => await _c.Host!.StartRecordingAsync(ct));

            yield return new("stop_recording", "停止录屏",
                "停止录屏并返回录像文件路径。",
                """{"type":"object","properties":{}}""",
                false, async (_, ct) => await _c.Host!.StopRecordingAsync(ct));

            yield return new("set_plugin_enabled", "启停插件",
                "启用或停用一个已安装插件。id 来自 list_plugins。",
                """{"type":"object","properties":{"id":{"type":"string"},"enabled":{"type":"boolean"}},"required":["id","enabled"]}""",
                false, (args, _) => Task.FromResult(_c.Host!.SetPluginEnabled(
                    Require(Str(args, "id"), "id"), Bool(args, "enabled") ?? true)));

            yield return new("set_appearance", "修改外观",
                "修改启动器外观：主题、背景模式、背景图、遮罩透明度、模糊半径。只传要改的项。",
                """{"type":"object","properties":{"theme":{"type":"string","enum":["light","dark","system"]},"background_mode":{"type":"string","enum":["none","builtin","file"]},"background_image_path":{"type":"string"},"opacity":{"type":"number","description":"背景不透明度 0.2~1"},"blur_radius":{"type":"integer","description":"模糊半径 0~64"}}}""",
                false, (args, _) => Task.FromResult(_c.Host!.SetAppearance(
                    Str(args, "theme"), Str(args, "background_mode"), Str(args, "background_image_path"),
                    Num(args, "opacity"), Int(args, "blur_radius"))));

            yield return new("install_plugin", "安装插件",
                "从本地 .mclplug 文件安装插件（同 id 覆盖旧版，装完默认停用）。",
                """{"type":"object","properties":{"path":{"type":"string","description":".mclplug 文件的绝对路径"}},"required":["path"]}""",
                false, (args, _) => Task.FromResult(_c.Host!.InstallPlugin(
                    Require(Str(args, "path"), "path"))));

            yield return new("uninstall_plugin", "卸载插件",
                "卸载一个已安装插件（删目录 + 清状态）。id 来自 list_plugins。",
                """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""",
                false, (args, _) => Task.FromResult(_c.Host!.UninstallPlugin(
                    Require(Str(args, "id"), "id"))));

            // 只把界面切过去，不改任何配置：立即执行，不需要用户点头（登录等手动步骤靠它引导）
            yield return new("open_page", "打开启动器页面",
                "把启动器界面切到指定页面，用于引导用户做只有界面能完成的事（如登录账户、看皮肤）。",
                """{"type":"object","properties":{"page":{"type":"string","enum":["home","versions","download","saves","multiplayer","accounts","plugins","ai","logs","settings"]}},"required":["page"]}""",
                true, (args, _) => Task.FromResult(_c.Host!.OpenPage(
                    Require(Str(args, "page"), "page"))));
        }
    }
}
