//! 多下载源：Mojang 官方 + 国内镜像（BMCLAPI / FastMinecraftMirror）。
//!
//! - 仅重写 Mojang 相关域名（meta / data / 资源 / Maven），Modrinth、CurseForge、
//!   Fabric/Forge 官方等其余地址原样放行；
//! - 启动时后台测速（可达性 + 首字节延迟 + 实测速度），自动切换最快的可达源；
//! - 测速结果全局缓存，供 GUI 命令查询展示。

use futures_util::StreamExt;
use reqwest::header::RANGE;
use std::collections::HashMap;
use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant};

/// 官方版本清单地址（同时作为测速探针目标：各源改写后下载同一文件比较速度）
pub const MOJANG_MANIFEST_URL: &str =
    "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

/// 下载源标识
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SourceKind {
    /// Mojang 官方
    Official,
    /// BMCLAPI（bangbang93，国内最常用的 Minecraft 镜像）
    Bmclapi,
    /// FastMinecraftMirror（无极镜像，MCBBS 关闭后的开源替代）
    Fastmirror,
    /// RainYun 雨云镜像（综合镜像源）
    Rainyun,
    /// 用户自定义镜像 URL
    Custom,
}

impl SourceKind {
    /// 稳定 ID（供前端展示/匹配）
    pub fn id(self) -> &'static str {
        match self {
            SourceKind::Official => "official",
            SourceKind::Bmclapi => "bmclapi",
            SourceKind::Fastmirror => "fastmirror",
            SourceKind::Rainyun => "rainyun",
            SourceKind::Custom => "custom",
        }
    }

    /// 显示名
    pub fn name(self) -> &'static str {
        match self {
            SourceKind::Official => "Mojang 官方",
            SourceKind::Bmclapi => "BMCLAPI",
            SourceKind::Fastmirror => "FastMinecraftMirror",
            SourceKind::Rainyun => "RainYun 雨云",
            SourceKind::Custom => "自定义镜像",
        }
    }

    /// 从稳定 ID 解析下载源（未知 ID 返回 None）
    pub fn from_id(id: &str) -> Option<SourceKind> {
        match id {
            "official" => Some(SourceKind::Official),
            "bmclapi" => Some(SourceKind::Bmclapi),
            "fastmirror" => Some(SourceKind::Fastmirror),
            "rainyun" => Some(SourceKind::Rainyun),
            "custom" => Some(SourceKind::Custom),
            // 兼容旧 ID（已移除的源，回退为 auto）
            "mcslapi" | "mcslsync" => None,
            _ => None,
        }
    }

    /// 全部源（测速遍历顺序；Custom 仅在有自定义 URL 时参与）
    pub fn all() -> [SourceKind; 5] {
        [
            SourceKind::Official,
            SourceKind::Bmclapi,
            SourceKind::Fastmirror,
            SourceKind::Rainyun,
            SourceKind::Custom,
        ]
    }

    /// 可用于测速的源（Custom 仅在已设置 URL 时参与测速）
    pub fn probeable() -> Vec<SourceKind> {
        let mut sources = vec![
            SourceKind::Official,
            SourceKind::Bmclapi,
            SourceKind::Fastmirror,
            SourceKind::Rainyun,
        ];
        if custom_mirror_url().is_some() {
            sources.push(SourceKind::Custom);
        }
        sources
    }
}

/// 全局活动下载源（默认官方；启动测速完成后自动切换）
static ACTIVE_SOURCE: Mutex<Option<SourceKind>> = Mutex::new(None);
/// 最近一次测速结果（供 GUI 命令查询）
static PROBE_RESULTS: Mutex<Option<Vec<SourceProbe>>> = Mutex::new(None);
/// 用户自定义镜像根 URL（Custom 源使用）
static CUSTOM_MIRROR_URL: Mutex<Option<String>> = Mutex::new(None);

/// 设置自定义镜像 URL
pub fn set_custom_mirror_url(url: &str) {
    let trimmed = url.trim().trim_end_matches('/');
    if trimmed.is_empty() {
        *CUSTOM_MIRROR_URL.lock().unwrap() = None;
    } else {
        *CUSTOM_MIRROR_URL.lock().unwrap() = Some(trimmed.to_string());
    }
    log::info!("自定义镜像 URL 已设置: {:?}", custom_mirror_url());
}

/// 获取自定义镜像 URL
pub fn custom_mirror_url() -> Option<String> {
    CUSTOM_MIRROR_URL.lock().unwrap().clone()
}

// ---------------------------------------------------------------------------
// 源健康度追踪
// ---------------------------------------------------------------------------

/// 源健康状态
#[derive(Debug, Clone)]
struct SourceHealth {
    /// 连续失败次数
    consecutive_failures: u32,
    /// 是否被降级
    degraded: bool,
}

/// 全局源健康度状态
static SOURCE_HEALTH: LazyLock<Mutex<HashMap<SourceKind, SourceHealth>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

/// 记录源请求成功（重置失败计数、恢复降级）
pub fn record_source_success(kind: SourceKind) {
    let mut health = SOURCE_HEALTH.lock().unwrap();
    health.insert(
        kind,
        SourceHealth {
            consecutive_failures: 0,
            degraded: false,
        },
    );
}

/// 记录源请求失败，连续失败 ≥5 次自动降级
pub fn record_source_failure(kind: SourceKind) {
    let mut health = SOURCE_HEALTH.lock().unwrap();
    let entry = health.entry(kind).or_insert(SourceHealth {
        consecutive_failures: 0,
        degraded: false,
    });
    entry.consecutive_failures += 1;
    if entry.consecutive_failures >= 5 {
        if !entry.degraded {
            entry.degraded = true;
            log::warn!(
                "下载源 {} 已降级（连续失败 {} 次）",
                kind.name(),
                entry.consecutive_failures
            );
        }
    }
}

/// 获取按健康度排序的源列表（健康源优先 → 降级源 → 官方最后兜底）
pub fn healthy_sources() -> Vec<SourceKind> {
    let health = SOURCE_HEALTH.lock().unwrap();
    let sources = SourceKind::probeable();
    let mut sources: Vec<SourceKind> = sources.into_iter().collect();
    sources.sort_by_key(|k| {
        let degraded = health.get(k).map(|h| h.degraded).unwrap_or(false);
        if *k == SourceKind::Official {
            2 // 官方始终最后
        } else if degraded {
            1 // 降级源中间
        } else {
            0 // 健康源优先
        }
    });
    sources
}

/// 重置所有源健康度（刷新测速后调用）
pub fn reset_source_health() {
    SOURCE_HEALTH.lock().unwrap().clear();
}

/// 设置活动下载源
pub fn set_active_source(kind: SourceKind) {
    *ACTIVE_SOURCE.lock().unwrap() = Some(kind);
    log::info!("下载源已切换: {}", kind.name());
}

/// 当前活动下载源（未设置时默认官方）
pub fn active_source() -> SourceKind {
    ACTIVE_SOURCE
        .lock()
        .unwrap()
        .as_ref()
        .copied()
        .unwrap_or(SourceKind::Official)
}

/// 按当前活动下载源重写 URL（未命中 Mojang 域名时原样返回）
pub fn rewrite_url(url: &str) -> String {
    rewrite_url_for(active_source(), url)
}

/// 按指定下载源重写 Mojang 域名 URL
///
/// 域名映射（与 HMCL 等主流启动器一致的镜像规则）：
/// - BMCLAPI：单一镜像根；`piston-meta / piston-data / launchermeta / launcher` → 根，
///   `resources.download.minecraft.net` → `/assets`，`libraries.minecraft.net` → `/maven`
/// - FastMinecraftMirror：按服务子域拆分（meta → launchermeta.*，data → launcher.*，
///   资源 → resources.*，Maven → libraries.*）
pub fn rewrite_url_for(kind: SourceKind, url: &str) -> String {
    if kind == SourceKind::Official {
        return url.to_string();
    }
    match kind {
        SourceKind::Bmclapi => rewrite_root(url, "bmclapi2.bangbang93.com"),
        SourceKind::Fastmirror => rewrite_fastmirror(url),
        SourceKind::Rainyun => rewrite_root(url, "mirrors.rainyun.com"),
        SourceKind::Custom => {
            if let Some(mirror) = custom_mirror_url() {
                // 去掉协议前缀（rewrite_root 内部会补 https://）
                let host = mirror
                    .strip_prefix("https://")
                    .or_else(|| mirror.strip_prefix("http://"))
                    .unwrap_or(&mirror);
                rewrite_root(url, host)
            } else {
                url.to_string()
            }
        }
        SourceKind::Official => unreachable!(),
    }
}

/// 单一镜像根改写（BMCLAPI 风格）
fn rewrite_root(url: &str, mirror: &str) -> String {
    const META_HOSTS: [&str; 4] = [
        "piston-meta.mojang.com",
        "piston-data.mojang.com",
        "launchermeta.mojang.com",
        "launcher.mojang.com",
    ];
    for host in META_HOSTS {
        if let Some(rest) = url.strip_prefix(&format!("https://{host}")) {
            return format!("https://{mirror}{rest}");
        }
    }
    if let Some(rest) = url.strip_prefix("https://resources.download.minecraft.net") {
        return format!("https://{mirror}/assets{rest}");
    }
    if let Some(rest) = url.strip_prefix("https://libraries.minecraft.net") {
        return format!("https://{mirror}/maven{rest}");
    }
    url.to_string()
}

/// FastMinecraftMirror 改写（按官方文档：服务 → 独立子域）
fn rewrite_fastmirror(url: &str) -> String {
    const META_HOSTS: [(&str, &str); 4] = [
        ("piston-meta.mojang.com", "launchermeta.fastmcmirror.org"),
        ("launchermeta.mojang.com", "launchermeta.fastmcmirror.org"),
        ("piston-data.mojang.com", "launcher.fastmcmirror.org"),
        ("launcher.mojang.com", "launcher.fastmcmirror.org"),
    ];
    for (host, target) in META_HOSTS {
        if let Some(rest) = url.strip_prefix(&format!("https://{host}")) {
            return format!("https://{target}{rest}");
        }
    }
    if let Some(rest) = url.strip_prefix("https://resources.download.minecraft.net") {
        return format!("https://resources.fastmcmirror.org{rest}");
    }
    if let Some(rest) = url.strip_prefix("https://libraries.minecraft.net") {
        return format!("https://libraries.fastmcmirror.org{rest}");
    }
    if let Some(rest) = url.strip_prefix("https://libraries.mojang.com") {
        return format!("https://libraries.fastmcmirror.org{rest}");
    }
    url.to_string()
}

/// 单个下载源的可达性 / 速度探测结果
#[derive(Debug, Clone, serde::Serialize)]
pub struct SourceProbe {
    pub source: SourceKind,
    /// 是否可达（HTTP 成功且读取到数据）
    pub reachable: bool,
    /// 首字节延迟（毫秒）
    pub latency_ms: u64,
    /// 实测下载速度（bytes/s，0 表示未测得）
    pub speed_bps: u64,
    /// 失败原因（可达时为 None）
    pub error: Option<String>,
}

/// 测速最大读取量（读到 512KB 即中止连接，够测速不浪费流量）
const PROBE_LIMIT: u64 = 512 * 1024;
/// 单个源测速整体超时
const PROBE_TIMEOUT: Duration = Duration::from_secs(12);

/// 探测单个下载源：下载版本清单（经该源改写）并测量延迟与速度
pub async fn probe_source(kind: SourceKind) -> SourceProbe {
    let err = |e: String| SourceProbe {
        source: kind,
        reachable: false,
        latency_ms: 0,
        speed_bps: 0,
        error: Some(e),
    };
    let client = match reqwest::Client::builder()
        .gzip(true)
        .brotli(true)
        .deflate(true)
        .connect_timeout(Duration::from_secs(8))
        .timeout(PROBE_TIMEOUT)
        .tcp_nodelay(true)
        .build()
    {
        Ok(c) => c,
        Err(e) => return err(e.to_string()),
    };

    let url = rewrite_url_for(kind, MOJANG_MANIFEST_URL);
    let start = Instant::now();
    let resp = match client
        .get(&url)
        .header(RANGE, format!("bytes=0-{}", PROBE_LIMIT - 1))
        .send()
        .await
    {
        Ok(r) if r.status().is_success() => r,
        Ok(r) => return err(format!("HTTP {}", r.status())),
        Err(e) => return err(e.to_string()),
    };

    let mut stream = resp.bytes_stream();
    let mut total: u64 = 0;
    let mut latency_ms: u64 = 0;
    while let Some(chunk) = stream.next().await {
        match chunk {
            Ok(c) => {
                if latency_ms == 0 {
                    latency_ms = start.elapsed().as_millis() as u64;
                }
                total += c.len() as u64;
                if total >= PROBE_LIMIT {
                    break;
                }
            }
            Err(e) => return err(e.to_string()),
        }
    }

    let elapsed = start.elapsed();
    let speed_bps = if elapsed.as_secs_f64() > 0.0 {
        (total as f64 / elapsed.as_secs_f64()) as u64
    } else {
        0
    };
    SourceProbe {
        source: kind,
        reachable: total > 0,
        latency_ms,
        speed_bps,
        error: None,
    }
}

/// 并发探测全部下载源，缓存结果并返回
pub async fn probe_all_sources() -> Vec<SourceProbe> {
    let sources = SourceKind::probeable();
    let results = futures_util::future::join_all(sources.iter().map(|&k| probe_source(k))).await;
    *PROBE_RESULTS.lock().unwrap() = Some(results.clone());
    for r in &results {
        log::info!(
            "下载源测速 [{}] 可达={} 延迟={}ms 速度={}/s{}",
            r.source.name(),
            r.reachable,
            r.latency_ms,
            fmt_speed(r.speed_bps),
            r.error
                .as_deref()
                .map(|e| format!(" 原因: {e}"))
                .unwrap_or_default()
        );
    }
    results
}

/// 最近一次测速结果（未测过返回 None）
pub fn probe_results() -> Option<Vec<SourceProbe>> {
    PROBE_RESULTS.lock().unwrap().clone()
}

/// 从测速结果中挑选最快的可达源：优先实测速度最高；速度均为 0 时取最低延迟；无可达源返回 None
pub fn pick_best(results: &[SourceProbe]) -> Option<SourceKind> {
    let reachable: Vec<&SourceProbe> = results.iter().filter(|r| r.reachable).collect();
    if reachable.is_empty() {
        return None;
    }
    if let Some(best) = reachable.iter().filter(|r| r.speed_bps > 0).max_by_key(|r| r.speed_bps) {
        return Some(best.source);
    }
    reachable
        .iter()
        .min_by_key(|r| r.latency_ms)
        .map(|r| r.source)
}

/// 人类可读速度（日志用）
fn fmt_speed(bps: u64) -> String {
    if bps == 0 {
        return "—".to_string();
    }
    let units = ["B", "KB", "MB", "GB"];
    let mut v = bps as f64;
    let mut i = 0;
    while v >= 1024.0 && i < units.len() - 1 {
        v /= 1024.0;
        i += 1;
    }
    format!("{:.1}{}/s", v, units[i])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn official_identity() {
        assert_eq!(
            rewrite_url_for(SourceKind::Official, MOJANG_MANIFEST_URL),
            MOJANG_MANIFEST_URL
        );
        assert_eq!(
            rewrite_url_for(SourceKind::Official, "https://api.modrinth.com/v2/ping"),
            "https://api.modrinth.com/v2/ping"
        );
    }

    #[test]
    fn bmclapi_rewrites_meta_and_data() {
        assert_eq!(
            rewrite_url_for(SourceKind::Bmclapi, MOJANG_MANIFEST_URL),
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json"
        );
        assert_eq!(
            rewrite_url_for(
                SourceKind::Bmclapi,
                "https://piston-data.mojang.com/v1/objects/abc/client.jar"
            ),
            "https://bmclapi2.bangbang93.com/v1/objects/abc/client.jar"
        );
        assert_eq!(
            rewrite_url_for(
                SourceKind::Bmclapi,
                "https://launchermeta.mojang.com/v1/packages/xyz/1.21.json"
            ),
            "https://bmclapi2.bangbang93.com/v1/packages/xyz/1.21.json"
        );
    }

    #[test]
    fn bmclapi_rewrites_assets_and_maven() {
        assert_eq!(
            rewrite_url_for(
                SourceKind::Bmclapi,
                "https://resources.download.minecraft.net/ab/abcdef"
            ),
            "https://bmclapi2.bangbang93.com/assets/ab/abcdef"
        );
        assert_eq!(
            rewrite_url_for(
                SourceKind::Bmclapi,
                "https://libraries.minecraft.net/com/mojang/brigadier/1.3.0/brigadier-1.3.0.jar"
            ),
            "https://bmclapi2.bangbang93.com/maven/com/mojang/brigadier/1.3.0/brigadier-1.3.0.jar"
        );
    }

    #[test]
    fn fastmirror_rewrites() {
        assert_eq!(
            rewrite_url_for(SourceKind::Fastmirror, MOJANG_MANIFEST_URL),
            "https://launchermeta.fastmcmirror.org/mc/game/version_manifest_v2.json"
        );
        assert_eq!(
            rewrite_url_for(
                SourceKind::Fastmirror,
                "https://piston-data.mojang.com/v1/objects/abc/client.jar"
            ),
            "https://launcher.fastmcmirror.org/v1/objects/abc/client.jar"
        );
        assert_eq!(
            rewrite_url_for(
                SourceKind::Fastmirror,
                "https://resources.download.minecraft.net/ab/abcdef"
            ),
            "https://resources.fastmcmirror.org/ab/abcdef"
        );
        assert_eq!(
            rewrite_url_for(
                SourceKind::Fastmirror,
                "https://libraries.minecraft.net/a/b.jar"
            ),
            "https://libraries.fastmcmirror.org/a/b.jar"
        );
        assert_eq!(
            rewrite_url_for(
                SourceKind::Fastmirror,
                "https://libraries.mojang.com/a/b.jar"
            ),
            "https://libraries.fastmcmirror.org/a/b.jar"
        );
    }

    #[test]
    fn non_mojang_urls_pass_through() {
        for kind in SourceKind::all() {
            assert_eq!(
                rewrite_url_for(kind, "https://api.modrinth.com/v2/project/a/version"),
                "https://api.modrinth.com/v2/project/a/version"
            );
            assert_eq!(
                rewrite_url_for(kind, "https://maven.minecraftforge.net/a/b.jar"),
                "https://maven.minecraftforge.net/a/b.jar"
            );
        }
    }

    #[test]
    fn pick_best_prefers_fastest_reachable() {
        let results = vec![
            SourceProbe {
                source: SourceKind::Official,
                reachable: true,
                latency_ms: 120,
                speed_bps: 2_000_000,
                error: None,
            },
            SourceProbe {
                source: SourceKind::Bmclapi,
                reachable: true,
                latency_ms: 40,
                speed_bps: 12_000_000,
                error: None,
            },
            SourceProbe {
                source: SourceKind::Fastmirror,
                reachable: false,
                latency_ms: 0,
                speed_bps: 0,
                error: Some("timeout".into()),
            },
        ];
        assert_eq!(pick_best(&results), Some(SourceKind::Bmclapi));
    }

    #[test]
    fn pick_best_none_when_all_down() {
        let results = vec![
            SourceProbe {
                source: SourceKind::Official,
                reachable: false,
                latency_ms: 0,
                speed_bps: 0,
                error: Some("down".into()),
            },
            SourceProbe {
                source: SourceKind::Bmclapi,
                reachable: false,
                latency_ms: 0,
                speed_bps: 0,
                error: Some("down".into()),
            },
        ];
        assert_eq!(pick_best(&results), None);
    }

    #[test]
    fn raiyun_rewrites_meta() {
        assert_eq!(
            rewrite_url_for(SourceKind::Rainyun, MOJANG_MANIFEST_URL),
            "https://mirrors.rainyun.com/mc/game/version_manifest_v2.json"
        );
        assert_eq!(
            rewrite_url_for(
                SourceKind::Rainyun,
                "https://resources.download.minecraft.net/ab/abcdef"
            ),
            "https://mirrors.rainyun.com/assets/ab/abcdef"
        );
    }

    #[test]
    fn custom_rewrites_with_url() {
        set_custom_mirror_url("https://my-mirror.example.com");
        assert_eq!(
            rewrite_url_for(SourceKind::Custom, MOJANG_MANIFEST_URL),
            "https://my-mirror.example.com/mc/game/version_manifest_v2.json"
        );
        // Clean up
        set_custom_mirror_url("");
    }

    #[test]
    fn custom_passthrough_without_url() {
        assert_eq!(
            rewrite_url_for(SourceKind::Custom, MOJANG_MANIFEST_URL),
            MOJANG_MANIFEST_URL
        );
    }

    #[test]
    fn source_id_roundtrip() {
        for kind in SourceKind::all() {
            let id = kind.id();
            let parsed = SourceKind::from_id(id);
            assert_eq!(parsed, Some(kind), "roundtrip failed for {:?}", kind);
        }
    }

    #[test]
    fn from_id_unknown_returns_none() {
        assert_eq!(SourceKind::from_id("unknown_source"), None);
    }

    #[test]
    fn healthy_sources_sorts_correctly() {
        reset_source_health();
        // Simulate Fastmirror degradation
        for _ in 0..5 {
            record_source_failure(SourceKind::Fastmirror);
        }
        let sources = healthy_sources();
        // Official should be last
        assert_eq!(sources.last(), Some(&SourceKind::Official));
        // Fastmirror should be before Official (degraded)
        let fast_pos = sources.iter().position(|&s| s == SourceKind::Fastmirror).unwrap();
        let official_pos = sources.iter().position(|&s| s == SourceKind::Official).unwrap();
        assert!(fast_pos < official_pos, "degraded source should come before official");

        // Recovery
        record_source_success(SourceKind::Fastmirror);
        let sources2 = healthy_sources();
        let fast_pos2 = sources2.iter().position(|&s| s == SourceKind::Fastmirror).unwrap();
        let official_pos2 = sources2.iter().position(|&s| s == SourceKind::Official).unwrap();
        assert!(fast_pos2 < official_pos2, "recovered source should still be before official");
    }
}
