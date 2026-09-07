use crate::error::{LauncherError, Result};
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::{Mutex as StdMutex, OnceLock};
use std::time::Duration;

// ============================================================================
// 进程级内存缓存（L1）
// ============================================================================

/// 进程级 Java 检测缓存：检测成功后复用，避免每次全盘搜索
static JAVA_CACHE: OnceLock<StdMutex<Option<JavaRuntime>>> = OnceLock::new();

/// 写入 Java 检测缓存
pub fn cache_java(runtime: &JavaRuntime) {
    let m = JAVA_CACHE.get_or_init(|| StdMutex::new(None));
    if let Ok(mut g) = m.lock() {
        *g = Some(runtime.clone());
    }
}

/// 读取 Java 检测缓存（未检测过返回 None）
pub fn cached_java() -> Option<JavaRuntime> {
    let m = JAVA_CACHE.get()?;
    m.lock().ok().and_then(|g| g.clone())
}

/// 清除 Java 检测缓存（配置变化或手动刷新时调用；同时删除磁盘缓存）
pub fn clear_java_cache() {
    if let Some(m) = JAVA_CACHE.get() {
        if let Ok(mut g) = m.lock() {
            *g = None;
        }
    }
    // 同时删除磁盘缓存文件
    if let Some(path) = disk_cache_path() {
        let _ = std::fs::remove_file(&path);
    }
}

// ============================================================================
// 持久化磁盘缓存（L2）
// ============================================================================

/// 磁盘缓存条目（可序列化版本）
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
struct CachedJavaEntry {
    runtime: JavaRuntime,
    /// 缓存时间戳（Unix 秒），用于 LRU 淘汰
    cached_at: u64,
}

/// 磁盘缓存文件结构
#[derive(Debug, Default, serde::Serialize, serde::Deserialize)]
struct JavaCacheStore {
    entries: Vec<CachedJavaEntry>,
}

/// 磁盘缓存文件路径
fn disk_cache_path() -> Option<PathBuf> {
    let dir = dirs::cache_dir()?.join("starcloud-minecraft");
    Some(dir.join("java_runtimes.json"))
}

/// 当前时间戳（Unix 秒）
fn now_secs() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

/// 从磁盘缓存加载 Java 运行时列表（校验 executable 是否仍存在）
fn load_disk_cache() -> Vec<JavaRuntime> {
    let path = match disk_cache_path() {
        Some(p) if p.exists() => p,
        _ => return Vec::new(),
    };
    let data = match std::fs::read_to_string(&path) {
        Ok(d) => d,
        Err(_) => return Vec::new(),
    };
    let store: JavaCacheStore = match serde_json::from_str(&data) {
        Ok(s) => s,
        Err(_) => {
            // 缓存损坏，删除文件
            let _ = std::fs::remove_file(&path);
            return Vec::new();
        }
    };
    store
        .entries
        .into_iter()
        .filter(|e| e.runtime.executable.is_file())
        .map(|e| e.runtime)
        .collect()
}

/// 写入磁盘缓存（合并现有条目，保留最多 20 条，LRU 淘汰，原子写入）
fn save_disk_cache(runtimes: &[JavaRuntime]) {
    let path = match disk_cache_path() {
        Some(p) => p,
        None => return,
    };
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    // 加载现有缓存，合并新条目
    let mut store: JavaCacheStore = if path.exists() {
        std::fs::read_to_string(&path)
            .ok()
            .and_then(|d| serde_json::from_str(&d).ok())
            .unwrap_or_default()
    } else {
        JavaCacheStore::default()
    };

    let now = now_secs();
    for rt in runtimes {
        // 移除同路径的旧条目
        store.entries.retain(|e| e.runtime.executable != rt.executable);
        store.entries.push(CachedJavaEntry {
            runtime: rt.clone(),
            cached_at: now,
        });
    }

    // 按 cached_at 降序排序，保留最多 20 条
    store
        .entries
        .sort_by(|a, b| b.cached_at.cmp(&a.cached_at));
    store.entries.truncate(20);

    // 原子写入：先写 .tmp，再 rename
    if let Ok(json) = serde_json::to_string(&store) {
        let tmp = path.with_extension("tmp");
        let _ = std::fs::write(&tmp, json);
        let _ = std::fs::rename(&tmp, &path);
    }
}

/// 从磁盘缓存中查找满足版本要求的最佳 Java
fn lookup_disk_cache(min_version: u32) -> Option<JavaRuntime> {
    let entries = load_disk_cache();
    pick_best(&entries, min_version)
}

// ============================================================================
// 数据类型
// ============================================================================

/// Java 运行时信息
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct JavaRuntime {
    /// Java 可执行文件路径
    pub executable: PathBuf,
    /// Java 版本号（主版本，如 8, 17, 21）
    pub major_version: u32,
    /// Java 完整版本字符串
    pub version_string: String,
    /// Java Home 目录
    pub java_home: PathBuf,
    /// 是否为 64 位
    pub is_64bit: bool,
}

/// Java 检测器 - 查找系统中可用的 Java 运行时
///
/// 搜索策略（参考 PCL 的 Java 搜索算法）：
/// 1. 系统索引（Windows 注册表 JavaSoft / Eclipse Adoptium / Microsoft 等）→ 有索引直接读 JavaHome
/// 2. 环境变量 JAVA_HOME / JRE_HOME
/// 3. PATH 中的 java / java.exe / javaw.exe
/// 4. 用户额外指定的搜索路径
/// 5. 预设常见安装目录（多盘符 Program Files / (x86) + 各大厂商目录 + LOCALAPPDATA）
/// 6. Linux / macOS 常见 JVM 目录
///
/// 候选排序：64 位优先，版本降序（PCL 风格）。
pub struct JavaDetector {
    /// 额外的 Java 搜索路径
    extra_paths: Vec<PathBuf>,
    /// 最小 Java 版本要求
    min_version: u32,
}

impl JavaDetector {
    /// 创建 Java 检测器
    pub fn new() -> Self {
        Self {
            extra_paths: Vec::new(),
            min_version: 8,
        }
    }

    /// 添加额外的 Java 搜索路径
    pub fn with_extra_path(mut self, path: impl Into<PathBuf>) -> Self {
        self.extra_paths.push(path.into());
        self
    }

    /// 设置最小版本要求
    pub fn with_min_version(mut self, version: u32) -> Self {
        self.min_version = version;
        self
    }

    /// 自动检测系统中最合适的 Java
    ///
    /// 检测策略：
    /// - Phase 1：快速源并行搜索（注册表/环境变量/PATH/额外路径），mpsc 收集
    /// - Phase 2：并行版本解析（工作窃取分片），pick_best 选出最佳候选
    /// - Phase 3（兜底）：仅快速源未命中时执行慢速源并行搜索
    pub fn detect(&self) -> Result<JavaRuntime> {
        let mut seen = HashSet::new();
        let mut all_exes: Vec<PathBuf> = Vec::new();

        // Phase 1: 快速源并行搜索
        for exe in self.search_fast_sources_parallel() {
            let norm = normalize_path(&exe);
            if seen.insert(norm) {
                all_exes.push(exe);
            }
        }

        // Phase 2: 并行版本解析快速源候选
        if !all_exes.is_empty() {
            let runtimes = parse_versions_parallel(&all_exes);
            if let Some(best) = pick_best(&runtimes, self.min_version) {
                save_disk_cache(&[best.clone()]);
                return Ok(best);
            }
        }

        // Phase 3: 慢速源兜底（仅快速源无满足条件的结果时）
        let before_slow = all_exes.len();
        for exe in self.search_slow_sources_parallel() {
            let norm = normalize_path(&exe);
            if seen.insert(norm) {
                all_exes.push(exe);
            }
        }

        // 慢速源有新候选，重新解析
        if all_exes.len() > before_slow {
            let runtimes = parse_versions_parallel(&all_exes);
            if let Some(best) = pick_best(&runtimes, self.min_version) {
                save_disk_cache(&[best.clone()]);
                return Ok(best);
            }
        }

        Err(LauncherError::JavaNotFound(self.min_version))
    }

    /// 检测 Java（三级缓存）：L1 内存 → L2 磁盘 → L3 完整搜索
    /// 未找到时返回 JavaNotFound 错误
    pub fn detect_or_cached(&self) -> Result<JavaRuntime> {
        // L1: 进程级内存缓存
        if let Some(runtime) = cached_java() {
            if runtime.major_version >= self.min_version && runtime.executable.is_file() {
                log::info!(
                    "Java 检测命中内存缓存: {} (版本 {}{})",
                    runtime.version_string,
                    runtime.major_version,
                    if runtime.is_64bit { ", 64 位" } else { ", 32 位" }
                );
                return Ok(runtime);
            }
        }

        // L2: 持久化磁盘缓存
        if let Some(runtime) = lookup_disk_cache(self.min_version) {
            log::info!(
                "Java 检测命中磁盘缓存: {} (版本 {}{})",
                runtime.version_string,
                runtime.major_version,
                if runtime.is_64bit { ", 64 位" } else { ", 32 位" }
            );
            cache_java(&runtime); // 回填 L1
            return Ok(runtime);
        }

        // L3: 完整搜索
        let runtime = self.detect()?;
        cache_java(&runtime);
        Ok(runtime)
    }

    /// 检测 Java，找不到时自动下载（异步）
    ///
    /// 流程：三级缓存 → 完整搜索 → 自动下载 Adoptium JRE
    pub async fn detect_or_download(&self) -> Result<JavaRuntime> {
        // 先尝试本地检测
        match self.detect_or_cached() {
            Ok(rt) => return Ok(rt),
            Err(LauncherError::JavaNotFound(min)) => {
                log::warn!("未找到 Java >= {}，开始自动下载...", min);
            }
            Err(e) => return Err(e),
        }

        // 自动下载
        let downloader = crate::launch::java_download::JavaDownloader::new()?;
        let runtime = downloader.auto_download(self.min_version).await?;
        cache_java(&runtime);
        // 写入磁盘缓存，重启后可直接命中（无需再次联网下载）
        save_disk_cache(&[runtime.clone()]);
        Ok(runtime)
    }

    /// 完整搜索并返回所有可用运行时（快速源 + 慢速源，按版本降序），
    /// 不做「挑最佳」——供 Java 管理器列表与多 Java 智能匹配使用。
    ///
    /// 包含：系统注册表 / JAVA_HOME / PATH / 启动器自带下载目录等全部来源。
    pub fn detect_all(&self) -> Vec<JavaRuntime> {
        let mut seen = HashSet::new();
        let mut all_exes: Vec<PathBuf> = Vec::new();
        for exe in self.search_fast_sources_parallel() {
            let norm = normalize_path(&exe);
            if seen.insert(norm) {
                all_exes.push(exe);
            }
        }
        for exe in self.search_slow_sources_parallel() {
            let norm = normalize_path(&exe);
            if seen.insert(norm) {
                all_exes.push(exe);
            }
        }
        let mut runtimes = parse_versions_parallel(&all_exes);
        runtimes.sort_by(|a, b| {
            b.major_version
                .cmp(&a.major_version)
                .then(b.is_64bit.cmp(&a.is_64bit))
        });
        runtimes
    }

    /// 检测指定路径的 Java
    ///
    /// 支持两种形式：
    /// - 直接指向可执行文件（如 `.../bin/java.exe`）
    /// - 指向 Java 安装目录（自动补全 `bin/java(.exe)`）
    pub fn detect_at(path: &Path) -> Result<JavaRuntime> {
        let exe = if path.is_file() {
            path.to_path_buf()
        } else {
            get_java_exe(path).ok_or_else(|| LauncherError::JavaNotFound(0))?
        };
        if !exe.exists() {
            return Err(LauncherError::JavaNotFound(0));
        }
        parse_java_version(&exe)
    }
}

impl Default for JavaDetector {
    fn default() -> Self {
        Self::new()
    }
}

// ============================================================================
// 并行搜索方法
// ============================================================================

impl JavaDetector {
    /// Phase 1: 快速源并行搜索（std::thread::scope 并发 4 线程）
    fn search_fast_sources_parallel(&self) -> Vec<PathBuf> {
        let (tx, rx) = std::sync::mpsc::channel();

        std::thread::scope(move |s| {
            // Thread A: Windows 注册表
            #[cfg(target_os = "windows")]
            s.spawn({
                let tx = tx.clone();
                move || {
                    for home in registry_java_homes() {
                        if let Some(exe) = get_java_exe(&home) {
                            let _ = tx.send(exe);
                        }
                    }
                }
            });

            // Thread B: 环境变量 JAVA_HOME / JRE_HOME
            s.spawn({
                let tx = tx.clone();
                move || {
                    for var in ["JAVA_HOME", "JRE_HOME"] {
                        if let Ok(home) = std::env::var(var) {
                            if let Some(exe) = get_java_exe(Path::new(&home)) {
                                let _ = tx.send(exe);
                            }
                        }
                    }
                }
            });

            // Thread C: PATH 中的 java / javaw
            s.spawn({
                let tx = tx.clone();
                move || {
                    for exe in java_exe_from_path() {
                        let _ = tx.send(exe);
                    }
                }
            });

            // Thread D: 用户额外路径
            let extra = &self.extra_paths;
            if !extra.is_empty() {
                s.spawn({
                    let tx = tx.clone();
                    move || {
                        for path in extra {
                            if let Some(exe) = get_java_exe(path) {
                                let _ = tx.send(exe);
                            }
                        }
                    }
                });
            }

            // Thread E: 启动器自带 Java（自动下载目录 `{data_dir}/starcloud-launcher/java/{version}/`）
            s.spawn({
                let tx = tx.clone();
                move || {
                    for exe in launcher_bundled_java_exes() {
                        let _ = tx.send(exe);
                    }
                }
            });
            // tx 在 scope 闭包返回时 drop，rx 自动结束
        });

        rx.into_iter().collect()
    }

    /// Phase 3: 慢速源并行搜索（仅快速源无结果时，并发 2 线程）
    fn search_slow_sources_parallel(&self) -> Vec<PathBuf> {
        let (tx, rx) = std::sync::mpsc::channel();

        std::thread::scope(move |s| {
            // Thread A: Uninstall 注册表
            #[cfg(target_os = "windows")]
            s.spawn({
                let tx = tx.clone();
                move || {
                    for home in uninstall_java_homes() {
                        if let Some(exe) = get_java_exe(&home) {
                            let _ = tx.send(exe);
                        }
                    }
                }
            });

            // Thread B: 预设目录扫描
            s.spawn({
                let tx = tx.clone();
                move || {
                    #[cfg(target_os = "windows")]
                    {
                        for dir in preset_install_dirs() {
                            if dir.is_dir() {
                                for exe in scan_dir_for_java_exes(&dir) {
                                    let _ = tx.send(exe);
                                }
                            }
                        }
                    }
                    #[cfg(any(target_os = "linux", target_os = "macos"))]
                    {
                        let common_paths = [
                            "/usr/lib/jvm",
                            "/usr/local/lib/jvm",
                            "/opt",
                            "/Library/Java/JavaVirtualMachines",
                        ];
                        for base_path in &common_paths {
                            let base = PathBuf::from(base_path);
                            if base.is_dir() {
                                for exe in scan_dir_for_java_exes(&base) {
                                    let _ = tx.send(exe);
                                }
                            }
                        }
                    }
                }
            });
            // tx 在 scope 闭包返回时 drop，rx 自动结束
        });

        rx.into_iter().collect()
    }
}

// ============================================================================
// 并行版本解析
// ============================================================================

/// 并行解析多个 Java 候选的版本（工作窃取分片）
fn parse_versions_parallel(exes: &[PathBuf]) -> Vec<JavaRuntime> {
    if exes.is_empty() {
        return Vec::new();
    }
    if exes.len() == 1 {
        return parse_java_version(&exes[0]).into_iter().collect();
    }

    let num_threads = std::thread::available_parallelism()
        .map(|n| n.get().min(8))
        .unwrap_or(4)
        .min(exes.len());

    let (tx, rx) = std::sync::mpsc::channel();

    std::thread::scope(move |s| {
        for t in 0..num_threads {
            let tx = tx.clone();
            s.spawn(move || {
                // 工作窃取分片：每个线程处理 step_by 分片
                for i in (t..exes.len()).step_by(num_threads) {
                    if let Ok(rt) = parse_java_version(&exes[i]) {
                        let _ = tx.send(rt);
                    }
                }
            });
        }
        // tx 在 scope 闭包返回时 drop，rx 自动结束
    });

    rx.into_iter().collect()
}

/// 运行命令并收集输出，带超时保护（mpsc + recv_timeout，零 CPU 轮询）
///
/// 超时后强制结束子进程并返回 None。
fn run_with_timeout(cmd: &mut std::process::Command, timeout: Duration) -> Option<std::process::Output> {
    let child = cmd
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .spawn()
        .ok()?;

    let (tx, rx) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let output = child.wait_with_output();
        let _ = tx.send(output);
    });

    match rx.recv_timeout(timeout) {
        Ok(Ok(output)) => Some(output),
        _ => None,
    }
}

/// 简单路径规范化，替代昂贵的 canonicalize()（纯字符串操作，避免文件系统调用）
fn normalize_path(path: &Path) -> PathBuf {
    let mut components = Vec::new();
    for c in path.components() {
        match c {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => {
                components.pop();
            }
            other => components.push(other.as_os_str().to_os_string()),
        }
    }
    let result: PathBuf = components.iter().collect();
    #[cfg(target_os = "windows")]
    {
        // Windows: 小写化实现大小写不敏感去重
        PathBuf::from(result.to_string_lossy().to_lowercase())
    }
    #[cfg(not(target_os = "windows"))]
    {
        result
    }
}

/// 从候选中挑选最佳：64 位优先，版本降序（PCL 风格），且需满足最小版本
fn pick_best(candidates: &[JavaRuntime], min_version: u32) -> Option<JavaRuntime> {
    candidates
        .iter()
        .filter(|r| r.major_version >= min_version)
        .max_by(|a, b| {
            b.is_64bit
                .cmp(&a.is_64bit)
                .then(b.major_version.cmp(&a.major_version))
        })
        .cloned()
}

/// 多 Java 场景下的智能匹配：满足最低版本要求的前提下，
/// 优先选「版本最接近要求」的运行时（老版本游戏跑老 Java 更稳），64 位优先。
///
/// 例：要求 Java 17，已装 [8, 17, 21] → 选 17；已装 [8, 21] → 选 21。
pub fn pick_for(candidates: &[JavaRuntime], min_version: u32) -> Option<JavaRuntime> {
    candidates
        .iter()
        .filter(|r| r.major_version >= min_version)
        .min_by_key(|r| (r.major_version, !r.is_64bit))
        .cloned()
}

/// 获取操作系统对应的 Java 可执行文件（优先 javaw，回退 java）
pub fn get_java_exe(dir: &Path) -> Option<PathBuf> {
    let bin = dir.join("bin");
    let names: &[&str] = if cfg!(target_os = "windows") {
        &["javaw.exe", "java.exe"]
    } else {
        &["java"]
    };
    for name in names {
        let exe = bin.join(name);
        if exe.is_file() {
            return Some(exe);
        }
    }
    None
}

/// 扫描目录：先检查目录自身，再扫描所有直接子目录，收集含 bin/java 的 Java 运行时
fn scan_dir_for_java_exes(root: &Path) -> Vec<PathBuf> {
    let mut exes = Vec::new();
    // 目录本身可能就是 Java Home（如 Android Studio 的 jbr）
    if let Some(exe) = get_java_exe(root) {
        exes.push(exe);
    }
    let Ok(entries) = std::fs::read_dir(root) else {
        return exes;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_dir() {
            continue;
        }
        if let Some(exe) = get_java_exe(&path) {
            exes.push(exe);
        }
    }
    exes
}

/// 从 PATH 环境变量中查找 java / java.exe / javaw.exe
fn java_exe_from_path() -> Vec<PathBuf> {
    let mut out = Vec::new();
    let Ok(path_var) = std::env::var("PATH") else {
        return out;
    };
    let names: &[&str] = if cfg!(target_os = "windows") {
        &["java.exe", "javaw.exe"]
    } else {
        &["java"]
    };
    for dir in std::env::split_paths(&path_var) {
        for name in names {
            let exe = dir.join(name);
            if exe.is_file() {
                out.push(exe);
            }
        }
    }
    out
}

/// 启动器自带 Java（自动下载目录）：`{data_dir}/starcloud-launcher/java/{version}/bin/java(.exe)`
///
/// 使 `detect_or_download` 下载的 Java 在下次启动时能被直接找到，
/// 避免每次重启都要重新下载或提示未检测到 Java。
fn launcher_bundled_java_exes() -> Vec<PathBuf> {
    let mut out = Vec::new();
    let root = dirs::data_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("starcloud-launcher")
        .join("java");
    if !root.is_dir() {
        return out;
    }
    // 目录本身可能是 Java Home
    if let Some(exe) = get_java_exe(&root) {
        out.push(exe);
    }
    if let Ok(entries) = std::fs::read_dir(&root) {
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                if let Some(exe) = get_java_exe(&path) {
                    out.push(exe);
                }
            }
        }
    }
    out
}

/// 列出系统逻辑盘符（从 C: 开始，跳过 A:/B: 避免软驱卡顿）
#[cfg(target_os = "windows")]
fn logical_drives() -> Vec<String> {
    let mut drives = Vec::new();
    for c in b'C'..=b'Z' {
        let drive = format!("{}:\\", c as char);
        if Path::new(&drive).exists() {
            drives.push(drive);
        }
    }
    drives
}

/// Windows 预设常见 Java 安装基目录（多盘符 + 各厂商目录 + 用户级安装）
#[cfg(target_os = "windows")]
fn preset_install_dirs() -> Vec<PathBuf> {
    let mut dirs = Vec::new();
    let pf_names = ["Program Files", "Program Files (x86)"];
    let vendors = [
        "Java",
        "JavaSoft",
        "Eclipse Adoptium",
        "Eclipse Foundation",
        "AdoptOpenJDK",
        "Amazon Corretto",
        "Zulu",
        "BellSoft",
        "Microsoft",
    ];
    for drive in logical_drives() {
        for pf in &pf_names {
            let base = PathBuf::from(format!("{}{}", drive, pf));
            for vendor in &vendors {
                dirs.push(base.join(vendor));
            }
        }
    }
    // 用户级安装（JetBrains JBR、VSCode 内嵌 JRE 等）
    if let Ok(local_app_data) = std::env::var("LOCALAPPDATA") {
        dirs.push(PathBuf::from(local_app_data).join("Programs"));
    }
    // 捆绑 JetBrains Runtime：Android Studio / IntelliJ / IDEA 自带 JBR
    for env in ["ProgramFiles", "ProgramFiles(x86)"] {
        if let Ok(pf) = std::env::var(env) {
            dirs.push(PathBuf::from(pf).join("Android").join("Android Studio").join("jbr"));
        }
    }
    dirs
}

/// 从 Windows 注册表读取已注册的 Java Home（系统索引）
///
/// 覆盖 JavaSoft（Oracle）、Eclipse Adoptium（Temurin）、Microsoft OpenJDK、
/// Amazon Corretto、Azul Zulu、Red Hat、Semeru、GraalVM 等主流厂商，
/// 同时包含 32 位视图（WOW6432Node）与 HKCU 用户级注册。
/// 依赖系统自带的 reg.exe，无第三方依赖。
#[cfg(target_os = "windows")]
fn registry_java_homes() -> Vec<PathBuf> {
    let roots = [
        r"HKLM\SOFTWARE\JavaSoft\Java Runtime Environment",
        r"HKLM\SOFTWARE\JavaSoft\JDK",
        r"HKLM\SOFTWARE\JavaSoft\JRE",
        r"HKLM\SOFTWARE\JavaSoft\Java Development Kit",
        r"HKLM\SOFTWARE\Eclipse Adoptium\JDK",
        r"HKLM\SOFTWARE\Eclipse Adoptium\JRE",
        r"HKLM\SOFTWARE\Microsoft\JDK",
        r"HKLM\SOFTWARE\Microsoft\OpenJDK",
        r"HKLM\SOFTWARE\Amazon Corretto\JDK",
        r"HKLM\SOFTWARE\Amazon Corretto\JRE",
        r"HKLM\SOFTWARE\Azul Systems\Zulu",
        r"HKLM\SOFTWARE\Red Hat\Red Hat OpenJDK",
        r"HKLM\SOFTWARE\Semeru\Semeru JDK",
        r"HKLM\SOFTWARE\GraalVM\GraalVM CE",
        r"HKLM\SOFTWARE\WOW6432Node\JavaSoft\Java Runtime Environment",
        r"HKLM\SOFTWARE\WOW6432Node\JavaSoft\JDK",
        r"HKLM\SOFTWARE\WOW6432Node\JavaSoft\JRE",
        r"HKLM\SOFTWARE\WOW6432Node\Eclipse Adoptium\JDK",
        r"HKLM\SOFTWARE\WOW6432Node\Eclipse Adoptium\JRE",
        r"HKLM\SOFTWARE\WOW6432Node\Amazon Corretto\JDK",
        r"HKLM\SOFTWARE\WOW6432Node\Azul Systems\Zulu",
        r"HKCU\SOFTWARE\JavaSoft\Java Runtime Environment",
        r"HKCU\SOFTWARE\JavaSoft\JDK",
        r"HKCU\SOFTWARE\Eclipse Adoptium\JDK",
    ];
    let mut homes = Vec::new();
    for root in roots {
        // 根键自身可能直接带 JavaHome（如部分 Zulu / Corretto 注册）
        if let Some(home) = reg_java_home(root) {
            homes.push(home);
        }
        // 列出子键（reg query 输出中键路径以 HKEY_ 开头）
        let mut cmd = std::process::Command::new("reg");
        cmd.args(["query", root]);
        let Some(out) = run_with_timeout(&mut cmd, Duration::from_secs(5)) else {
            continue;
        };
        let text = String::from_utf8_lossy(&out.stdout);
        for line in text.lines() {
            let key = line.trim();
            if !key.starts_with("HKEY_") {
                continue;
            }
            if let Some(home) = reg_java_home(key) {
                homes.push(home);
            }
        }
    }
    homes
}

/// 读取单个注册表键的 JavaHome 值（REG_SZ）
#[cfg(target_os = "windows")]
fn reg_java_home(key: &str) -> Option<PathBuf> {
    let mut cmd = std::process::Command::new("reg");
    cmd.args(["query", key, "/v", "JavaHome"]);
    let out = run_with_timeout(&mut cmd, Duration::from_secs(5))?;
    let text = String::from_utf8_lossy(&out.stdout);
    for vline in text.lines() {
        if let Some((name, value)) = split_reg_value(vline.trim()) {
            if name.eq_ignore_ascii_case("JavaHome") && !value.is_empty() {
                return Some(PathBuf::from(value));
            }
        }
    }
    None
}

/// 从注册表「卸载信息」中搜索 Java 安装（兜底：未注册 JavaHome 的厂商与自定义安装）
///
/// 对 32/64 位 + 用户三棵 Uninstall 键各做一次 /s 递归查询，
/// 取带 JavaHome 的条目，或 DisplayName 含 Java 关键字且含 InstallLocation 的条目。
#[cfg(target_os = "windows")]
fn uninstall_java_homes() -> Vec<PathBuf> {
    let roots = [
        r"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        r"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        r"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    ];
    let mut homes = Vec::new();
    for root in roots {
        let mut cmd = std::process::Command::new("reg");
        cmd.args(["query", root, "/s"]);
        let Some(out) = run_with_timeout(&mut cmd, Duration::from_secs(20)) else {
            continue;
        };
        let text = String::from_utf8_lossy(&out.stdout);
        let mut java_home: Option<PathBuf> = None;
        let mut java_display = false;
        let mut install_location: Option<PathBuf> = None;
        let flush = |homes: &mut Vec<PathBuf>, java_home: &mut Option<PathBuf>,
                     java_display: &mut bool, install_location: &mut Option<PathBuf>| {
            if let Some(home) = java_home.take() {
                homes.push(home);
            } else if *java_display {
                if let Some(loc) = install_location.take() {
                    homes.push(loc);
                }
            }
            *java_display = false;
            *install_location = None;
        };
        for line in text.lines() {
            let trimmed = line.trim();
            if trimmed.starts_with("HKEY_") {
                // 键边界：结算上一个键
                flush(&mut homes, &mut java_home, &mut java_display, &mut install_location);
                continue;
            }
            if let Some((name, value)) = split_reg_value(trimmed) {
                if name.eq_ignore_ascii_case("JavaHome") && !value.is_empty() {
                    java_home = Some(PathBuf::from(value));
                } else if name.eq_ignore_ascii_case("InstallLocation") && !value.is_empty() {
                    install_location = Some(PathBuf::from(value));
                } else if name.eq_ignore_ascii_case("DisplayName") && is_java_display(&value) {
                    java_display = true;
                }
            }
        }
        flush(&mut homes, &mut java_home, &mut java_display, &mut install_location);
    }
    homes
}

/// DisplayName 是否像 Java 相关产品
fn is_java_display(name: &str) -> bool {
    let lower = name.to_ascii_lowercase();
    [
        "java", "openjdk", "temurin", "corretto", "zulu", "jre", "jdk", "jbr", "graalvm",
        "semeru", "microsoft build",
    ]
    .iter()
    .any(|k| lower.contains(k))
}

/// 从一行注册表值输出中解析 (值名, 数据)
///
/// 形如：`JavaHome    REG_SZ    C:\Program Files\Java\jdk-17`
fn split_reg_value(line: &str) -> Option<(String, String)> {
    let tokens: Vec<&str> = line.split_whitespace().collect();
    if tokens.len() < 3 || tokens[1] != "REG_SZ" {
        return None;
    }
    let data = tokens[2..].join(" ").trim_matches('"').to_string();
    if data.is_empty() {
        return None;
    }
    Some((tokens[0].to_string(), data))
}

/// 解析 Java 版本信息
fn parse_java_version(java_exe: &Path) -> Result<JavaRuntime> {
    let mut cmd = std::process::Command::new(java_exe);
    cmd.arg("-XshowSettings:properties")
        .arg("-version")
        .stderr(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped());
    let output = run_with_timeout(&mut cmd, Duration::from_secs(10)).ok_or_else(|| {
        LauncherError::LaunchError(format!("Java 检测超时: {}", java_exe.display()))
    })?;

    let stderr = String::from_utf8_lossy(&output.stderr);

    // 解析版本号
    // 优先取 -XshowSettings 输出中的 "java.version = 17.0.1"（格式规范）
    // 回退到标准首行 "java version \"1.8.0_371\"" / "openjdk version \"17.0.1\""
    let major_version =
        if let Some(line) = stderr.lines().find(|l| l.trim_start().starts_with("java.version")) {
            let cleaned = line.split('=').nth(1).unwrap_or(line);
            parse_major_version(cleaned)
        } else if let Some(line) = stderr.lines().find(|l| l.contains("version")) {
            let cleaned = line.split('"').nth(1).unwrap_or(line);
            parse_major_version(cleaned)
        } else {
            return Err(LauncherError::LaunchError("无法解析 Java 版本".to_string()));
        };

    // 检测是否为 64 位
    let is_64bit = stderr.contains("64-Bit") || stderr.contains("sun.arch.data.model = 64");

    let java_home = java_exe
        .parent()
        .and_then(|p| p.parent())
        .map(|p| p.to_path_buf())
        .unwrap_or_default();

    // 版本显示：优先取 java.version = 22.0.2，回退到输出首行
    let version_string = stderr
        .lines()
        .find(|l| l.trim_start().starts_with("java.version"))
        .and_then(|l| l.split('=').nth(1))
        .map(|v| v.trim().to_string())
        .or_else(|| stderr.lines().next().map(|l| l.trim().to_string()))
        .unwrap_or_else(|| "unknown".to_string());

    Ok(JavaRuntime {
        executable: java_exe.to_path_buf(),
        major_version,
        version_string,
        java_home,
        is_64bit,
    })
}

/// 从版本字符串中解析主版本号
fn parse_major_version(version_str: &str) -> u32 {
    let version_str = version_str.trim().trim_matches('"');
    // "1.8.0_301" -> 8, "17.0.1" -> 17
    if version_str.starts_with("1.") {
        // Java 8 及更早版本格式: 1.8.0_xxx
        let parts: Vec<&str> = version_str.splitn(3, '.').collect();
        if parts.len() >= 2 {
            parts[1].parse().unwrap_or(0)
        } else {
            8
        }
    } else {
        let parts: Vec<&str> = version_str.splitn(2, '.').collect();
        parts[0].parse().unwrap_or(0)
    }
}
