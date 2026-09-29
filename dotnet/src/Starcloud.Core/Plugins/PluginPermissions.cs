// 插件权限白名单 / 黑名单 + 身份参数拦截（规范 §2 §4）
// 安全不变量：插件无权修改游戏认证、UUID、登录校验逻辑；
// 身份相关权限从接口层面锁死（本体不暴露对应钩子），身份相关命令行参数直接拦截丢弃。
//
// 本文件同时承载 2.0 的细粒度能力模型（规范 2.0 §5）：白名单制、带参数、风险分级；
// 1.x 的 24 项粗粒度权限常量保持原样，两套语法按 manifest.apiVersion 分派。

using System.Text.RegularExpressions;

namespace Starcloud.Core.Plugins;

public static class PluginPermissions
{
    // ── ✅ 允许开放给插件的能力（白名单） ──

    //  基础（v1.0 起）
    public const string GameArguments = "launcher:game_arguments"; // 追加游戏 JVM/启动参数（仅增量）
    public const string UiPanel = "launcher:ui_panel";             // 新增 UI 面板 / 挂载区 / 确认框
    public const string Notification = "launcher:notification";    // 弹窗通知
    public const string FileAccess = "launcher:file_access";       // 读写插件自身 data 目录

    //  实例（v1.1 起）：读 = 查清单/详情/文件；写 = 安装/复制/改名/删除/补全
    public const string InstancesRead = "launcher:instances_read";
    public const string InstancesWrite = "launcher:instances_write";

    //  游戏进程：按当前账户启动实例（身份仍由本体解析，插件拿不到凭证）
    public const string GameLaunch = "launcher:game_launch";

    //  社区资源：读 = 搜索/版本列表/已装清单；写 = 安装/删除实例内的 mod 等
    public const string ContentRead = "launcher:content_read";
    public const string ContentWrite = "launcher:content_write";

    //  下载与网络：download = 走本体下载引擎写文件；network = 插件自己的 HTTP 请求
    public const string Download = "launcher:download";
    public const string Network = "launcher:network";

    //  设置 / 账户（只读身份，永不暴露令牌）/ 日志读取
    public const string SettingsRead = "launcher:settings_read";
    public const string SettingsWrite = "launcher:settings_write";
    public const string AccountsRead = "launcher:accounts_read";
    public const string LogsRead = "launcher:logs_read";

    //  存档与备份：读 = 存档/备份清单；写 = 备份/还原/删除/导出整合包
    public const string SavesRead = "launcher:saves_read";
    public const string SavesWrite = "launcher:saves_write";

    //  联机：内置陶瓦联机与第三方联机 SDK（开房 / 加入 / 结束 / 装核心）
    public const string Multiplayer = "launcher:multiplayer";

    //  外观：主题 / 背景 / 毛玻璃遮罩
    public const string Appearance = "launcher:appearance";

    //  自更新：只允许查询（安装更新由用户自己在设置里点）
    public const string Updates = "launcher:updates";

    //  宿主交互：事件订阅 / 剪贴板 / 打开链接与目录 / 插件私有键值存储 / 任务中心
    public const string Events = "launcher:events";
    public const string Clipboard = "launcher:clipboard";
    public const string Shell = "launcher:shell";
    public const string Storage = "launcher:storage";
    public const string Tasks = "launcher:tasks";

    /// <summary>允许声明的权限全集</summary>
    public static readonly string[] Allowed =
    [
        GameArguments, UiPanel, Notification, FileAccess,
        InstancesRead, InstancesWrite, GameLaunch,
        ContentRead, ContentWrite, Download, Network,
        SettingsRead, SettingsWrite, AccountsRead, LogsRead,
        SavesRead, SavesWrite, Multiplayer, Appearance, Updates,
        Events, Clipboard, Shell, Storage, Tasks,
    ];

    // ── ❌ 禁止开放给插件的权限（黑名单，出现即拒绝加载） ──
    public static readonly string[] Forbidden =
    [
        "launcher:account_verify", // 账号身份校验
        "launcher:auth_override",  // 认证流程接管
        "launcher:uuid_override",  // UUID 覆写
    ];

    // ── ⛔ 本体自管、不开放给插件的能力（不在白名单 = 声明即被拒，这里写明理由） ──
    // · 插件管理（启用 / 停用 / 安装 / 卸载其他插件）：插件不应决定同类的生死，
    //   否则一个插件就能把用户依赖的插件停掉，或把自己装成"被信任的"那一份。
    // · 启动器自更新安装：更新包会覆盖本体二进制，只能由用户在设置页手动点。
    // · 账户写操作（移除账户 / 改默认账户）：身份相关操作一律留在本体界面里。

    /// <summary>未知权限（不在白名单也不在黑名单）是否拒绝：白名单制，一律拒绝</summary>
    public static bool IsAllowed(string permission) => Allowed.Contains(permission);

    public static bool IsForbidden(string permission) => Forbidden.Contains(permission);

    // ── 身份相关命令行参数键（第二道防线：插件 append 的参数命中即丢弃） ──
    // 覆盖玩家名 / UUID / 访问令牌 / 用户类型 / 客户端标识 / 外置认证注入等核心身份校验参数
    private static readonly string[] IdentityArgKeys =
    [
        "--username", "--uuid", "--accessToken", "--userType", "--userProperties",
        "--clientId", "--clientid", "--xuid", "--authlib-injector",
        "--authServer", "--authserver",
    ];

    /// <summary>
    /// 判定插件想追加的参数是否命中身份校验类黑名单（忽略大小写）。
    /// 支持 「--key value」 的 key 形式与 「--key=value」 形式。
    /// </summary>
    public static bool IsIdentityArg(string arg, out string matchedKey)
    {
        matchedKey = "";
        if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith('-')) return false;
        var token = arg.Trim();
        // --key=value 拆出 key；裸 --key 或 「--key value」（空格分隔取值）取第一个空白段比对
        var eq = token.IndexOf('=');
        var key = eq > 0 ? token[..eq] : token.Split(' ', '\t')[0];
        foreach (var banned in IdentityArgKeys)
        {
            if (!string.Equals(key, banned, StringComparison.OrdinalIgnoreCase)) continue;
            matchedKey = banned;
            return true;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════════
    //  2.0 细粒度能力（规范 2.0 §5，草案）
    //  语法：<namespace>:<verb> 或 <namespace>:<verb>(k=v, flag)
    //        另接受省写形式 <namespace>:<key>=<value>（如 network:host=api.example.com、
    //        storage:quota=2mb），解析为对应的 verb + 参数。
    //  白名单制：未知 namespace / verb / 参数名、超范围取值一律拒绝加载。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>私有存储默认配额（KB），规范 2.0 §5.3</summary>
    public const int DefaultStorageQuotaKb = 256;

    /// <summary>私有存储配额上限（KB），规范 2.0 §5.3</summary>
    public const int MaxStorageQuotaKb = 32 * 1024;

    /// <summary>单个 verb 的规格：基础风险、可用参数、必填参数、触发高风险升级的参数</summary>
    private sealed record VerbSpec(
        string Name, PluginRiskLevel Risk, string[] Params, string[] Required, string[] HighRiskParams);

    /// <summary>2.0 能力目录（namespace → verb 全集）；不在表内即拒绝</summary>
    private static readonly Dictionary<string, VerbSpec[]> CapabilityCatalog =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["instances"] =
            [
                new("read", PluginRiskLevel.Low, [], [], []),
                new("write", PluginRiskLevel.Medium, ["allow_delete"], [], ["allow_delete"]),
            ],
            ["game"] =
            [
                new("arguments", PluginRiskLevel.Medium, [], [], []),
                new("launch", PluginRiskLevel.Medium, [], [], []),
                new("kill", PluginRiskLevel.High, [], [], []),
            ],
            ["content"] =
            [
                new("read", PluginRiskLevel.Low, [], [], []),
                new("write", PluginRiskLevel.Medium, [], [], []),
            ],
            ["download"] = [new("file", PluginRiskLevel.Low, [], [], [])],
            ["network"] =
            [
                new("host", PluginRiskLevel.Low, ["host"], ["host"], []),
                new("any", PluginRiskLevel.High, [], [], []),
            ],
            ["settings"] =
            [
                new("read", PluginRiskLevel.Low, [], [], []),
                new("write", PluginRiskLevel.High, [], [], []),
            ],
            ["accounts"] = [new("read", PluginRiskLevel.Low, [], [], [])],
            ["logs"] = [new("read", PluginRiskLevel.Low, [], [], [])],
            ["saves"] =
            [
                new("read", PluginRiskLevel.Low, [], [], []),
                new("write", PluginRiskLevel.Medium, ["allow_delete"], [], ["allow_delete"]),
            ],
            ["multiplayer"] = [new("use", PluginRiskLevel.Medium, [], [], [])],
            ["appearance"] = [new("write", PluginRiskLevel.Low, [], [], [])],
            ["updates"] = [new("read", PluginRiskLevel.Low, [], [], [])],
            ["events"] = [new("subscribe", PluginRiskLevel.Low, [], [], [])],
            ["clipboard"] =
            [
                new("read", PluginRiskLevel.Medium, [], [], []),
                new("write", PluginRiskLevel.Low, [], [], []),
            ],
            ["shell"] = [new("open", PluginRiskLevel.Medium, [], [], [])],
            ["storage"] = [new("kv", PluginRiskLevel.Low, ["quota"], [], [])],
            ["tasks"] =
            [
                new("read", PluginRiskLevel.Low, [], [], []),
                new("cancel", PluginRiskLevel.Low, [], [], []),
                new("spawn", PluginRiskLevel.Medium, [], [], []),
            ],
            ["ui"] =
            [
                new("panel", PluginRiskLevel.Low, [], [], []),
                new("slot", PluginRiskLevel.Low, [], [], []),
                new("notify", PluginRiskLevel.Low, [], [], []),
                new("html", PluginRiskLevel.High, [], [], []),
            ],
        };

    /// <summary>
    /// 解析一条 2.0 能力串。失败给出明确原因（未知命名空间 / verb / 参数、取值超范围）。
    /// </summary>
    public static bool TryParse(string raw, out PluginCapability capability, out string error)
    {
        capability = null!;
        error = "";
        var text = (raw ?? "").Trim();
        if (text.Length == 0) { error = "空权限串"; return false; }
        if (text.StartsWith("launcher:", StringComparison.OrdinalIgnoreCase))
        {
            error = $"2.0 权限语法已变更，不再接受 1.x 写法：{text}";
            return false;
        }

        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            error = $"权限串格式非法（应形如 instances:read）：{text}";
            return false;
        }
        var ns = text[..colon].Trim();
        var rest = text[(colon + 1)..].Trim();
        if (!CapabilityCatalog.TryGetValue(ns, out var specs))
        {
            error = $"未知权限命名空间：{ns}";
            return false;
        }

        string verb;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var open = rest.IndexOf('(');
        if (open >= 0)
        {
            if (!rest.EndsWith(')')) { error = $"权限串括号未闭合：{text}"; return false; }
            verb = rest[..open].Trim();
            var inner = rest[(open + 1)..^1];
            foreach (var part in inner.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                   StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) parameters[part] = "true";
                else parameters[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }
        else
        {
            var eq = rest.IndexOf('=');
            if (eq < 0)
            {
                verb = rest;
            }
            else
            {
                var key = rest[..eq].Trim();
                var value = rest[(eq + 1)..].Trim();
                if (specs.Any(s => Same(s.Name, key)))
                {
                    verb = key;
                    parameters[key] = value;
                }
                else
                {
                    var owner = specs.FirstOrDefault(s => s.Params.Any(p => Same(p, key)));
                    if (owner is null) { error = $"未知权限：{ns}:{key}"; return false; }
                    verb = owner.Name;
                    parameters[key] = value;
                }
            }
        }

        var spec = specs.FirstOrDefault(s => Same(s.Name, verb));
        if (spec is null) { error = $"未知权限：{ns}:{verb}"; return false; }

        foreach (var key in parameters.Keys)
            if (!spec.Params.Any(p => Same(p, key)))
            {
                error = $"权限 {ns}:{spec.Name} 不接受参数 {key}";
                return false;
            }
        foreach (var required in spec.Required)
            if (!parameters.TryGetValue(required, out var v) || string.IsNullOrWhiteSpace(v))
            {
                error = $"权限 {ns}:{spec.Name} 缺少参数 {required}";
                return false;
            }
        if (parameters.TryGetValue("host", out var host) && !IsValidHostPattern(host))
        {
            error = $"network:host 主机名非法：{host}";
            return false;
        }
        if (parameters.TryGetValue("quota", out var quota) && !TryParseQuota(quota, out _, out var quotaError))
        {
            error = quotaError;
            return false;
        }

        capability = new PluginCapability(ns.ToLowerInvariant(), spec.Name, parameters, text);
        return true;

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>批量解析；返回是否全部通过，错误逐条累积</summary>
    public static bool TryParseAll(IEnumerable<string> permissions,
        out List<PluginCapability> capabilities, out List<string> errors)
    {
        capabilities = [];
        errors = [];
        foreach (var raw in permissions)
        {
            if (TryParse(raw, out var cap, out var error)) capabilities.Add(cap);
            else errors.Add(error);
        }
        return errors.Count == 0;
    }

    /// <summary>能力风险等级（参数可升级为高风险，如 instances:write(allow_delete)）</summary>
    public static PluginRiskLevel RiskOf(PluginCapability capability)
    {
        var spec = CapabilityCatalog[capability.Namespace]
            .First(s => string.Equals(s.Name, capability.Verb, StringComparison.OrdinalIgnoreCase));
        return spec.HighRiskParams.Any(capability.HasParam) ? PluginRiskLevel.High : spec.Risk;
    }

    /// <summary>该能力是否需要宿主在首次调用时弹「高风险二次确认」（规范 2.0 §5.3）</summary>
    public static bool RequiresConfirmation(PluginCapability capability) =>
        RiskOf(capability) == PluginRiskLevel.High;

    /// <summary>解析配额字符串（256kb / 2mb / 1gb / 纯数字=kb）；超上限视为非法</summary>
    public static bool TryParseQuota(string text, out int kb, out string error)
    {
        kb = DefaultStorageQuotaKb;
        error = "";
        var t = (text ?? "").Trim().ToLowerInvariant();
        var multiplier = 1;
        if (t.EndsWith("kb")) t = t[..^2];
        else if (t.EndsWith("mb")) { t = t[..^2]; multiplier = 1024; }
        else if (t.EndsWith("gb")) { t = t[..^2]; multiplier = 1024 * 1024; }
        if (!int.TryParse(t, out var value) || value <= 0)
        {
            error = $"storage 配额非法：{text}（应形如 256kb / 2mb）";
            return false;
        }
        var total = (long)value * multiplier;
        if (total > MaxStorageQuotaKb)
        {
            error = $"storage 配额超上限 {MaxStorageQuotaKb / 1024}mb：{text}";
            return false;
        }
        kb = (int)total;
        return true;
    }

    /// <summary>主机名 / 通配主机名（*.example.com）合法性：不接受协议头、路径、空白</summary>
    private static bool IsValidHostPattern(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;
        if (host.Contains("://") || host.Contains('/') || host.Any(char.IsWhiteSpace)) return false;
        var bare = host.StartsWith("*.", StringComparison.Ordinal) ? host[2..] : host;
        return bare.Length > 0 && Regex.IsMatch(bare, @"^[A-Za-z0-9]([A-Za-z0-9\-.]*[A-Za-z0-9])?$");
    }

    // ════════════════════════════════════════════════════════════════════
    //  2.0 → 1.x 能力桥接（规范 2.0 §9「双版本共存」）
    //  原则：只放行"不放大权限"的能力。若 2.0 的细粒度限制尚未强制（删除闸门、
    //  高风险二次确认）或能力本身还没实现（HTML 面板、ctx.tasks.spawn），
    //  一律**不给**（fail-closed），并在 Deferred 里记明原因，由宿主写日志。
    //
    //  已带强制的能力：
    //    · network:host= / network:any  → 主机白名单（PluginApiCore.EnsureHostAllowed）
    //    · storage:kv(quota=)           → 存储配额（PluginStorageApi 写前校验）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>桥接结果：生效能力 + 需要随能力一起下发的限制 + 暂未开放的声明</summary>
    public sealed class Api2Grant
    {
        /// <summary>生效的 1.x 能力常量（ctx 按它装配）</summary>
        public IReadOnlyList<string> Capabilities { get; init; } = [];

        /// <summary>生效能力的原始 2.0 串（供插件页 / 日志展示）</summary>
        public IReadOnlyList<string> Granted { get; init; } = [];

        /// <summary>私有存储配额（KB）；null = 未声明 storage:kv</summary>
        public int? StorageQuotaKb { get; init; }

        /// <summary>网络主机白名单；null = 未声明 network，["*"] = network:any</summary>
        public string[]? AllowedHosts { get; init; }

        /// <summary>声明了但本次不放行的能力（含原因），宿主应记入日志</summary>
        public IReadOnlyList<string> Deferred { get; init; } = [];
    }

    /// <summary>暂未开放的 2.0 能力 → 原因（未列出的按"暂不支持"处理）</summary>
    private static readonly Dictionary<string, string> DeferredReasons =
        new(StringComparer.Ordinal)
        {
            ["instances:write"] = "删除闸门（allow_delete 需运行时二次确认）尚未实现",
            ["saves:write"] = "删除闸门（allow_delete 需运行时二次确认）尚未实现",
            ["game:kill"] = "需要运行时二次确认，尚未实现",
            ["settings:write"] = "需要运行时二次确认，尚未实现",
            ["ui:html"] = "HTML 面板模式尚未实现",
            ["tasks:spawn"] = "后台作业 ctx.tasks.spawn 尚未实现",
        };

    /// <summary>把 2.0 能力串桥接成 1.x 能力集合（见上方原则）</summary>
    public static Api2Grant BridgeApi2(IEnumerable<string> permissions)
    {
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        var granted = new List<string>();
        var deferred = new List<string>();
        var hosts = new List<string>();
        var anyHost = false;
        int? quota = null;

        void Grant(string raw, string capability)
        {
            capabilities.Add(capability);
            granted.Add(raw);
        }

        foreach (var raw in permissions)
        {
            if (!TryParse(raw, out var cap, out var error))
            {
                deferred.Add($"{raw}（{error}）");
                continue;
            }
            switch ($"{cap.Namespace}:{cap.Verb}")
            {
                case "ui:panel": Grant(raw, UiPanel); break;
                case "ui:notify": Grant(raw, Notification); break;
                case "events:subscribe": Grant(raw, Events); break;
                case "content:read": Grant(raw, ContentRead); break;
                case "content:write": Grant(raw, ContentWrite); break;
                case "download:file": Grant(raw, Download); break;
                case "settings:read": Grant(raw, SettingsRead); break;
                case "accounts:read": Grant(raw, AccountsRead); break;
                case "logs:read": Grant(raw, LogsRead); break;
                case "instances:read": Grant(raw, InstancesRead); break;
                case "saves:read": Grant(raw, SavesRead); break;
                case "tasks:read":
                case "tasks:cancel": Grant(raw, Tasks); break;
                case "appearance:write": Grant(raw, Appearance); break;
                case "updates:read": Grant(raw, Updates); break;
                case "clipboard:read":
                case "clipboard:write": Grant(raw, Clipboard); break;
                case "shell:open": Grant(raw, Shell); break;
                case "multiplayer:use": Grant(raw, Multiplayer); break;
                case "game:arguments": Grant(raw, GameArguments); break;
                case "game:launch": Grant(raw, GameLaunch); break;
                case "storage:kv":
                    Grant(raw, Storage);
                    quota = cap.Parameters.TryGetValue("quota", out var q) &&
                            TryParseQuota(q, out var kb, out _)
                        ? kb
                        : DefaultStorageQuotaKb;
                    break;
                case "network:any":
                    Grant(raw, Network);
                    anyHost = true;
                    break;
                case "network:host":
                    Grant(raw, Network);
                    if (cap.Parameters.TryGetValue("host", out var host) && host.Length > 0)
                        hosts.Add(host);
                    break;
                default:
                    deferred.Add($"{raw}（{DeferredReasons.GetValueOrDefault(
                        $"{cap.Namespace}:{cap.Verb}", "暂不支持该能力")}）");
                    break;
            }
        }

        return new Api2Grant
        {
            Capabilities = [.. capabilities],
            Granted = granted,
            StorageQuotaKb = quota,
            AllowedHosts = anyHost ? ["*"] : hosts.Count > 0 ? [.. hosts] : null,
            Deferred = deferred,
        };
    }
}

/// <summary>2.0 能力风险等级（规范 2.0 §5.2）</summary>
public enum PluginRiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>一条解析通过的 2.0 细粒度能力</summary>
public sealed record PluginCapability(
    string Namespace, string Verb,
    IReadOnlyDictionary<string, string> Parameters, string Raw)
{
    public bool HasParam(string name) => Parameters.ContainsKey(name);

    public override string ToString() => Raw;
}
