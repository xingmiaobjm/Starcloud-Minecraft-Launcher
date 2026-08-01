use std::path::Path;
use crate::error::Result;

/// 下载引擎 - 调度版本、资源、库的下载
pub struct DownloadEngine {
    /// Minecraft 根目录 (.minecraft)
    dot_minecraft: std::path::PathBuf,
}

impl DownloadEngine {
    /// 创建下载引擎
    pub fn new(dot_minecraft: impl Into<std::path::PathBuf>) -> Self {
        Self {
            dot_minecraft: dot_minecraft.into(),
        }
    }

    /// 获取 .minecraft 目录
    pub fn dot_minecraft(&self) -> &Path {
        &self.dot_minecraft
    }

    /// 获取版本目录
    pub fn versions_dir(&self) -> std::path::PathBuf {
        self.dot_minecraft.join("versions")
    }

    /// 获取资源目录
    pub fn assets_dir(&self) -> std::path::PathBuf {
        self.dot_minecraft.join("assets")
    }

    /// 获取库目录
    pub fn libraries_dir(&self) -> std::path::PathBuf {
        self.dot_minecraft.join("libraries")
    }

    /// 获取 Native 解压目录
    pub fn natives_dir(&self, version_id: &str) -> std::path::PathBuf {
        self.dot_minecraft
            .join("versions")
            .join(version_id)
            .join("natives")
    }

    /// 创建资源下载器
    pub fn asset_downloader(&self) -> super::assets::AssetDownloader {
        super::assets::AssetDownloader::new(self.assets_dir())
    }

    /// 创建库下载器
    pub fn library_downloader(&self) -> super::library::LibraryDownloader {
        super::library::LibraryDownloader::new(self.libraries_dir())
    }
}

/// 解压 native 库文件
pub fn extract_native(native_path: &Path, extract_dir: &Path) -> Result<()> {
    if !native_path.exists() {
        return Ok(());
    }

    let file = std::fs::File::open(native_path)?;
    let mut archive = zip::ZipArchive::new(file)?;

    for i in 0..archive.len() {
        let mut entry = archive.by_index(i)?;
        let name = entry.name().to_string();

        // 跳过 META-INF
        if name.starts_with("META-INF/") {
            continue;
        }

        let out_path = extract_dir.join(&name);
        if entry.is_dir() {
            std::fs::create_dir_all(&out_path)?;
        } else {
            if let Some(parent) = out_path.parent() {
                std::fs::create_dir_all(parent)?;
            }
            let mut out_file = std::fs::File::create(&out_path)?;
            std::io::copy(&mut entry, &mut out_file)?;
        }
    }

    Ok(())
}
