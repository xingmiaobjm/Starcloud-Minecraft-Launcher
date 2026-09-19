use crate::error::{LauncherError, Result};
use crate::version::manifest::{ArgumentValue, ArgumentValueInner, ExtractConfig, VersionDetails};
use std::path::Path;

/// 下载引擎 - 调度版本、资源、库的下载
#[derive(Clone)]
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

    /// 版本对应的 natives 解压目录：
    /// 新式布局（1.21.9+/26.x，jvm 参数引用 `${natives_directory}/java`）→ `{natives}/java`；
    /// 旧式布局（≤1.21.8，`-Djava.library.path=${natives_directory}`）→ `{natives}`。
    pub fn version_natives_dir(&self, details: &VersionDetails) -> std::path::PathBuf {
        let base = self.natives_dir(&details.id);
        if self.uses_natives_subdir(details) {
            base.join("java")
        } else {
            base
        }
    }

    /// 新式 natives 子目录布局检测：jvm 参数里出现 `${natives_directory}/`。
    fn uses_natives_subdir(&self, details: &VersionDetails) -> bool {
        details
            .arguments
            .as_ref()
            .map(|a| {
                a.jvm.iter().any(|arg| {
                    let s = match arg {
                        ArgumentValue::Simple(s) => s.clone(),
                        ArgumentValue::Conditional(c) => match &c.value {
                            ArgumentValueInner::Single(s) => s.clone(),
                            ArgumentValueInner::Multiple(v) => v.join(" "),
                        },
                    };
                    s.contains("${natives_directory}/")
                })
            })
            .unwrap_or(false)
    }

    /// 获取客户端 jar 路径（versions/{id}/{id}.jar）
    pub fn client_jar_path(&self, version_id: &str) -> std::path::PathBuf {
        self.versions_dir()
            .join(version_id)
            .join(format!("{}.jar", version_id))
    }

    /// 获取日志配置文件路径（versions/{id}/{id}-log4j2.xml）
    pub fn logging_config_path(&self, version_id: &str) -> std::path::PathBuf {
        self.versions_dir()
            .join(version_id)
            .join(super::logging_config_file_name(version_id))
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

/// 校验 zip 条目路径是否安全（拒绝绝对路径与 `..` 穿越）
///
/// 返回 `Ok(relative_path)` 表示安全；`Err` 表示存在路径穿越，应跳过该条目。
pub fn sanitize_zip_entry(entry_name: &str) -> Result<&str> {
    if entry_name.is_empty() {
        return Err(LauncherError::Other(format!(
            "压缩包包含空路径条目: {:?}",
            entry_name
        )));
    }
    // 拒绝绝对路径与盘符
    if Path::new(entry_name).is_absolute()
        || entry_name.contains(':')
        || entry_name.starts_with('/')
        || entry_name.starts_with('\\')
    {
        return Err(LauncherError::Other(format!(
            "压缩包条目包含绝对路径: {:?}",
            entry_name
        )));
    }
    // 逐个组件校验，拒绝 .. 与空组件（连续斜杠）
    for component in Path::new(entry_name).components() {
        use std::path::Component;
        match component {
            Component::ParentDir => {
                return Err(LauncherError::Other(format!(
                    "压缩包条目包含路径穿越: {:?}",
                    entry_name
                )));
            }
            Component::CurDir => {
                return Err(LauncherError::Other(format!(
                    "压缩包条目包含相对路径: {:?}",
                    entry_name
                )));
            }
            Component::RootDir | Component::Prefix(_) => {
                return Err(LauncherError::Other(format!(
                    "压缩包条目包含根路径: {:?}",
                    entry_name
                )));
            }
            Component::Normal(_) => {}
        }
    }
    Ok(entry_name)
}

/// 解压 native 库文件（带路径穿越防护与 exclude 过滤）
pub fn extract_native(native_path: &Path, extract_dir: &Path) -> Result<()> {
    extract_native_with_exclude(native_path, extract_dir, None)
}

/// 解压 native 库文件，支持排除列表
pub fn extract_native_with_exclude(
    native_path: &Path,
    extract_dir: &Path,
    exclude: Option<&ExtractConfig>,
) -> Result<()> {
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

        // 路径穿越防护
        let safe_name = match sanitize_zip_entry(&name) {
            Ok(n) => n.to_string(),
            Err(e) => {
                log::warn!("跳过不安全的压缩包条目: {} ({})", name, e);
                continue;
            }
        };

        // exclude 过滤（匹配前缀）
        if let Some(config) = exclude {
            if config.exclude.iter().any(|ex| safe_name.starts_with(ex)) {
                log::debug!("排除解压条目: {}", safe_name);
                continue;
            }
        }

        let out_path = extract_dir.join(&safe_name);
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
