use std::path::PathBuf;
use crate::error::{LauncherError, Result};

/// Java 运行时信息
#[derive(Debug, Clone)]
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
    pub fn detect(&self) -> Result<JavaRuntime> {
        let mut candidates = Vec::new();

        // 1. 检查 JAVA_HOME 环境变量
        if let Ok(java_home) = std::env::var("JAVA_HOME") {
            let home = PathBuf::from(&java_home);
            let exe = get_java_exe(&home);
            if exe.exists() {
                if let Ok(runtime) = parse_java_version(&exe) {
                    candidates.push(runtime);
                }
            }
        }

        // 2. 检查 PATH 中的 java
        let path_java = get_java_exe_from_path();
        if path_java.exists() {
            if let Ok(runtime) = parse_java_version(&path_java) {
                candidates.push(runtime);
            }
        }

        // 3. 检查额外搜索路径
        for extra in &self.extra_paths {
            let exe = get_java_exe(extra);
            if exe.exists() {
                if let Ok(runtime) = parse_java_version(&exe) {
                    candidates.push(runtime);
                }
            }
        }

        // 4. 检查常见安装位置 (Windows)
        #[cfg(target_os = "windows")]
        {
            for drive in ["C:", "D:", "E:"] {
                let base = PathBuf::from(format!("{}\\Program Files\\Java", drive));
                if base.exists() {
                    if let Ok(entries) = std::fs::read_dir(&base) {
                        for entry in entries.flatten() {
                            let exe = get_java_exe(&entry.path());
                            if exe.exists() {
                                if let Ok(runtime) = parse_java_version(&exe) {
                                    candidates.push(runtime);
                                }
                            }
                        }
                    }
                }
                // Java 在 Program Files 下也可能
                let alt_base = PathBuf::from(format!("{}\\Program Files", drive));
                if alt_base.exists() {
                    if let Ok(entries) = std::fs::read_dir(&alt_base) {
                        for entry in entries.flatten() {
                            let name = entry.file_name();
                            let name_str = name.to_string_lossy();
                            if name_str.to_lowercase().contains("java") || name_str.to_lowercase().contains("jdk") || name_str.to_lowercase().contains("jre") {
                                let exe = get_java_exe(&entry.path());
                                if exe.exists() {
                                    if let Ok(runtime) = parse_java_version(&exe) {
                                        candidates.push(runtime);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // 5. 检查常见安装位置 (Linux / macOS)
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
                if base.exists() {
                    if let Ok(entries) = std::fs::read_dir(&base) {
                        for entry in entries.flatten() {
                            let exe = get_java_exe(&entry.path());
                            if exe.exists() {
                                if let Ok(runtime) = parse_java_version(&exe) {
                                    candidates.push(runtime);
                                }
                            }
                        }
                    }
                }
            }
        }

        // 按版本降序排列，选最合适的
        candidates.sort_by(|a, b| b.major_version.cmp(&a.major_version));

        let best = candidates
            .into_iter()
            .find(|r| r.major_version >= self.min_version)
            .ok_or_else(|| LauncherError::JavaNotFound(self.min_version))?;

        log::info!("检测到 Java: {} (版本 {})", best.version_string, best.major_version);
        Ok(best)
    }

    /// 检测指定路径的 Java
    pub fn detect_at(path: &PathBuf) -> Result<JavaRuntime> {
        let exe = get_java_exe(path);
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

/// 获取操作系统对应的 java 可执行文件名
fn get_java_exe(dir: &PathBuf) -> PathBuf {
    let name = if cfg!(target_os = "windows") {
        "javaw.exe"
    } else {
        "java"
    };
    dir.join("bin").join(name)
}

/// 从 PATH 环境变量中查找 java
fn get_java_exe_from_path() -> PathBuf {
    let name = if cfg!(target_os = "windows") {
        "javaw.exe"
    } else {
        "java"
    };
    PathBuf::from(name)
}

/// 解析 Java 版本信息
fn parse_java_version(java_exe: &PathBuf) -> Result<JavaRuntime> {
    let output = std::process::Command::new(java_exe)
        .arg("-XshowSettings:properties")
        .arg("-version")
        .stderr(std::process::Stdio::piped())
        .output()
        .map_err(|e| LauncherError::LaunchError(format!("无法执行 Java: {}", e)))?;

    let stderr = String::from_utf8_lossy(&output.stderr);

    // 解析版本号: 从 "java version \"1.8.0_301\"" 或 "openjdk version \"17.0.1\""
    let major_version = if let Some(line) = stderr.lines().find(|l| l.contains("version")) {
        parse_major_version(line)
    } else if let Some(line) = stderr.lines().find(|l| l.contains("java.version")) {
        // -XshowSettings 输出格式: "    java.version = 17.0.1"
        let parts: Vec<&str> = line.split('=').collect();
        if parts.len() >= 2 {
            parse_major_version(parts[1])
        } else {
            return Err(LauncherError::LaunchError("无法解析 Java 版本".to_string()));
        }
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

    Ok(JavaRuntime {
        executable: java_exe.clone(),
        major_version,
        version_string: stderr.lines().next().unwrap_or("unknown").to_string(),
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
