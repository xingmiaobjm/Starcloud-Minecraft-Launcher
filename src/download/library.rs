use crate::error::{LauncherError, Result};
use crate::version::manifest::{Library, LibraryArtifact, Rule, RuleAction};
use sha1::Digest;
use std::path::{Path, PathBuf};
use tokio::fs;
use tokio::io::AsyncWriteExt;

/// 库文件下载器
pub struct LibraryDownloader {
    /// 库文件根目录 (.minecraft/libraries)
    base_dir: PathBuf,
    /// 并发数
    concurrency: usize,
    /// 当前操作系统名称
    os_name: String,
    /// 当前操作系统架构
    os_arch: String,
}

impl LibraryDownloader {
    /// 创建库文件下载器
    pub fn new(base_dir: impl Into<PathBuf>) -> Self {
        Self {
            base_dir: base_dir.into(),
            concurrency: 6,
            os_name: detect_os_name(),
            os_arch: std::env::consts::ARCH.to_string(),
        }
    }

    /// 设置并发数
    pub fn concurrency(mut self, n: usize) -> Self {
        self.concurrency = n;
        self
    }

    /// 设置操作系统名称（用于 native 库筛选）
    pub fn os_name(mut self, name: impl Into<String>) -> Self {
        self.os_name = name.into();
        self
    }

    /// 下载所有需要的库文件
    pub async fn download_libraries(&self, libraries: &[Library]) -> Result<Vec<String>> {
        fs::create_dir_all(&self.base_dir).await?;

        let client = reqwest::Client::new();
        let mut classpath_entries: Vec<String> = Vec::new();
        let mut tasks = Vec::new();
        let mut batches: Vec<Vec<(LibraryArtifact, PathBuf)>> = Vec::new();
        let mut current_batch = Vec::new();

        for lib in libraries {
            // 检查规则是否允许加载此库
            if !check_rules(lib.rules.as_ref(), &self.os_name, &self.os_arch) {
                continue;
            }

            // 获取主 artifact
            if let Some(downloads) = &lib.downloads {
                if let Some(artifact) = &downloads.artifact {
                    let lib_path = self.base_dir.join(&artifact.path);
                    classpath_entries.push(lib_path.to_string_lossy().to_string());

                    if !lib_path.exists() {
                        current_batch.push((artifact.clone(), lib_path));
                        if current_batch.len() >= self.concurrency {
                            batches.push(std::mem::take(&mut current_batch));
                        }
                    }
                }

                // 获取 native 库
                if let (Some(natives), Some(classifiers)) = (&lib.natives, &downloads.classifiers) {
                    if let Some(native_suffix) = natives.get(&self.os_name) {
                        // native_suffix 格式如 "natives-linux"
                        let classifier_key = native_suffix.replace("${arch}", &self.replace_arch());
                        if let Some(native_artifact) = classifiers.get(&classifier_key) {
                            let native_path = self.base_dir.join(&native_artifact.path);
                            if !native_path.exists() {
                                current_batch.push((native_artifact.clone(), native_path.clone()));
                                if current_batch.len() >= self.concurrency {
                                    batches.push(std::mem::take(&mut current_batch));
                                }
                            }

                            // 解压 native 库
                            if lib.extract.is_some() {
                                let extract_dir = native_path
                                    .parent()
                                    .map(|p| p.to_path_buf())
                                    .unwrap_or_else(|| self.base_dir.clone());
                                crate::download::engine::extract_native(
                                    &native_path,
                                    &extract_dir,
                                )?;
                            }
                        }
                    }
                }
            }
        }

        if !current_batch.is_empty() {
            batches.push(current_batch);
        }

        // 分批并发下载
        for batch in batches {
            for (artifact, path) in batch {
                let client = client.clone();
                tasks.push(tokio::spawn(async move {
                    download_single_library(&client, &artifact, &path).await
                }));
            }
            for task in tasks.drain(..) {
                task.await??;
            }
        }

        Ok(classpath_entries)
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

/// 下载单个库文件
async fn download_single_library(
    client: &reqwest::Client,
    artifact: &LibraryArtifact,
    dest_path: &Path,
) -> Result<()> {
    if let Some(parent) = dest_path.parent() {
        fs::create_dir_all(parent).await?;
    }

    log::debug!("下载库: {}", artifact.path);

    let response = client.get(&artifact.url).send().await?;
    if !response.status().is_success() {
        return Err(LauncherError::DownloadError(format!(
            "下载库失败: {} (HTTP {})",
            artifact.path,
            response.status()
        )));
    }

    let bytes = response.bytes().await?;

    // SHA1 校验
    let hash = format!("{:x}", sha1::Sha1::digest(&bytes));
    if hash != artifact.sha1 {
        return Err(LauncherError::ValidationError {
            expected: artifact.sha1.clone(),
            actual: hash,
        });
    }

    // 原子写入
    let mut temp_path = dest_path.to_path_buf();
    temp_path.set_extension("tmp");
    let mut file = fs::File::create(&temp_path).await?;
    file.write_all(&bytes).await?;
    file.flush().await?;

    fs::rename(&temp_path, dest_path).await?;
    Ok(())
}
