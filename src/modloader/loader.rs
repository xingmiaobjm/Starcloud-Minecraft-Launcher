use crate::version::manifest::Library;
use serde::Deserialize;
use std::path::{Path, PathBuf};

/// ModLoader 安装信息
#[derive(Debug, Clone)]
pub struct ModLoaderInfo {
    /// 加载器类型
    pub loader_type: ModLoaderType,
    /// 加载器版本
    pub version: String,
    /// 对应的 Minecraft 版本
    pub mc_version: String,
    /// 下载 URL
    pub download_url: Option<String>,
    /// 安装需要添加的库
    pub extra_libraries: Vec<Library>,
    /// 安装需要添加的 JVM 参数
    pub extra_jvm_args: Vec<String>,
    /// 主类名（覆盖默认）
    pub main_class_override: Option<String>,
}

/// ModLoader 类型
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ModLoaderType {
    Forge,
    Fabric,
    Quilt,
    NeoForge,
    Custom(String),
}

impl std::fmt::Display for ModLoaderType {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ModLoaderType::Forge => write!(f, "forge"),
            ModLoaderType::Fabric => write!(f, "fabric"),
            ModLoaderType::Quilt => write!(f, "quilt"),
            ModLoaderType::NeoForge => write!(f, "neoforge"),
            ModLoaderType::Custom(s) => write!(f, "{}", s),
        }
    }
}

/// Forge 安装器
pub mod forge {
    use super::*;
    use crate::error::{LauncherError, Result};

    #[derive(Debug, Deserialize)]
    struct ForgePromotion {
        promos: std::collections::HashMap<String, String>,
    }

    /// 获取指定 MC 版本的推荐 Forge 版本
    pub async fn get_recommended_version(mc_version: &str) -> Result<String> {
        let url = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        let response = reqwest::get(url).await?;
        let promos: ForgePromotion = response.json().await?;

        let key = format!("{}-recommended", mc_version);
        let key2 = format!("{}-latest", mc_version);

        promos
            .promos
            .get(&key)
            .or_else(|| promos.promos.get(&key2))
            .cloned()
            .ok_or_else(|| {
                LauncherError::VersionNotFound(format!("MC {} 没有可用的 Forge 版本", mc_version))
            })
    }

    /// 构建 Forge 安装目录路径
    pub fn install_dir(dot_minecraft: &Path, mc_version: &str, forge_version: &str) -> PathBuf {
        dot_minecraft
            .join("versions")
            .join(format!("{}-forge-{}", mc_version, forge_version))
    }
}

/// Fabric 安装器
pub mod fabric {
    use super::*;
    use crate::error::{LauncherError, Result};

    /// meta 接口返回的顶层条目：loader 内才是加载器信息
    #[derive(Debug, Deserialize)]
    struct FabricEntry {
        loader: FabricLoaderVersion,
    }

    #[derive(Debug, Deserialize)]
    struct FabricLoaderVersion {
        version: String,
        stable: bool,
    }

    /// 获取最新稳定版 Fabric Loader
    pub async fn get_latest_loader(mc_version: &str) -> Result<String> {
        let url = format!(
            "https://meta.fabricmc.net/v2/versions/loader/{}",
            mc_version
        );
        let response = reqwest::get(&url).await?;

        if !response.status().is_success() {
            return Err(LauncherError::VersionNotFound(format!(
                "MC {} 没有可用的 Fabric 版本",
                mc_version
            )));
        }

        let entries: Vec<FabricEntry> = response.json().await?;
        entries
            .iter()
            .find(|e| e.loader.stable)
            .or_else(|| entries.first())
            .map(|e| e.loader.version.clone())
            .ok_or_else(|| LauncherError::VersionNotFound("没有可用的 Fabric Loader".into()))
    }

    /// 获取 Fabric 安装所需的 JSON
    pub async fn get_profile_json(
        mc_version: &str,
        loader_version: &str,
    ) -> Result<serde_json::Value> {
        let url = format!(
            "https://meta.fabricmc.net/v2/versions/loader/{}/{}/profile/json",
            mc_version, loader_version
        );
        let response = reqwest::get(&url).await?;
        let json: serde_json::Value = response.json().await?;
        Ok(json)
    }

    /// 构建 Fabric 安装目录路径
    pub fn install_dir(dot_minecraft: &Path, mc_version: &str, loader_version: &str) -> PathBuf {
        dot_minecraft
            .join("versions")
            .join(format!("fabric-loader-{}-{}", loader_version, mc_version))
    }
}

/// Quilt 安装器
pub mod quilt {
    use super::*;
    use crate::error::{LauncherError, Result};

    /// meta 接口返回的顶层条目：loader 内才是加载器信息
    #[derive(Debug, Deserialize)]
    struct QuiltEntry {
        loader: QuiltLoaderVersion,
    }

    #[derive(Debug, Deserialize)]
    struct QuiltLoaderVersion {
        version: String,
    }

    /// 判断是否为预发布版本（beta / alpha / rc 等）
    fn is_prerelease(version: &str) -> bool {
        let lower = version.to_lowercase();
        lower.contains("beta") || lower.contains("alpha") || lower.contains("rc") || lower.contains("-")
    }

    /// 按数字段比较版本号（用于选取最大稳定版）
    fn compare_version(a: &str, b: &str) -> std::cmp::Ordering {
        fn parts(s: &str) -> Vec<u64> {
            s.split(|c: char| !c.is_ascii_digit())
                .filter(|p| !p.is_empty())
                .map(|p| p.parse().unwrap_or(0))
                .collect()
        }
        let pa = parts(a);
        let pb = parts(b);
        for (x, y) in pa.iter().zip(pb.iter()) {
            match x.cmp(y) {
                std::cmp::Ordering::Equal => continue,
                other => return other,
            }
        }
        pa.len().cmp(&pb.len())
    }

    /// 获取最新 Quilt Loader（优先稳定版，否则取版本号最大者）
    pub async fn get_latest_loader(mc_version: &str) -> Result<String> {
        let url = format!("https://meta.quiltmc.org/v3/versions/loader/{}", mc_version);
        let response = reqwest::get(&url).await?;

        if !response.status().is_success() {
            return Err(LauncherError::VersionNotFound(format!(
                "MC {} 没有可用的 Quilt 版本",
                mc_version
            )));
        }

        let entries: Vec<QuiltEntry> = response.json().await?;
        entries
            .iter()
            .filter(|e| !is_prerelease(&e.loader.version))
            .max_by(|a, b| compare_version(&a.loader.version, &b.loader.version))
            .or_else(|| entries.iter().max_by(|a, b| compare_version(&a.loader.version, &b.loader.version)))
            .map(|e| e.loader.version.clone())
            .ok_or_else(|| LauncherError::VersionNotFound("没有可用的 Quilt Loader".into()))
    }

    /// 获取 Quilt 安装所需的 profile JSON
    pub async fn get_profile_json(
        mc_version: &str,
        loader_version: &str,
    ) -> Result<serde_json::Value> {
        let url = format!(
            "https://meta.quiltmc.org/v3/versions/loader/{}/{}/profile/json",
            mc_version, loader_version
        );
        let response = reqwest::get(&url).await?;
        let json: serde_json::Value = response.json().await?;
        Ok(json)
    }

    /// 构建 Quilt 安装目录路径
    pub fn install_dir(dot_minecraft: &Path, mc_version: &str, loader_version: &str) -> PathBuf {
        dot_minecraft
            .join("versions")
            .join(format!("quilt-loader-{}-{}", loader_version, mc_version))
    }
}

/// NeoForge 安装器
pub mod neoforge {
    use super::*;
    use crate::error::{LauncherError, Result};

    /// 获取指定 MC 版本可用的最新 NeoForge 构建号
    ///
    /// 从 maven-metadata.xml 中筛选形如 `{mc}-{build}` 的版本并取最大构建号。
    pub async fn get_latest_version(mc_version: &str) -> Result<String> {
        let url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
        let response = reqwest::get(url).await?;
        if !response.status().is_success() {
            return Err(LauncherError::VersionNotFound(format!(
                "MC {} 没有可用的 NeoForge 版本",
                mc_version
            )));
        }
        let text = response.text().await?;
        let versions = parse_metadata_versions(&text);
        let prefix = format!("{}-", mc_version);
        let candidate = versions
            .iter()
            .filter(|v| v.starts_with(&prefix))
            .max_by(|a, b| compare_version(a, b));
        candidate.cloned().ok_or_else(|| {
            LauncherError::VersionNotFound(format!("MC {} 没有可用的 NeoForge 版本", mc_version))
        })
    }

    /// 从 maven-metadata.xml 文本中解析 <version> 列表
    fn parse_metadata_versions(xml: &str) -> Vec<String> {
        let mut out = Vec::new();
        for line in xml.lines() {
            let line = line.trim();
            if line.starts_with("<version>") && line.ends_with("</version>") {
                let inner = &line["<version>".len()..line.len() - "</version>".len()];
                if !inner.is_empty() {
                    out.push(inner.to_string());
                }
            }
        }
        out
    }

    /// 按版本号数字段比较（忽略 `{mc}-` 前缀，比较构建号部分）
    fn compare_version(a: &str, b: &str) -> std::cmp::Ordering {
        fn parts(s: &str) -> Vec<u64> {
            s.split(|c: char| !c.is_ascii_digit())
                .filter(|p| !p.is_empty())
                .map(|p| p.parse().unwrap_or(0))
                .collect()
        }
        let pa = parts(a);
        let pb = parts(b);
        for (x, y) in pa.iter().zip(pb.iter()) {
            match x.cmp(y) {
                std::cmp::Ordering::Equal => continue,
                other => return other,
            }
        }
        pa.len().cmp(&pb.len())
    }

    /// 构建 NeoForge 安装目录路径
    pub fn install_dir(
        dot_minecraft: &Path,
        mc_version: &str,
        loader_version: &str,
    ) -> PathBuf {
        dot_minecraft
            .join("versions")
            .join(format!("{}-neoforge-{}", mc_version, loader_version))
    }
}
