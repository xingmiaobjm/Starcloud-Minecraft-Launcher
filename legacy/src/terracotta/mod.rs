//! Terracotta | 陶瓦联机 集成
//!
//! Terracotta 是第三方开源免费 P2P 联机工具（基于 EasyTier 开发，为 Minecraft 优化），
//! 使玩家无需公网 IP / 端口映射即可与朋友联机。
//!
//! 集成方式（遵循 Terracotta 的 AGPL-3.0-or-later「例外条款」，保证本启动器保持 MIT 许可）：
//! 1. 下载官方平台包（tar.gz，含未经修改的二进制）并解压；
//! 2. 以 `--hmcl <portFile>` 参数启动核心进程，进程将本地 HTTP 端口写入 JSON 文件；
//! 3. 通过本地 HTTP API（`/state` `/state/scanning` `/state/guesting` ...）驱动联机；
//! 4. 启动器界面须显著标识「联机由 Terracotta | 陶瓦联机 提供技术支持」。
//!
//! 协议参考：HMCL 的 `org.jackhuang.hmcl.terracotta` 集成实现。

use crate::error::{LauncherError, Result};
use crate::download::progress::{ProgressStatus, ProgressTracker};
use futures_util::StreamExt;
use serde::{Deserialize, Serialize};
use std::path::{Component, Path, PathBuf};
use std::process::Stdio;
use std::time::Duration;
use tokio::io::AsyncWriteExt;

/// Terracotta 核心版本（对应 GitHub Releases 标签）
pub const TERRACOTTA_VERSION: &str = "0.4.2";
/// GitHub Releases 下载基址
const GITHUB_RELEASE_BASE: &str = "https://github.com/burningtnt/Terracotta/releases/download";
/// Gitee 镜像 Releases 下载基址（国内加速）
const GITEE_RELEASE_BASE: &str = "https://gitee.com/burningtnt/Terracotta/releases/download";
/// Terracotta 公共节点列表（打洞/中继服务器）
const NODE_LIST_URL: &str = "https://terracotta.glavo.site/nodes";
/// 已安装版本标识文件
const VERSION_FILE: &str = "version.txt";

// ---------------------------------------------------------------------------
// 平台与包
// ---------------------------------------------------------------------------

/// 平台包的可执行文件名（Windows 下为 .exe）
pub fn executable_name() -> &'static str {
    if cfg!(target_os = "windows") {
        "terracotta.exe"
    } else {
        "terracotta"
    }
}

/// Release 资产平台后缀（如 `windows-x86_64`）
pub fn platform_tag() -> Result<&'static str> {
    let tag = match (std::env::consts::OS, std::env::consts::ARCH) {
        ("windows", "x86_64") => "windows-x86_64",
        ("windows", "aarch64") => "windows-arm64",
        ("linux", "x86_64") => "linux-x86_64",
        ("linux", "aarch64") => "linux-arm64",
        ("linux", "riscv64") => "linux-riscv64",
        ("linux", "loongarch64") => "linux-loongarch64",
        ("macos", "x86_64") => "macos-x86_64",
        ("macos", "aarch64") => "macos-arm64",
        ("freebsd", "x86_64") => "freebsd-x86_64",
        ("freebsd", "aarch64") => "freebsd-arm64",
        _ => {
            return Err(LauncherError::Other(format!(
                "当前平台暂不支持陶瓦联机（{} {}）",
                std::env::consts::OS,
                std::env::consts::ARCH
            )))
        }
    };
    Ok(tag)
}

/// 平台包文件名（如 `terracotta-0.4.2-windows-x86_64-pkg.tar.gz`）
pub fn bundle_file_name() -> Result<String> {
    Ok(format!(
        "terracotta-{}-{}-pkg.tar.gz",
        TERRACOTTA_VERSION,
        platform_tag()?
    ))
}

/// 全部下载地址（国内镜像优先，官方兜底）
pub fn bundle_urls() -> Result<Vec<String>> {
    let file = bundle_file_name()?;
    let gitee = format!("{}/v{}/{}", GITEE_RELEASE_BASE, TERRACOTTA_VERSION, file);
    let github = format!("{}/v{}/{}", GITHUB_RELEASE_BASE, TERRACOTTA_VERSION, file);
    Ok(vec![gitee, github])
}

/// 校验 tar 条目路径安全（拒绝绝对路径与 `..` 穿越），返回安全相对名
fn sanitize_tar_entry(entry_name: &str) -> Result<&str> {
    if entry_name.is_empty() {
        return Err(LauncherError::Other("压缩包包含空路径条目".into()));
    }
    // 去掉开头的 `./` 前缀（tar 常见），但保留目录层级
    let trimmed = entry_name.trim_start_matches("./");
    if Path::new(trimmed).is_absolute()
        || trimmed.starts_with('/')
        || trimmed.starts_with('\\')
        || trimmed.contains(':')
    {
        return Err(LauncherError::Other(format!(
            "压缩包条目包含绝对路径: {:?}",
            trimmed
        )));
    }
    for component in Path::new(trimmed).components() {
        match component {
            Component::ParentDir => {
                return Err(LauncherError::Other(format!(
                    "压缩包条目包含路径穿越: {:?}",
                    trimmed
                )));
            }
            Component::CurDir | Component::RootDir | Component::Prefix(_) => {
                return Err(LauncherError::Other(format!(
                    "压缩包条目包含非法路径: {:?}",
                    trimmed
                )));
            }
            Component::Normal(_) => {}
        }
    }
    Ok(trimmed)
}

/// 解压平台包 tar.gz 到核心目录（带路径穿越防护）
pub fn extract_bundle(pkg: &Path, core_dir: &Path) -> Result<()> {
    let file = std::fs::File::open(pkg)?;
    let decoder = flate2::read::GzDecoder::new(file);
    let mut archive = tar::Archive::new(decoder);
    for entry in archive.entries()? {
        let mut entry = entry?;
        let name = entry.path()?.to_string_lossy().to_string();
        let safe = match sanitize_tar_entry(&name) {
            Ok(n) => n,
            Err(e) => {
                log::warn!("跳过不安全的 Terracotta 包条目: {} ({})", name, e);
                continue;
            }
        };
        let out = core_dir.join(safe);
        if entry.header().entry_type().is_dir() {
            std::fs::create_dir_all(&out)?;
        } else {
            if let Some(parent) = out.parent() {
                std::fs::create_dir_all(parent)?;
            }
            entry.unpack(&out)?;
        }
    }
    Ok(())
}

/// 读取已安装的核心版本（未安装返回 None）
pub fn installed_version(core_dir: &Path) -> Option<String> {
    let f = core_dir.join(VERSION_FILE);
    std::fs::read_to_string(f)
        .ok()
        .map(|s| s.trim().to_string())
}

/// 将平台包内带版本/平台后缀的可执行文件统一重命名为固定名。
///
/// 官方包内文件名形如 `terracotta-0.4.2-windows-x86_64.exe`（非固定的 `terracotta.exe`），
/// 为保证 `executable_name()` / `is_installed()` / `launch_session()` 使用一致的文件名，
/// 解压后统一重命名为固定名（重命名不改动二进制内容，符合官方许可例外条款）。
pub fn normalize_executable(core_dir: &Path) -> Result<()> {
    let fixed = core_dir.join(executable_name());
    if fixed.exists() {
        return Ok(());
    }
    let entry = std::fs::read_dir(core_dir)?
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .find(|p| {
            if !p.is_file() {
                return false;
            }
            if p.file_name() == fixed.file_name() {
                return false;
            }
            let name = p.file_name().and_then(|n| n.to_str()).unwrap_or("");
            name.starts_with("terracotta")
        });
    match entry {
        Some(src) => std::fs::rename(&src, &fixed).map_err(|e| {
            LauncherError::DownloadError(format!("重命名联机核心可执行文件失败: {}", e))
        }),
        None => Err(LauncherError::DownloadError(format!(
            "联机核心包缺少可执行文件 {}",
            executable_name()
        ))),
    }
}

/// 核心是否已就绪（版本匹配 + 可执行文件存在）
pub fn is_installed(core_dir: &Path) -> bool {
    installed_version(core_dir).as_deref() == Some(TERRACOTTA_VERSION)
        && core_dir.join(executable_name()).exists()
}

// ---------------------------------------------------------------------------
// 节点列表
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize)]
struct NodeEntry {
    url: String,
    #[serde(default)]
    region: Option<String>,
}

/// 过滤节点：region 为空（全球）始终保留；
/// `include_cn` 为 true 时仅额外保留中国节点（防止跨地域互不联通的场景），
/// 为 false 时保留除中国节点外的全部。
fn filter_nodes(entries: Vec<NodeEntry>, include_cn: bool) -> Vec<String> {
    entries
        .into_iter()
        .filter(|n| {
            match n.region.as_deref() {
                None => true,
                Some(r) if r.eq_ignore_ascii_case("CN") => include_cn,
                Some(_) => true,
            }
        })
        .map(|n| n.url)
        .collect()
}

/// 获取 Terracotta 公共节点列表（失败返回空，不阻断联机流程）
pub async fn fetch_nodes(include_cn: bool) -> Vec<String> {
    let client = match http_client() {
        Ok(c) => c,
        Err(_) => return Vec::new(),
    };
    match client
        .get(NODE_LIST_URL)
        .send()
        .await
        .and_then(|r| r.error_for_status())
    {
        Ok(resp) => match resp.json::<Vec<NodeEntry>>().await {
            Ok(entries) => filter_nodes(entries, include_cn),
            Err(e) => {
                log::warn!("解析 Terracotta 节点列表失败: {}", e);
                Vec::new()
            }
        },
        Err(e) => {
            log::warn!("获取 Terracotta 节点列表失败: {}", e);
            Vec::new()
        }
    }
}

// ---------------------------------------------------------------------------
// 下载与安装
// ---------------------------------------------------------------------------

/// 流式下载平台包（镜像链：任一成功即止），进度经 tracker 上报
async fn download_bundle(dest_dir: &Path, tracker: &ProgressTracker) -> Result<PathBuf> {
    let file_name = bundle_file_name()?;
    let dest = dest_dir.join(&file_name);
    let urls = bundle_urls()?;
    let mut last_err: Option<LauncherError> = None;
    for url in urls {
        log::info!("下载 Terracotta 平台包: {}", url);
        tracker.begin_stage("下载联机核心", 0);
        match download_to(&url, &dest, tracker).await {
            Ok(()) => {
                tracker.set_status(ProgressStatus::Completed);
                return Ok(dest);
            }
            Err(e) => {
                log::warn!("下载 Terracotta 失败（尝试下一镜像）: {}", e);
                last_err = Some(e);
                let _ = tokio::fs::remove_file(&dest).await;
            }
        }
    }
    Err(last_err
        .unwrap_or_else(|| LauncherError::DownloadError("没有可用的下载地址".into())))
}

async fn download_to(url: &str, dest: &Path, tracker: &ProgressTracker) -> Result<()> {
    let client = http_client()?;
    let resp = client.get(url).send().await?.error_for_status()?;
    let total = resp.content_length().unwrap_or(0);
    tracker.begin_stage("下载联机核心", total);

    if let Some(parent) = dest.parent() {
        tokio::fs::create_dir_all(parent).await?;
    }
    let mut file = tokio::fs::File::create(dest).await?;
    let mut stream = resp.bytes_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk?;
        file.write_all(&chunk).await?;
        tracker.add_progress(chunk.len() as u64);
    }
    file.flush().await?;
    Ok(())
}

/// 安装（下载 + 解压 + 写入版本标识）并返回核心目录
pub async fn install_core(terracotta_root: &Path, tracker: &ProgressTracker) -> Result<PathBuf> {
    let core_dir = terracotta_root.join("core");
    let pkg = download_bundle(terracotta_root, tracker).await?;

    tracker.set_status(ProgressStatus::Extracting);
    tracker.set_stage("解压联机核心");
    // 解压前清空旧核心，避免残留旧 DLL 干扰
    if core_dir.exists() {
        tokio::fs::remove_dir_all(&core_dir).await?;
    }
    tokio::fs::create_dir_all(&core_dir).await?;

    // 解压（阻塞 IO，任务本身在后台线程执行）
    extract_bundle(&pkg, &core_dir)?;
    // 官方包内可执行文件带版本/平台后缀，统一重命名为固定名（失败即视为包不完整）
    normalize_executable(&core_dir)?;
    tokio::fs::write(core_dir.join(VERSION_FILE), TERRACOTTA_VERSION).await?;
    // 清理下载的压缩包
    let _ = tokio::fs::remove_file(&pkg).await;

    log::info!("Terracotta 核心 {} 安装完成", TERRACOTTA_VERSION);
    Ok(core_dir)
}

// ---------------------------------------------------------------------------
// 进程与会话
// ---------------------------------------------------------------------------

/// 运行中的 Terracotta 会话（子进程 + 本地 HTTP 端口）
pub struct TerracottaSession {
    /// 本地 HTTP API 端口
    pub port: u16,
    /// 核心子进程
    pub child: std::process::Child,
    /// 临时端口传输文件目录（进程退出后清理）
    pub temp_dir: Option<tempfile::TempDir>,
}

impl TerracottaSession {
    /// 停止核心进程（发送停止请求后兜底 kill）
    pub fn shutdown(&mut self) {
        let _ = set_waiting_sync(self.port);
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn http_client() -> Result<reqwest::Client> {
    Ok(reqwest::Client::builder()
        .connect_timeout(Duration::from_secs(3))
        .timeout(Duration::from_secs(10))
        .tcp_keepalive(Some(Duration::from_secs(60)))
        .build()?)
}

/// 启动核心进程并等待端口文件出现（最多约 30 秒）
pub fn launch_session(core_dir: &Path) -> Result<TerracottaSession> {
    let exe = core_dir.join(executable_name());
    if !exe.exists() {
        return Err(LauncherError::Other(format!(
            "联机核心未安装（缺少 {}）",
            executable_name()
        )));
    }
    let temp_dir = tempfile::tempdir()?;
    let port_file = temp_dir.path().join("http");

    let mut child = std::process::Command::new(&exe)
        .arg("--hmcl")
        .arg(&port_file)
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| LauncherError::LaunchError(format!("启动联机核心失败: {}", e)))?;

    // 轮询端口文件
    let deadline = std::time::Instant::now() + Duration::from_secs(30);
    let mut last_err = None;
    while std::time::Instant::now() < deadline {
        if let Ok(text) = std::fs::read_to_string(&port_file) {
            match serde_json::from_str::<serde_json::Value>(&text) {
                Ok(json) => {
                    if let Some(port) = json.get("port").and_then(|v| v.as_u64()) {
                        log::info!("Terracotta 核心已启动，端口 {}", port);
                        return Ok(TerracottaSession {
                            port: port as u16,
                            child,
                            temp_dir: Some(temp_dir),
                        });
                    }
                }
                Err(e) => last_err = Some(e),
            }
        }
        std::thread::sleep(Duration::from_millis(200));
    }
    let _ = child.kill();
    let _ = child.wait();
    Err(LauncherError::LaunchError(format!(
        "等待联机核心启动超时{}",
        last_err.map(|e| format!("（端口文件解析失败: {}）", e)).unwrap_or_default()
    )))
}

// ---------------------------------------------------------------------------
// 本地 HTTP API
// ---------------------------------------------------------------------------

/// 查询核心状态（未就绪/进程异常时返回 Ok(None)）
pub async fn fetch_state(port: u16) -> Result<Option<TerracottaState>> {
    let client = http_client()?;
    let resp = client
        .get(format!("http://127.0.0.1:{}/state", port))
        .send()
        .await
        .map_err(|e| LauncherError::HttpError(e))?;
    if !resp.status().is_success() {
        return Ok(None);
    }
    let text = resp.text().await?;
    let raw: RawState = serde_json::from_str(&text)?;
    Ok(Some(TerracottaState::from_raw(raw)))
}

/// 触发「等待」状态（停止创建/加入，回到空闲）
pub async fn set_waiting(port: u16) -> Result<()> {
    get_ok(&format!("http://127.0.0.1:{}/state/ide", port)).await
}

/// 同步版等待（供进程关闭时调用，不可失败阻塞）
fn set_waiting_sync(port: u16) -> Result<()> {
    let client = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(3))
        .build()?;
    client
        .get(format!("http://127.0.0.1:{}/state/ide", port))
        .send()?;
    Ok(())
}

/// 触发「创建房间」扫描（游戏内需先开启局域网共享）
pub async fn set_scanning(port: u16, player: &str, nodes: &[String]) -> Result<()> {
    let mut url = format!("http://127.0.0.1:{}/state/scanning", port);
    url = append_query(&url, &[("player", player)]);
    for node in nodes {
        url = append_query(&url, &[("public_nodes", node)]);
    }
    get_ok(&url).await
}

/// 触发「加入房间」
pub async fn set_guesting(port: u16, room: &str, player: &str, nodes: &[String]) -> Result<()> {
    let mut url = format!("http://127.0.0.1:{}/state/guesting", port);
    url = append_query(&url, &[("room", room), ("player", player)]);
    for node in nodes {
        url = append_query(&url, &[("public_nodes", node)]);
    }
    get_ok(&url).await
}

/// 拉取核心日志
pub async fn fetch_logs(port: u16) -> Result<String> {
    let client = http_client()?;
    let resp = client
        .get(format!("http://127.0.0.1:{}/log?fetch=true", port))
        .send()
        .await
        .map_err(|e| LauncherError::HttpError(e))?;
    Ok(resp.text().await?)
}

async fn get_ok(url: &str) -> Result<()> {
    let client = http_client()?;
    let resp = client.get(url).send().await?;
    if resp.status().is_success() {
        Ok(())
    } else {
        Err(LauncherError::DownloadError(format!(
            "联机核心返回 HTTP {}",
            resp.status()
        )))
    }
}

/// 查询参数编码集：RFC 3986 unreserved 字符（`-` `.` `_` `~`）不编码，其余非字母数字均编码
const QUERY_ENCODE_SET: &percent_encoding::AsciiSet = &percent_encoding::NON_ALPHANUMERIC
    .remove(b'-')
    .remove(b'.')
    .remove(b'_')
    .remove(b'~');

/// 追加查询参数（不重复追加同名参数）
fn append_query(url: &str, pairs: &[(&str, &str)]) -> String {
    let mut out = String::with_capacity(url.len() + 64);
    let mut first = !url.contains('?');
    for (k, v) in pairs {
        if v.is_empty() {
            continue;
        }
        out.push_str(if first { "?" } else { "&" });
        first = false;
        out.push_str(k);
        out.push('=');
        out.push_str(&percent_encoding::utf8_percent_encode(v, QUERY_ENCODE_SET).to_string());
    }
    format!("{}{}", url, out)
}

// ---------------------------------------------------------------------------
// 状态模型
// ---------------------------------------------------------------------------

/// 联机核心返回的原始状态（宽容解析，字段可能缺失）
#[derive(Debug, Clone, Deserialize)]
pub struct RawState {
    pub state: String,
    #[allow(dead_code)]
    pub index: Option<i64>,
    pub room: Option<String>,
    pub profiles: Option<Vec<serde_json::Value>>,
    pub url: Option<String>,
    pub difficulty: Option<serde_json::Value>,
    #[serde(rename = "type")]
    pub exception_type: Option<i32>,
}

/// 推送给前端的联机状态
#[derive(Debug, Clone, Serialize)]
#[serde(tag = "state", rename_all = "kebab-case")]
pub enum TerracottaState {
    /// 空闲（核心就绪，未在联机）
    Waiting,
    /// 创建房间中（扫描局域网世界）
    HostScanning,
    /// 房间启动中
    HostStarting,
    /// 创建成功（room = 邀请码）
    HostOk { room: String, profiles: Vec<serde_json::Value> },
    /// 加入房间连接中
    GuestConnecting,
    /// 已进入虚拟局域网（difficulty = P2P 难度提示）
    GuestStarting { difficulty: String },
    /// 加入成功（可在游戏局域网列表看到房主世界）
    GuestOk { url: String, profiles: Vec<serde_json::Value> },
    /// 联机异常（type = 错误类型标识）
    Exception { r#type: String, message: String },
}

impl TerracottaState {
    /// 从原始状态映射（未知 state 值回退为 Waiting）
    fn from_raw(raw: RawState) -> Self {
        match raw.state.as_str() {
            "host-scanning" => TerracottaState::HostScanning,
            "host-starting" => TerracottaState::HostStarting,
            "host-ok" => TerracottaState::HostOk {
                room: raw.room.unwrap_or_default(),
                profiles: raw.profiles.unwrap_or_default(),
            },
            "guest-connecting" => TerracottaState::GuestConnecting,
            "guest-starting" => TerracottaState::GuestStarting {
                difficulty: difficulty_label(raw.difficulty),
            },
            "guest-ok" => TerracottaState::GuestOk {
                url: raw.url.unwrap_or_default(),
                profiles: raw.profiles.unwrap_or_default(),
            },
            "exception" => {
                let t = raw.exception_type.unwrap_or(-1);
                TerracottaState::Exception {
                    r#type: exception_label(t).to_string(),
                    message: exception_message(t).to_string(),
                }
            }
            _ => TerracottaState::Waiting,
        }
    }
}

/// P2P 难度标识（int / 字符串归一化）
fn difficulty_label(v: Option<serde_json::Value>) -> String {
    let s = match v {
        Some(serde_json::Value::Number(n)) => match n.as_u64() {
            Some(1) => return "easiest".into(),
            Some(2) => return "simple".into(),
            Some(3) => return "medium".into(),
            Some(4) => return "tough".into(),
            _ => return "unknown".into(),
        },
        Some(serde_json::Value::String(s)) => s,
        _ => return "unknown".into(),
    };
    match s.to_ascii_lowercase().as_str() {
        "easiest" => "easiest",
        "simple" => "simple",
        "medium" => "medium",
        "tough" => "tough",
        _ => "unknown",
    }
    .into()
}

/// 异常类型标识（对应核心 JSON 中 `type` 的整数值）
fn exception_label(t: i32) -> &'static str {
    match t {
        0 => "ping_host_fail",
        1 => "ping_host_rst",
        2 => "guest_et_crash",
        3 => "host_et_crash",
        4 => "ping_server_rst",
        5 => "scaffolding_invalid_response",
        _ => "unknown",
    }
}

/// 异常人类可读描述（前端可直接展示）
fn exception_message(t: i32) -> &'static str {
    match t {
        0 => "无法连接房主（P2P 打洞失败）",
        1 => "与房主的连接被重置",
        2 => "联机核心（客户端侧）异常退出",
        3 => "联机核心（房主侧）异常退出",
        4 => "与服务器的连接被重置",
        5 => "联机服务端响应无效",
        _ => "联机发生未知错误",
    }
}

// ---------------------------------------------------------------------------
// 测试
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_sanitize_tar_entry_ok() {
        assert_eq!(sanitize_tar_entry("terracotta.exe").unwrap(), "terracotta.exe");
        assert_eq!(sanitize_tar_entry("./dll/foo.dll").unwrap(), "dll/foo.dll");
        assert_eq!(sanitize_tar_entry("a/b/c.txt").unwrap(), "a/b/c.txt");
    }

    #[test]
    fn test_sanitize_tar_entry_rejects_traversal() {
        assert!(sanitize_tar_entry("../evil.txt").is_err());
        assert!(sanitize_tar_entry("a/../../evil.txt").is_err());
        assert!(sanitize_tar_entry("/abs/path").is_err());
        assert!(sanitize_tar_entry("C:/windows/x").is_err());
        assert!(sanitize_tar_entry("").is_err());
    }

    #[test]
    fn test_parse_waiting() {
        let raw: RawState = serde_json::from_str(r#"{"state":"waiting","index":3}"#).unwrap();
        let s = TerracottaState::from_raw(raw);
        assert!(matches!(s, TerracottaState::Waiting));
    }

    #[test]
    fn test_parse_host_ok() {
        let raw: RawState = serde_json::from_str(
            r#"{"state":"host-ok","index":4,"room":"U/AAAA-BBBB-CCCC-DDDD","profiles":[]}"#,
        )
        .unwrap();
        match TerracottaState::from_raw(raw) {
            TerracottaState::HostOk { room, .. } => {
                assert_eq!(room, "U/AAAA-BBBB-CCCC-DDDD")
            }
            other => panic!("应为 HostOk，实际 {:?}", other),
        }
    }

    #[test]
    fn test_parse_guest_starting_difficulty() {
        let raw: RawState =
            serde_json::from_str(r#"{"state":"guest-starting","index":5,"difficulty":2}"#).unwrap();
        match TerracottaState::from_raw(raw) {
            TerracottaState::GuestStarting { difficulty } => {
                assert_eq!(difficulty, "simple")
            }
            other => panic!("应为 GuestStarting，实际 {:?}", other),
        }
    }

    #[test]
    fn test_parse_exception() {
        let raw: RawState =
            serde_json::from_str(r#"{"state":"exception","index":6,"type":0}"#).unwrap();
        match TerracottaState::from_raw(raw) {
            TerracottaState::Exception { r#type, .. } => {
                assert_eq!(r#type, "ping_host_fail")
            }
            other => panic!("应为 Exception，实际 {:?}", other),
        }
    }

    #[test]
    fn test_filter_nodes() {
        let entries = vec![
            NodeEntry { url: "https://a.example".into(), region: None },
            NodeEntry { url: "https://cn.example".into(), region: Some("CN".into()) },
            NodeEntry { url: "https://us.example".into(), region: Some("US".into()) },
        ];
        // 国内用户：保留全球 + 中国
        let cn = filter_nodes(entries.clone(), true);
        assert_eq!(cn.len(), 3);
        // 海外用户：保留全球 + 非中国
        let intl = filter_nodes(entries, false);
        assert_eq!(intl.len(), 2);
        assert!(intl.iter().all(|u| u != "https://cn.example"));
    }

    #[test]
    fn test_bundle_file_name() {
        let name = bundle_file_name().unwrap();
        assert!(name.starts_with("terracotta-0.4.2-"));
        assert!(name.ends_with("-pkg.tar.gz"));
    }

    #[test]
    fn test_append_query() {
        let url = append_query("http://127.0.0.1:123/state/scanning", &[("player", "张三")]);
        assert!(url.contains("player=%E5%BC%A0%E4%B8%89"));
        assert!(!url.contains('&'));
        let url2 = append_query(&url, &[("public_nodes", "https://n.example")]);
        assert!(url2.contains('&'));
        assert!(url2.contains("public_nodes=https%3A%2F%2Fn.example"));
    }
}
