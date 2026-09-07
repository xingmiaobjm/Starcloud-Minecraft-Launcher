//! Java 运行时自动下载（Adoptium API v3）
//!
//! 流程：
//! 1. 调用 Adoptium API 查询可用版本列表
//! 2. 根据当前 OS/架构筛选合适的 JRE 二进制包
//! 3. 使用现有 HttpClient 流式下载（带多源回退 + 重试）
//! 4. 解压到本地目录 `{data_dir}/starcloud-launcher/java/{version}/`
//! 5. 返回 JavaRuntime 信息供检测器使用

use crate::download::http_client::HttpClient;
use crate::error::{LauncherError, Result};
use crate::launch::java::{JavaRuntime, get_java_exe};
use std::path::{Path, PathBuf};

/// Adoptium API v3 基础地址
const ADOPTIUM_API: &str = "https://api.adoptium.net/v3";

/// Java 下载存储根目录（相对于数据目录）
pub fn java_download_root() -> PathBuf {
    dirs::data_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("starcloud-launcher")
        .join("java")
}

/// 获取当前操作系统标识（Adoptium 格式）
fn adoptium_os() -> &'static str {
    if cfg!(target_os = "windows") {
        "windows"
    } else if cfg!(target_os = "macos") {
        "mac"
    } else if cfg!(target_os = "linux") {
        "linux"
    } else {
        std::env::consts::OS
    }
}

/// 获取当前架构标识（Adoptium 格式）
fn adoptium_arch() -> &'static str {
    match std::env::consts::ARCH {
        "x86_64" => "x64",
        "aarch64" => "aarch64",
        "x86" => "x86",
        "arm" => "arm",
        other => other,
    }
}

/// 获取当前 OS/架构的文件扩展名
fn archive_ext() -> &'static str {
    if cfg!(target_os = "windows") {
        "zip"
    } else {
        "tar.gz"
    }
}

// ── Adoptium API 响应数据结构 ─────────────────────────────────
// API v3 返回格式: /assets/latest/{version}/hotspot
// 返回一个数组，每项包含 binary 子对象（含 architecture / os / image_type / package）

/// 某个版本的某个二进制发布条目
#[derive(Debug, serde::Deserialize)]
struct AdoptiumRelease {
    binary: AdoptiumBinary,
}

/// 单个二进制包信息
#[derive(Debug, Clone, serde::Deserialize)]
struct AdoptiumBinary {
    /// 架构（x64 / aarch64 等）
    architecture: String,
    /// 操作系统（windows / linux / mac 等）
    os: String,
    /// 镜像类型（jdk / jre / debugimage 等）
    image_type: String,
    /// 安装包下载信息
    package: AdoptiumPackage,
}

/// 二进制包下载信息
#[derive(Debug, Clone, serde::Deserialize)]
struct AdoptiumPackage {
    /// 包名（如 OpenJDK25U-jre_x64_windows_hotspot_25.0.4_7.zip）
    name: String,
    /// 下载链接
    link: String,
    /// 文件大小（bytes）
    size: u64,
    /// SHA256 校验和
    #[allow(dead_code)]
    checksum: String,
}

/// 可用版本信息
#[derive(Debug, serde::Deserialize)]
struct AdoptiumVersionInfo {
    /// 版本数组
    available_releases: Vec<u32>,
    /// 最新 LTS 版本
    most_recent_lts: u32,
    /// 所有可用 LTS 版本
    available_lts_releases: Vec<u32>,
}

// ── Java 下载器 ────────────────────────────────────────────────

/// Java 自动下载器
pub struct JavaDownloader {
    /// HTTP 客户端
    http: HttpClient,
    /// 下载根目录
    root: PathBuf,
}

impl JavaDownloader {
    /// 创建 Java 下载器
    pub fn new() -> Result<Self> {
        Ok(Self {
            http: HttpClient::new()?,
            root: java_download_root(),
        })
    }

    /// 自定义下载根目录
    pub fn with_root(mut self, root: impl Into<PathBuf>) -> Self {
        self.root = root.into();
        self
    }

    // ── 查询可用版本 ──────────────────────────────────────────

    /// 获取 Adoptium 所有可用版本列表
    pub async fn fetch_available_versions(&self) -> Result<Vec<u32>> {
        let url = format!("{}/info/available_releases", ADOPTIUM_API);
        log::debug!("查询 Adoptium 可用版本: {}", url);

        let resp = self.http.get_json_with_retry::<AdoptiumVersionInfo>(&url).await?;
        log::info!(
            "Adoptium 可用版本: LTS={}, 共 {} 个版本",
            resp.most_recent_lts,
            resp.available_releases.len()
        );

        let mut versions = resp.available_releases;
        versions.sort_unstable();
        Ok(versions)
    }

    /// 获取符合最低版本要求的推荐版本（优先 LTS）
    pub async fn recommend_version(&self, min_version: u32) -> Result<u32> {
        let versions = self.fetch_available_versions().await?;
        let candidates: Vec<u32> = versions
            .into_iter()
            .filter(|&v| v >= min_version)
            .collect();

        if candidates.is_empty() {
            return Err(LauncherError::DownloadError(format!(
                "Adoptium 没有 >= {} 的 Java 版本可用",
                min_version
            )));
        }

        // 优先 LTS，否则取最新
        let info = self
            .http
            .get_json_with_retry::<AdoptiumVersionInfo>(&format!(
                "{}/info/available_releases",
                ADOPTIUM_API
            ))
            .await?;

        let lts = info
            .available_lts_releases
            .iter()
            .copied()
            .filter(|v| *v >= min_version)
            .max();

        let version = lts.unwrap_or(*candidates.last().unwrap());
        log::info!("推荐 Java 版本: {} (min={})", version, min_version);
        Ok(version)
    }

    // ── 下载和解压 ────────────────────────────────────────────

    /// 下载指定主版本的 Java JRE，返回解压后的 JavaRuntime
    ///
    /// 如果本地已存在，直接返回缓存路径。
    pub async fn download_java(&self, major_version: u32) -> Result<JavaRuntime> {
        let target_dir = self.root.join(major_version.to_string());

        // 检查是否已下载
        if let Some(exe) = get_java_exe(&target_dir) {
            if exe.is_file() {
                log::info!("Java {} 已存在: {}", major_version, target_dir.display());
                return crate::launch::java::JavaDetector::detect_at(&target_dir);
            }
        }

        log::info!("开始下载 Java {} ({} {})", major_version, adoptium_os(), adoptium_arch());

        // 1. 查询二进制包信息
        let binary = self
            .fetch_binary_info(major_version)
            .await?;

        // 2. 下载
        let archive_path = self
            .download_binary(&binary, major_version)
            .await?;

        // 3. 解压
        self.extract_java(&archive_path, &target_dir, major_version)?;

        // 4. 清理临时文件
        let _ = std::fs::remove_file(&archive_path);

        // 5. 验证并返回
        let runtime = crate::launch::java::JavaDetector::detect_at(&target_dir)?;
        log::info!(
            "Java {} 下载完成: {} ({})",
            major_version,
            runtime.version_string,
            runtime.executable.display()
        );
        Ok(runtime)
    }

    /// 查询 Adoptium API 获取匹配当前 OS/架构的 JRE 二进制包
    async fn fetch_binary_info(&self, major_version: u32) -> Result<AdoptiumBinary> {
        let url = format!(
            "{}/assets/latest/{}/hotspot",
            ADOPTIUM_API, major_version
        );
        log::debug!("查询二进制包: {}", url);

        let releases: Vec<AdoptiumRelease> = self.http.get_json_with_retry(&url).await?;

        let os = adoptium_os();
        let arch = adoptium_arch();

        // 优先匹配 JRE + 当前 OS/Arch
        let binary = releases
            .iter()
            .find_map(|r| {
                let b = &r.binary;
                if b.image_type == "jre" && b.os == os && b.architecture == arch {
                    Some(b.clone())
                } else {
                    None
                }
            })
            .or_else(|| {
                // 回退：JDK（体积更大但可用）
                releases.iter().find_map(|r| {
                    let b = &r.binary;
                    if b.os == os && b.architecture == arch {
                        Some(b.clone())
                    } else {
                        None
                    }
                })
            })
            .ok_or_else(|| {
                LauncherError::DownloadError(format!(
                    "Adoptium 没有 Java {} 的 {}-{} JRE 包",
                    major_version, os, arch
                ))
            })?;

        log::info!(
            "找到 Java {} 二进制包: {} ({:.1} MB)",
            major_version,
            binary.package.name,
            binary.package.size as f64 / 1_048_576.0
        );
        Ok(binary)
    }

    /// 流式下载二进制包到临时文件，返回临时文件路径
    async fn download_binary(
        &self,
        binary: &AdoptiumBinary,
        major_version: u32,
    ) -> Result<PathBuf> {
        let ext = archive_ext();
        let archive_name = format!("jdk-{}-{}-{}.{}", major_version, adoptium_os(), adoptium_arch(), ext);
        let archive_path = self.root.join(&archive_name);

        // 创建目录
        std::fs::create_dir_all(&self.root)?;

        log::info!(
            "下载 Java {}: {} → {} ({:.1} MB)",
            major_version,
            binary.package.link,
            archive_path.display(),
            binary.package.size as f64 / 1_048_576.0
        );

        let start = std::time::Instant::now();

        // 流式下载
        let bytes = self.http.get_bytes_with_retry(&binary.package.link).await?;

        let elapsed = start.elapsed();
        let speed = if elapsed.as_secs_f64() > 0.0 {
            (bytes.len() as f64 / elapsed.as_secs_f64() / 1_048_576.0) as u32
        } else {
            0
        };

        log::info!("下载完成: {} bytes ({:.1} MB/s)", bytes.len(), speed);

        // 写入文件
        std::fs::write(&archive_path, &bytes)?;

        Ok(archive_path)
    }

    /// 解压 Java 二进制包到目标目录
    fn extract_java(
        &self,
        archive_path: &Path,
        target_dir: &Path,
        major_version: u32,
    ) -> Result<()> {
        // 清理旧目录
        if target_dir.exists() {
            std::fs::remove_dir_all(target_dir)?;
        }
        std::fs::create_dir_all(target_dir)?;

        log::info!("解压 Java {} 到 {}", major_version, target_dir.display());

        if cfg!(target_os = "windows") {
            // Windows: ZIP 解压
            self.extract_zip(archive_path, target_dir)?;
        } else {
            // Linux/macOS: tar.gz 解压
            self.extract_tarball(archive_path, target_dir)?;
        }

        // Adoptium ZIP 解压后通常在 `jdk-{version}+{build}-jre/` 子目录下
        // 我们需要把内容提升到 target_dir（即 target_dir/bin/java.exe）
        self.flatten_one_level(target_dir, major_version)?;

        Ok(())
    }

    /// ZIP 解压
    fn extract_zip(&self, archive_path: &Path, target_dir: &Path) -> Result<()> {
        let file = std::fs::File::open(archive_path)?;
        let mut archive = zip::ZipArchive::new(file)?;

        for i in 0..archive.len() {
            let mut entry = archive.by_index(i)?;
            let name = entry.name().to_string();

            // 跳过目录条目
            if name.ends_with('/') {
                continue;
            }

            let dest = target_dir.join(&name);
            if let Some(parent) = dest.parent() {
                std::fs::create_dir_all(parent)?;
            }

            let mut output = std::fs::File::create(&dest)?;
            std::io::copy(&mut entry, &mut output)?;

            // 恢复可执行权限（Unix 类系统）
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                if let Some(mode) = entry.unix_mode() {
                    let _ = std::fs::set_permissions(&dest, std::fs::Permissions::from_mode(mode));
                }
            }
        }
        Ok(())
    }

    /// tar.gz 解压
    fn extract_tarball(&self, archive_path: &Path, target_dir: &Path) -> Result<()> {
        let file = std::fs::File::open(archive_path)?;
        let decoder = flate2::read::GzDecoder::new(file);
        let mut archive = tar::Archive::new(decoder);

        for entry in archive.entries()? {
            let mut entry = entry?;
            let path = entry.path()?.to_path_buf();

            // 跳过目录条目
            if path.as_os_str().is_empty() || entry.header().entry_type().is_dir() {
                continue;
            }

            let dest = target_dir.join(&path);
            if let Some(parent) = dest.parent() {
                std::fs::create_dir_all(parent)?;
            }

            entry.unpack(&dest)?;
        }
        Ok(())
    }

    /// 将解压后的一层子目录内容提升到 target_dir
    ///
    /// Adoptium 的 ZIP 解压后结构通常是：
    ///   target_dir/jdk-21.0.5+11-jre/bin/java.exe
    /// 我们需要变成：
    ///   target_dir/bin/java.exe
    fn flatten_one_level(&self, target_dir: &Path, _major_version: u32) -> Result<()> {
        // 检查 target_dir 下是否只有一个子目录
        let entries: Vec<_> = std::fs::read_dir(target_dir)?
            .filter_map(|e| e.ok())
            .filter(|e| e.path().is_dir())
            .collect();

        if entries.len() != 1 {
            // 已经扁平或没有子目录，不需要处理
            return Ok(());
        }

        let subdir = entries[0].path();
        let subdir_name = subdir
            .file_name()
            .map(|n| n.to_string_lossy().to_string())
            .unwrap_or_default();

        // 只处理看起来像 Adoptium 解压目录的（以 jdk-/jre-/OpenJDK 开头）
        if !subdir_name.starts_with("jdk")
            && !subdir_name.starts_with("jre")
            && !subdir_name.starts_with("OpenJDK")
        {
            return Ok(());
        }

        log::debug!("扁平化目录: {} → {}", subdir.display(), target_dir.display());

        // 移动子目录内容到父目录
        let sub_entries: Vec<_> = std::fs::read_dir(&subdir)?
            .filter_map(|e| e.ok())
            .collect();

        for entry in sub_entries {
            let src = entry.path();
            let name = entry.file_name();
            let dst = target_dir.join(&name);

            if dst.exists() {
                if dst.is_dir() {
                    std::fs::remove_dir_all(&dst)?;
                } else {
                    std::fs::remove_file(&dst)?;
                }
            }
            std::fs::rename(&src, &dst)?;
        }

        // 删除空子目录
        let _ = std::fs::remove_dir(&subdir);

        Ok(())
    }

    // ── 自动下载（对外接口）────────────────────────────────────

    /// 自动下载满足最低版本要求的 Java
    ///
    /// 1. 查询 Adoptium 可用版本
    /// 2. 选择推荐版本（优先 LTS）
    /// 3. 下载并解压
    /// 4. 返回 JavaRuntime
    pub async fn auto_download(&self, min_version: u32) -> Result<JavaRuntime> {
        let version = self.recommend_version(min_version).await?;
        self.download_java(version).await
    }

    /// 获取指定版本的已下载 Java 路径
    pub fn get_java_path(&self, major_version: u32) -> Option<PathBuf> {
        let dir = self.root.join(major_version.to_string());
        get_java_exe(&dir)
    }

    /// 列出所有已下载的 Java 版本
    pub fn list_downloaded(&self) -> Vec<u32> {
        let mut versions = Vec::new();
        if let Ok(entries) = std::fs::read_dir(&self.root) {
            for entry in entries.flatten() {
                let path = entry.path();
                if path.is_dir() {
                    if let Some(name) = path.file_name().and_then(|n| n.to_str()) {
                        if let Ok(v) = name.parse::<u32>() {
                            if get_java_exe(&path).is_some() {
                                versions.push(v);
                            }
                        }
                    }
                }
            }
        }
        versions.sort_unstable();
        versions
    }

    /// 列出所有已下载的 Java 运行时（带完整版本信息，供 Java 管理器展示）
    pub fn list_downloaded_runtimes(&self) -> Vec<JavaRuntime> {
        self.list_downloaded()
            .iter()
            .filter_map(|v| {
                let dir = self.root.join(v.to_string());
                crate::launch::java::JavaDetector::detect_at(&dir).ok()
            })
            .collect()
    }

    /// 删除已下载的 Java 版本（目录 + 进程/磁盘缓存同步清理，避免失效路径被复用）
    pub fn remove_java(&self, major_version: u32) -> Result<()> {
        let dir = self.root.join(major_version.to_string());
        if dir.exists() {
            std::fs::remove_dir_all(&dir)?;
        }
        crate::launch::java::clear_java_cache();
        log::info!("已删除 Java {}（{}）", major_version, dir.display());
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_adoptium_os_arch() {
        let os = adoptium_os();
        let arch = adoptium_arch();
        assert!(!os.is_empty());
        assert!(!arch.is_empty());
        // 常见组合
        assert!(os == "windows" || os == "mac" || os == "linux");
        assert!(arch == "x64" || arch == "aarch64" || arch == "x86" || arch == "arm");
    }

    #[test]
    fn test_java_download_root() {
        let root = java_download_root();
        assert!(root.to_string_lossy().contains("starcloud-launcher"));
        assert!(root.to_string_lossy().contains("java"));
    }

    #[test]
    fn test_archive_ext() {
        let ext = archive_ext();
        assert!(ext == "zip" || ext == "tar.gz");
    }
}