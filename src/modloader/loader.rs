use crate::version::manifest::Library;
use serde::Deserialize;
use std::path::PathBuf;

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

        promos.promos.get(&key)
            .or_else(|| promos.promos.get(&key2))
            .cloned()
            .ok_or_else(|| LauncherError::VersionNotFound(
                format!("MC {} 没有可用的 Forge 版本", mc_version)
            ))
    }

    /// 构建 Forge 安装目录路径
    pub fn install_dir(dot_minecraft: &PathBuf, mc_version: &str, forge_version: &str) -> PathBuf {
        dot_minecraft
            .join("versions")
            .join(format!("{}-forge-{}", mc_version, forge_version))
    }
}

/// Fabric 安装器
pub mod fabric {
    use super::*;
    use crate::error::{LauncherError, Result};

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
            return Err(LauncherError::VersionNotFound(
                format!("MC {} 没有可用的 Fabric 版本", mc_version)
            ));
        }

        let loaders: Vec<FabricLoaderVersion> = response.json().await?;
        loaders.iter()
            .find(|l| l.stable)
            .or_else(|| loaders.first())
            .map(|l| l.version.clone())
            .ok_or_else(|| LauncherError::VersionNotFound("没有可用的 Fabric Loader".into()))
    }

    /// 获取 Fabric 安装所需的 JSON
    pub async fn get_profile_json(mc_version: &str, loader_version: &str) -> Result<serde_json::Value> {
        let url = format!(
            "https://meta.fabricmc.net/v2/versions/loader/{}/{}/profile/json",
            mc_version, loader_version
        );
        let response = reqwest::get(&url).await?;
        let json: serde_json::Value = response.json().await?;
        Ok(json)
    }

    /// 构建 Fabric 安装目录路径
    pub fn install_dir(dot_minecraft: &PathBuf, mc_version: &str, loader_version: &str) -> PathBuf {
        dot_minecraft
            .join("versions")
            .join(format!("fabric-loader-{}-{}", loader_version, mc_version))
    }
}

/// Quilt 安装器
pub mod quilt {
    use super::*;
    use crate::error::{LauncherError, Result};

    /// 获取最新 Quilt Loader
    pub async fn get_latest_loader(mc_version: &str) -> Result<String> {
        let url = format!(
            "https://meta.quiltmc.org/v3/versions/loader/{}",
            mc_version
        );
        let response = reqwest::get(&url).await?;

        if !response.status().is_success() {
            return Err(LauncherError::VersionNotFound(
                format!("MC {} 没有可用的 Quilt 版本", mc_version)
            ));
        }

        #[derive(Debug, Deserialize)]
        struct LoaderEntry {
            version: String,
        }

        let loaders: Vec<LoaderEntry> = response.json().await?;
        loaders.first()
            .map(|l| l.version.clone())
            .ok_or_else(|| LauncherError::VersionNotFound("没有可用的 Quilt Loader".into()))
    }

    /// 构建 Quilt 安装目录路径
    pub fn install_dir(dot_minecraft: &PathBuf, mc_version: &str, loader_version: &str) -> PathBuf {
        dot_minecraft
            .join("versions")
            .join(format!("quilt-loader-{}-{}", loader_version, mc_version))
    }
}
