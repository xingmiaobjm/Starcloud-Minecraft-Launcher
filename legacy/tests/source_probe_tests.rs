//! 多下载源回退 & 自动测速 真实集成测试
//!
//! 运行方式：
//!   cargo test --test source_probe_tests -- --nocapture
//!
//! 包含：
//!   1. 真实测速所有源（probe_all_sources）
//!   2. 健康度排序验证（healthy_sources）
//!   3. 多源回退下载（get_with_fallback / get_bytes_with_fallback）
//!   4. 源失败降级 & 恢复

use starcloud_minecraft::download::sources::{
    custom_mirror_url, healthy_sources, probe_all_sources, record_source_failure,
    record_source_success, reset_source_health, set_custom_mirror_url, SourceKind, MOJANG_MANIFEST_URL,
};
use starcloud_minecraft::download::HttpClient;

// ── 测速 ──────────────────────────────────────────────────────

#[tokio::test]
async fn test_probe_all_sources_real() {
    eprintln!("==========================================");
    eprintln!("  测试：全源测速（真实网络请求）");
    eprintln!("==========================================");
    eprintln!("目标 URL: {}\n", MOJANG_MANIFEST_URL);

    let results = probe_all_sources().await;

    assert!(!results.is_empty(), "测速结果不应为空");

    let mut any_reachable = false;
    for r in &results {
        let status = if r.reachable {
            any_reachable = true;
            "✅ 可达"
        } else {
            "❌ 不可达"
        };
        eprintln!(
            "  {}  {}  |  延迟: {}ms  |  速度: {}",
            status,
            r.source.name(),
            r.latency_ms,
            fmt_speed(r.speed_bps),
        );
        if let Some(err) = &r.error {
            eprintln!("        原因: {}", err);
        }
    }

    assert!(any_reachable, "至少需要一个源可达才能继续测试");
    eprintln!("\n测速通过 —— 至少一个源可达\n");
}

// ── 健康度排序 ─────────────────────────────────────────────────

#[tokio::test]
async fn test_health_tracking_and_ordering() {
    eprintln!("==========================================");
    eprintln!("  测试：健康度追踪 & 排序");
    eprintln!("==========================================");

    reset_source_health();

    // 初始状态：所有源都是健康的
    let sources = healthy_sources();
    eprintln!("初始健康源列表:");
    for (i, s) in sources.iter().enumerate() {
        eprintln!("  {}. {} ({})", i + 1, s.name(), s.id());
    }
    // Official 应该在最后
    assert_eq!(
        sources.last(),
        Some(&SourceKind::Official),
        "Official 应始终在最后"
    );

    // 模拟 Bmclapi 连续失败 5 次 → 降级
    eprintln!("\n模拟 BMCLAPI 连续失败 5 次...");
    for i in 1..=5 {
        record_source_failure(SourceKind::Bmclapi);
        eprintln!("  第 {} 次失败", i);
    }

    let sources = healthy_sources();
    eprintln!("\n降级后健康源列表:");
    for (i, s) in sources.iter().enumerate() {
        let _degraded = i > 0
            && sources[..i].iter().any(|prev| *prev == SourceKind::Official);
        eprintln!("  {}. {} ({})", i + 1, s.name(), s.id());
    }
    // Official 仍在最后
    assert_eq!(
        sources.last(),
        Some(&SourceKind::Official),
        "降级后 Official 仍应在最后"
    );
    // Bmclapi 应在 Official 之前（已降级）
    let bmcl_pos = sources
        .iter()
        .position(|&s| s == SourceKind::Bmclapi)
        .unwrap();
    let official_pos = sources
        .iter()
        .position(|&s| s == SourceKind::Official)
        .unwrap();
    assert!(
        bmcl_pos < official_pos,
        "降级源应排在 Official 之前"
    );

    // 恢复：一次成功即可恢复
    eprintln!("\nBMCLAPI 恢复（一次成功）...");
    record_source_success(SourceKind::Bmclapi);
    let sources = healthy_sources();
    eprintln!("恢复后健康源列表:");
    for (i, s) in sources.iter().enumerate() {
        eprintln!("  {}. {} ({})", i + 1, s.name(), s.id());
    }
    assert_eq!(sources.last(), Some(&SourceKind::Official));

    eprintln!("\n健康度追踪测试通过\n");
}

// ── 多源回退下载 ───────────────────────────────────────────────

#[tokio::test]
async fn test_get_bytes_with_fallback() {
    eprintln!("==========================================");
    eprintln!("  测试：多源回退下载（真实请求）");
    eprintln!("==========================================");

    reset_source_health();

    let client = HttpClient::new().expect("创建 HTTP 客户端失败");

    // 先用测速结果确定可用源
    let probe_results = probe_all_sources().await;
    let reachable: Vec<_> = probe_results
        .iter()
        .filter(|r| r.reachable)
        .map(|r| r.source)
        .collect();
    assert!(
        !reachable.is_empty(),
        "至少需要一个源可达才能测试下载"
    );

    eprintln!(
        "可达源: {}",
        reachable
            .iter()
            .map(|s| s.name())
            .collect::<Vec<_>>()
            .join(", ")
    );

    // 用健康源列表下载版本清单（小文件，约 500KB）
    let sources = healthy_sources();
    eprintln!("使用健康源列表下载版本清单...");

    let result = client
        .get_bytes_with_fallback(MOJANG_MANIFEST_URL, &sources)
        .await;

    match &result {
        Ok(bytes) => {
            eprintln!("✅ 下载成功: {} bytes", bytes.len());
            // 验证是合法 JSON
            let _json: serde_json::Value =
                serde_json::from_slice(bytes).expect("版本清单应为合法 JSON");
            eprintln!("✅ JSON 解析通过");
        }
        Err(e) => {
            eprintln!("❌ 所有源均失败: {}", e);
            panic!("多源回退下载失败: {}", e);
        }
    }

    eprintln!("\n多源回退下载测试通过\n");
}

// ── 自定义镜像源 ───────────────────────────────────────────────

#[tokio::test]
async fn test_custom_mirror_source() {
    // 测试自定义镜像 URL 为空时不参与测速
    set_custom_mirror_url("");

    let sources = healthy_sources();
    let has_custom = sources.iter().any(|&s| s == SourceKind::Custom);
    assert!(
        !has_custom,
        "未设置自定义 URL 时，Custom 不应出现在健康源列表中"
    );

    eprintln!("✅ 未设置自定义镜像 URL 时，Custom 不参与列表");

    // 设置一个假的自定义 URL
    set_custom_mirror_url("https://my-custom-mirror.example.com");
    assert!(custom_mirror_url().is_some());

    let sources = healthy_sources();
    let has_custom = sources.iter().any(|&s| s == SourceKind::Custom);
    assert!(
        has_custom,
        "设置自定义 URL 后，Custom 应出现在健康源列表中"
    );

    eprintln!("✅ 设置自定义镜像 URL 后，Custom 参与列表");

    // 清理
    set_custom_mirror_url("");
    assert!(custom_mirror_url().is_none());

    eprintln!("\n自定义镜像源测试通过\n");
}

// ── 辅助函数 ───────────────────────────────────────────────────

fn fmt_speed(bps: u64) -> String {
    if bps == 0 {
        return "—".to_string();
    }
    let units = ["B/s", "KB/s", "MB/s", "GB/s"];
    let mut v = bps as f64;
    let mut i = 0;
    while v >= 1024.0 && i < units.len() - 1 {
        v /= 1024.0;
        i += 1;
    }
    format!("{:.1} {}", v, units[i])
}