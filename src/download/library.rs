use crate::download::engine::extract_native_with_exclude;
use crate::download::http_client::HttpClient;
use crate::download::progress::ProgressTracker;
use crate::error::{LauncherError, Result};
use crate::version::manifest::{Library, LibraryArtifact, Rule, RuleAction};
use sha1::Digest;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use tokio::fs;
use tokio::io::AsyncWriteExt;

/// 库文件下载器
pub struct LibraryDownloader {
    /// 库文件根目录 (.minecraft/libraries)
    base_dir: PathBuf,
    /// Native 解压目录（可选，默认解压到库目录的父目录）
    natives_dir: Option<PathBuf>,
    /// 并发数
    concurrency: usize,
    /// 当前操作系统名称
    os_name: String,
    /// 当前操作系统架构
    os_arch: String,
    /// 进度追踪器（可选）
    tracker: Option<Arc<ProgressTracker>>,
}

impl LibraryDownloader {
    /// 创建库文件下载器
    pub fn new(base_dir: impl Into<PathBuf>) -> Self {
        Self {
            base_dir: base_dir.into(),
            natives_dir: None,
            concurrency: 6,
            os_name: detect_os_name(),
            os_arch: std::env::consts::ARCH.to_string(),
            tracker: None,
        }
    }

    /// 设置并发数
    pub fn concurrency(mut self, n: usize) -> Self {
        self.concurrency = n.max(1);
        self
    }

    /// 设置 Native 解压目录（解压前会清空该目录）
    pub fn natives_dir(mut self, dir: impl Into<PathBuf>) -> Self {
        self.natives_dir = Some(dir.into());
        self
    }

    /// 设置操作系统名称（用于 native 库筛选）
    pub fn os_name(mut self, name: impl Into<String>) -> Self {
        self.os_name = name.into();
        self
    }

    /// 接入进度追踪器
    pub fn with_tracker(mut self, tracker: Arc<ProgressTracker>) -> Self {
        self.tracker = Some(tracker);
        self
    }

    /// 下载所有需要的库文件，返回 classpath 条目（已存在则校验 SHA1）
    pub async fn download_libraries(&self, libraries: &[Library]) -> Result<Vec<String>> {
        fs::create_dir_all(&self.base_dir).await?;

        let http =
            HttpClient::new()?.with_retry(crate::download::http_client::RetryConfig::default());
        let mut classpath_entries: Vec<String> = Vec::new();
        let mut download_queue: Vec<(LibraryArtifact, PathBuf)> = Vec::new();
        let mut native_extract: Vec<(PathBuf, Option<crate::version::manifest::ExtractConfig>)> =
            Vec::new();

        // 需要下载的总字节数（用于进度）
        let mut total_bytes: u64 = 0;

        for lib in libraries {
            // 检查规则是否允许加载此库
            if !check_rules(lib.rules.as_ref(), &self.os_name, &self.os_arch) {
                continue;
            }

            // 解析下载条目（新版 downloads.artifact / 旧版 url 回退）
            let artifact = resolve_artifact(lib);

            // 主 artifact
            if let Some(artifact) = &artifact {
                let lib_path = self.base_dir.join(&artifact.path);
                classpath_entries.push(lib_path.to_string_lossy().to_string());

                if self.needs_download(&lib_path, artifact) {
                    total_bytes += artifact.size;
                    download_queue.push((artifact.clone(), lib_path));
                }
            }

            // Native 库：筛选 → 下载 → 解压到 natives_dir
            if let (Some(natives), Some(downloads)) = (&lib.natives, &lib.downloads) {
                if let Some(native_suffix) = natives.get(&self.os_name) {
                    let classifier_key = native_suffix.replace("${arch}", &self.replace_arch());
                    if let Some(native_artifact) = downloads
                        .classifiers
                        .as_ref()
                        .and_then(|c| c.get(&classifier_key))
                    {
                        let native_path = self.base_dir.join(&native_artifact.path);
                        if self.needs_download(&native_path, native_artifact) {
                            total_bytes += native_artifact.size;
                            download_queue.push((native_artifact.clone(), native_path.clone()));
                        }
                        native_extract.push((native_path, lib.extract.clone()));
                    }
                }
            }
        }

        // 已存在且校验失败的文件：删除后重新下载（原子写入保证一致性）
        if let Some(tracker) = &self.tracker {
            tracker.begin_stage("下载库文件", total_bytes);
        }

        // 分批并发下载（统一走 HttpClient：超时 + 重试 + 连接池）
        for batch in download_queue.chunks(self.concurrency) {
            let mut tasks = Vec::new();
            for (artifact, path) in batch {
                let http = http.clone();
                let path = path.clone();
                let artifact = artifact.clone();
                tasks.push(tokio::spawn(async move {
                    download_single_library(&http, &artifact, &path).await
                }));
            }
            for task in tasks {
                let size = task.await??;
                if let Some(tracker) = &self.tracker {
                    tracker.add_progress(size);
                }
            }
        }

        // 解压 native 库（清空旧目录，避免残留旧版本文件）
        if !native_extract.is_empty() {
            if let Some(tracker) = &self.tracker {
                tracker.set_status(crate::download::progress::ProgressStatus::Extracting);
            }
            let extract_dir = match &self.natives_dir {
                Some(dir) => {
                    let dir = dir.clone();
                    clear_dir(&dir).await?;
                    dir
                }
                None => {
                    // 未指定 natives_dir 时回退到库目录的父目录（兼容旧行为）
                    native_extract[0]
                        .0
                        .parent()
                        .map(|p| p.to_path_buf())
                        .unwrap_or_else(|| self.base_dir.clone())
                }
            };
            fs::create_dir_all(&extract_dir).await?;

            for (native_path, extract_config) in native_extract {
                log::info!("解压 native: {}", native_path.display());
                extract_native_with_exclude(&native_path, &extract_dir, extract_config.as_ref())?;
            }
            if let Some(tracker) = &self.tracker {
                tracker.set_status(crate::download::progress::ProgressStatus::Downloading);
            }
        }

        if let Some(tracker) = &self.tracker {
            tracker.set_status(crate::download::progress::ProgressStatus::Completed);
        }

        Ok(classpath_entries)
    }

    /// 判断文件是否需要下载：不存在，或存在但 SHA1 不匹配（有 sha1 时）
    fn needs_download(&self, path: &Path, artifact: &LibraryArtifact) -> bool {
        if !path.exists() {
            return true;
        }
        if artifact.sha1.is_empty() {
            return false;
        }
        match sha1_of_file(path) {
            Ok(hash) => hash != artifact.sha1,
            Err(_) => true,
        }
    }

    fn replace_arch(&self) -> String {
        match std::env::consts::ARCH {
            "x86" => "32".to_string(),
            "x86_64" => "64".to_string(),
            "aarch64" => "arm64".to_string(),
            other => other.to_string(),
        }
    }
}

/// 解析库文件的下载条目：
/// 1. 新版格式：`downloads.artifact`
/// 2. 旧版格式回退：仅 `url` 时按 `{url}/{name 转路径}` 构造
fn resolve_artifact(lib: &Library) -> Option<LibraryArtifact> {
    if let Some(downloads) = &lib.downloads {
        if let Some(artifact) = &downloads.artifact {
            return Some(artifact.clone());
        }
    }
    // 旧版库：url + name
    let url = lib.url.as_deref()?;
    let path = maven_path_from_name(&lib.name)?;
    let download_url = format!("{}/{}", url.trim_end_matches('/'), path);
    Some(LibraryArtifact {
        path,
        sha1: String::new(),
        size: 0,
        url: download_url,
    })
}

/// 将 Maven 坐标 `group:artifact:version[:classifier]` 转换为文件相对路径
fn maven_path_from_name(name: &str) -> Option<String> {
    let parts: Vec<&str> = name.split(':').collect();
    if parts.len() < 3 {
        return None;
    }
    let group = parts[0];
    let artifact = parts[1];
    let version = parts[2];
    let classifier = parts.get(3).copied().unwrap_or("");

    let file_name = if classifier.is_empty() {
        format!("{}-{}.jar", artifact, version)
    } else {
        format!("{}-{}-{}.jar", artifact, version, classifier)
    };

    // Maven 标准布局：group/artifact/version/file（如 org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar）
    Some(format!(
        "{}/{}/{}/{}",
        group.replace('.', "/"),
        artifact,
        version,
        file_name
    ))
}

/// 检查规则是否允许
fn check_rules(rules: Option<&Vec<Rule>>, os_name: &str, os_arch: &str) -> bool {
    let rules = match rules {
        Some(r) => r,
        None => return true, // 无规则则默认允许
    };

    let mut allowed = false;
    for rule in rules {
        let matches = match &rule.os {
            Some(os_rule) => {
                os_rule.name == os_name && os_rule.arch.as_ref().is_none_or(|a| a == os_arch)
            }
            None => true,
        };

        if matches {
            allowed = match rule.action {
                RuleAction::Allow => true,
                RuleAction::Disallow => false,
            };
        }
    }

    allowed
}

/// 检测当前操作系统名称（Mojang 格式）
fn detect_os_name() -> String {
    if cfg!(target_os = "windows") {
        "windows".to_string()
    } else if cfg!(target_os = "macos") {
        "osx".to_string()
    } else if cfg!(target_os = "linux") {
        "linux".to_string()
    } else {
        std::env::consts::OS.to_string()
    }
}

/// 下载单个库文件（带重试），返回写入字节数
async fn download_single_library(
    http: &HttpClient,
    artifact: &LibraryArtifact,
    dest_path: &Path,
) -> Result<u64> {
    if let Some(parent) = dest_path.parent() {
        fs::create_dir_all(parent).await?;
    }

    log::debug!("下载库: {} ({} bytes)", artifact.path, artifact.size);

    let bytes = http.get_bytes_with_retry(&artifact.url).await?;

    // SHA1 校验（有期望值时）
    if !artifact.sha1.is_empty() {
        let hash = format!("{:x}", sha1::Sha1::digest(&bytes));
        if hash != artifact.sha1 {
            return Err(LauncherError::ValidationError {
                expected: artifact.sha1.clone(),
                actual: hash,
            });
        }
    }

    // 原子写入（临时文件 + rename）
    let temp_path = temp_path_for(dest_path);
    let mut file = fs::File::create(&temp_path).await?;
    file.write_all(&bytes).await?;
    file.flush().await?;

    // Windows 上 rename 前需确保目标不存在
    if dest_path.exists() {
        fs::remove_file(dest_path).await?;
    }
    fs::rename(&temp_path, dest_path).await?;

    Ok(bytes.len() as u64)
}

/// 构造临时文件路径
fn temp_path_for(dest: &Path) -> PathBuf {
    let mut p = dest.to_path_buf();
    let name = p
        .file_name()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();
    p.set_file_name(format!("{}.tmp", name));
    p
}

/// 计算文件 SHA1（十六进制小写）
fn sha1_of_file(path: &Path) -> Result<String> {
    let bytes = std::fs::read(path)?;
    Ok(format!("{:x}", sha1::Sha1::digest(&bytes)))
}

/// 清空目录内容（保留目录本身）
async fn clear_dir(dir: &Path) -> Result<()> {
    if !dir.exists() {
        return Ok(());
    }
    let mut entries = fs::read_dir(dir).await?;
    while let Some(entry) = entries.next_entry().await? {
        let path = entry.path();
        if entry.file_type().await.map(|t| t.is_dir()).unwrap_or(false) {
            fs::remove_dir_all(&path).await?;
        } else {
            fs::remove_file(&path).await?;
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::version::manifest::LibraryDownloads;

    #[test]
    fn test_maven_path_from_name() {
        assert_eq!(
            maven_path_from_name("org.lwjgl:lwjgl:3.3.1"),
            Some("org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar".to_string())
        );
        assert_eq!(
            maven_path_from_name("org.lwjgl:lwjgl:3.3.1:natives-windows"),
            Some("org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar".to_string())
        );
        assert_eq!(maven_path_from_name("invalid"), None);
    }

    #[test]
    fn test_resolve_artifact_new_format() {
        let lib = Library {
            name: "com.example:test:1.0".to_string(),
            downloads: Some(LibraryDownloads {
                artifact: Some(LibraryArtifact {
                    path: "com/example/test/1.0/test-1.0.jar".to_string(),
                    sha1: "abc".to_string(),
                    size: 10,
                    url: "https://example.com/test.jar".to_string(),
                }),
                classifiers: None,
            }),
            url: Some("https://fallback.example.com".to_string()),
            rules: None,
            natives: None,
            extract: None,
        };
        let artifact = resolve_artifact(&lib).unwrap();
        assert_eq!(artifact.path, "com/example/test/1.0/test-1.0.jar");
        assert_eq!(artifact.url, "https://example.com/test.jar");
    }

    #[test]
    fn test_resolve_artifact_legacy_fallback() {
        let lib = Library {
            name: "com.example:test:1.0".to_string(),
            downloads: None,
            url: Some("https://fallback.example.com/libs".to_string()),
            rules: None,
            natives: None,
            extract: None,
        };
        let artifact = resolve_artifact(&lib).unwrap();
        assert_eq!(artifact.path, "com/example/test/1.0/test-1.0.jar");
        assert_eq!(
            artifact.url,
            "https://fallback.example.com/libs/com/example/test/1.0/test-1.0.jar"
        );
        // 旧版回退无 sha1
        assert!(artifact.sha1.is_empty());
    }
}
