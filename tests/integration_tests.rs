use furry_minecraft::{
    account::OfflineAccount,
    config::{LaunchProfile, LauncherConfig, ProfileManager},
    download::RetryConfig,
    modloader::ModLoaderType,
    version_manager::{VersionComparator, VersionDiff, VersionValidator},
    LaunchConfig,
};
use std::sync::atomic::{AtomicU32, Ordering};

// ============================================================
// 版本比较测试
// ============================================================
#[test]
fn test_version_comparison_basic() {
    // 同版本
    assert_eq!(
        VersionComparator::compare("1.20.1", "1.20.1"),
        std::cmp::Ordering::Equal
    );
    // 递增
    assert_eq!(
        VersionComparator::compare("1.19", "1.20"),
        std::cmp::Ordering::Less
    );
    assert_eq!(
        VersionComparator::compare("1.20.0", "1.20.1"),
        std::cmp::Ordering::Less
    );
    // 递减
    assert_eq!(
        VersionComparator::compare("1.21", "1.20"),
        std::cmp::Ordering::Greater
    );
    // 主版本号
    assert_eq!(
        VersionComparator::compare("1.8", "1.7.10"),
        std::cmp::Ordering::Greater
    );
}

#[test]
fn test_version_diff() {
    assert_eq!(
        VersionComparator::diff("1.19", "1.20"),
        VersionDiff::Newer {
            id: "1.20".to_string()
        }
    );
    assert_eq!(VersionComparator::diff("1.20", "1.20"), VersionDiff::Same);
    assert_eq!(VersionComparator::diff("1.21", "1.20"), VersionDiff::Older);
}

#[test]
fn test_snapshot_detection() {
    assert!(VersionComparator::is_snapshot("24w01a"));
    assert!(VersionComparator::is_snapshot("1.21-pre1"));
    assert!(VersionComparator::is_snapshot("1.21-Pre2"));
    assert!(!VersionComparator::is_snapshot("1.20.1"));
    assert!(!VersionComparator::is_snapshot("1.21"));
}

#[test]
fn test_version_id_validation() {
    assert!(VersionComparator::is_valid_id("1.20.1"));
    assert!(VersionComparator::is_valid_id("24w01a"));
    assert!(VersionComparator::is_valid_id("forge-1.20.1-47.2.0"));
    assert!(!VersionComparator::is_valid_id(""));
    assert!(!VersionComparator::is_valid_id("a".repeat(65).as_str()));
    assert!(!VersionComparator::is_valid_id("bad<>id"));
}

// ============================================================
// LaunchConfig 校验测试
// ============================================================
#[test]
fn test_validate_launch_config_valid() {
    let config = LaunchConfig::basic("1.21", "/tmp/.minecraft", "Player");
    assert!(VersionValidator::validate_launch_config(&config).is_ok());
}

#[test]
fn test_validate_launch_config_empty_version() {
    let config = LaunchConfig::basic("", "/tmp/.minecraft", "Player");
    assert!(VersionValidator::validate_launch_config(&config).is_err());
}

#[test]
fn test_validate_launch_config_invalid_username() {
    let config = LaunchConfig::basic("1.21", "/tmp/.minecraft", "bad name");
    assert!(VersionValidator::validate_launch_config(&config).is_err());

    let long_name = LaunchConfig::basic("1.21", "/tmp/.minecraft", "a".repeat(33).as_str());
    assert!(VersionValidator::validate_launch_config(&long_name).is_err());
}

#[test]
fn test_validate_launch_config_memory_limits() {
    // 最小内存大于最大内存
    let config = LaunchConfig::basic("1.21", "/tmp/.minecraft", "Player").with_memory(4096, 1024);
    assert!(VersionValidator::validate_launch_config(&config).is_err());

    // 内存过小
    let config = LaunchConfig::basic("1.21", "/tmp/.minecraft", "Player").with_memory(128, 256);
    assert!(VersionValidator::validate_launch_config(&config).is_err());

    // 内存过大
    let config = LaunchConfig::basic("1.21", "/tmp/.minecraft", "Player").with_memory(1024, 99999);
    assert!(VersionValidator::validate_launch_config(&config).is_err());
}

#[test]
fn test_validate_launch_config_window_size() {
    let config = LaunchConfig::basic("1.21", "/tmp/.minecraft", "Player").with_window(100, 100);
    assert!(VersionValidator::validate_launch_config(&config).is_err());
}

// ============================================================
// URL 校验测试
// ============================================================
#[test]
fn test_validate_url() {
    assert!(VersionValidator::validate_url("https://example.com/file.txt").is_ok());
    assert!(VersionValidator::validate_url("http://localhost:8080").is_ok());
    assert!(VersionValidator::validate_url("ftp://bad.com").is_err());
    assert!(VersionValidator::validate_url("").is_err());
}

// ============================================================
// 离线账户测试
// ============================================================
#[test]
fn test_offline_account_creation() {
    let account = OfflineAccount::new("TestUser").unwrap();
    assert_eq!(account.username, "TestUser");
    assert!(uuid::Uuid::parse_str(&account.uuid).is_ok());
}

#[test]
fn test_offline_uuid_deterministic() {
    let a = OfflineAccount::new("PlayerOne").unwrap();
    let b = OfflineAccount::new("PlayerOne").unwrap();
    assert_eq!(a.uuid, b.uuid);

    let c = OfflineAccount::new("PlayerTwo").unwrap();
    assert_ne!(a.uuid, c.uuid);
}

#[test]
fn test_offline_account_invalid() {
    assert!(OfflineAccount::new("").is_err());
    assert!(OfflineAccount::new("name with spaces").is_err());
    assert!(OfflineAccount::new("a".repeat(33).as_str()).is_err());
}

#[test]
fn test_offline_account_credentials() {
    let account = OfflineAccount::new("Player").unwrap();
    let cred = account.credentials();
    assert_eq!(cred.username, "Player");
    assert_eq!(cred.access_token, "0");
    assert_eq!(cred.user_type, "mojang");
}

// ============================================================
// 启动器配置测试
// ============================================================
#[test]
fn test_launcher_config_default() {
    let config = LauncherConfig::default();
    assert_eq!(config.config_version, 1);
    assert_eq!(config.default_min_memory_mb, 1024);
    assert_eq!(config.default_max_memory_mb, 4096);
    assert_eq!(config.download_concurrency, 8);
    assert_eq!(config.download_max_retries, 3);
    assert_eq!(config.max_cache_entries, 128);
}

#[test]
#[allow(clippy::field_reassign_with_default)] // 测试语义：先修改再验证 reset
fn test_launcher_config_reset() {
    let mut config = LauncherConfig::default();
    config.default_max_memory_mb = 8192;
    config.download_concurrency = 16;
    config.reset();
    assert_eq!(config.default_max_memory_mb, 4096);
    assert_eq!(config.download_concurrency, 8);
}

#[test]
#[allow(clippy::field_reassign_with_default)] // 测试语义：构造低版本配置验证迁移
fn test_launcher_config_migrate() {
    let mut config = LauncherConfig::default();
    config.config_version = 0;
    config.download_concurrency = 1;
    config.migrate().unwrap();
    assert_eq!(config.config_version, 1);
    assert_eq!(config.download_concurrency, 8);
}

#[test]
fn test_launch_profile() {
    let profile = LaunchProfile::new("MyProfile", "1.20.1", "Player");
    assert_eq!(profile.name, "MyProfile");
    assert_eq!(profile.version_id, "1.20.1");
    assert_eq!(profile.min_memory_mb, 2048);
    assert_eq!(profile.max_memory_mb, 4096);
}

#[test]
fn test_profile_manager_operations() {
    let temp = std::env::temp_dir().join("furry_test_profiles");
    let mut mgr = ProfileManager::new(&temp);

    mgr.add(LaunchProfile::new("Profile1", "1.20.1", "P1"));
    mgr.add(LaunchProfile::new("Profile2", "1.21", "P2"));

    assert_eq!(mgr.list().len(), 2);
    assert!(mgr.find("Profile1").is_some());
    assert!(mgr.find("Missing").is_none());

    assert!(mgr.remove("Profile1"));
    assert_eq!(mgr.list().len(), 1);
}

// ============================================================
// 重试配置测试
// ============================================================
#[test]
fn test_retry_config_default() {
    let config = RetryConfig::default();
    assert_eq!(config.max_retries, 3);
    assert!(!config.retryable_statuses.is_empty());
}

#[tokio::test]
async fn test_retry_success_first_try() {
    let config = RetryConfig::default();
    let attempts = AtomicU32::new(0);

    let result: furry_minecraft::Result<u32> = config
        .retry(|| {
            attempts.fetch_add(1, Ordering::SeqCst);
            async { Ok(42) }
        })
        .await;

    assert_eq!(result.unwrap(), 42);
    assert_eq!(attempts.load(Ordering::SeqCst), 1);
}

#[tokio::test]
async fn test_retry_eventually_fails() {
    let config = RetryConfig {
        max_retries: 2,
        initial_delay: std::time::Duration::from_millis(1),
        max_delay: std::time::Duration::from_millis(10),
        ..RetryConfig::default()
    };

    let attempts = AtomicU32::new(0);

    let result: furry_minecraft::Result<u32> = config
        .retry(|| {
            attempts.fetch_add(1, Ordering::SeqCst);
            async {
                Err(furry_minecraft::LauncherError::DownloadError(
                    "总是失败".to_string(),
                ))
            }
        })
        .await;

    assert!(result.is_err());
    assert_eq!(attempts.load(Ordering::SeqCst), 2);
}

// ============================================================
// 下载进度追踪测试
// ============================================================
#[test]
fn test_progress_tracker() {
    let tracker = furry_minecraft::download::ProgressTracker::new();
    tracker.set_total(200);

    assert_eq!(tracker.snapshot().percentage, 0.0);

    tracker.set_progress(100);
    assert_eq!(tracker.snapshot().downloaded_bytes, 100);
    assert_eq!(tracker.snapshot().percentage, 50.0);

    tracker.add_progress(100);
    assert!(tracker.is_complete());
    assert_eq!(tracker.snapshot().percentage, 100.0);
}

#[test]
fn test_progress_tracker_reset() {
    let tracker = furry_minecraft::download::ProgressTracker::new();
    tracker.set_total(100);
    tracker.set_progress(100);
    assert!(tracker.is_complete());

    tracker.reset();
    assert!(!tracker.is_complete());
    assert_eq!(tracker.snapshot().downloaded_bytes, 0);
}

// ============================================================
// ModLoader 类型测试
// ============================================================
#[test]
fn test_modloader_type_display() {
    assert_eq!(ModLoaderType::Forge.to_string(), "forge");
    assert_eq!(ModLoaderType::Fabric.to_string(), "fabric");
    assert_eq!(ModLoaderType::Quilt.to_string(), "quilt");
    assert_eq!(ModLoaderType::NeoForge.to_string(), "neoforge");
    assert_eq!(ModLoaderType::Custom("test".into()).to_string(), "test");
}
