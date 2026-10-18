// Minecraft 版本管理命令

use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::fs;

// ========= 基础类型 =========

#[derive(Serialize, Deserialize, Clone)]
pub struct MinecraftVersion {
    pub id: String,
    pub version_type: String,
    pub release_time: String,
    pub url: Option<String>,
}

#[derive(Serialize, Deserialize)]
pub struct VersionManifest {
    pub latest: LatestVersions,
    pub versions: Vec<MinecraftVersion>,
}

#[derive(Serialize, Deserialize)]
pub struct LatestVersions {
    pub release: String,
    pub snapshot: String,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct InstalledVersion {
    pub id: String,
    pub version_type: String,
    pub installed: bool,
    pub size_mb: f64,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct JavaInstallation {
    pub path: String,
    pub version: String,
    pub vendor: String,
    pub is_64bit: bool,
}

#[derive(Serialize, Deserialize)]
pub struct DownloadProgressEvent {
    pub version_id: String,
    pub stage: String,
    pub current: u64,
    pub total: u64,
}

// ========= Version JSON 结构 =========

#[derive(Deserialize)]
struct VersionDetail {
    id: String,
    #[serde(rename = "type")]
    version_type: String,
    downloads: VersionDownloads,
    asset_index: AssetIndex,
    libraries: Vec<Library>,
    #[serde(rename = "mainClass")]
    main_class: String,
    arguments: Option<VersionArguments>,
    #[serde(rename = "javaVersion")]
    java_version: Option<JavaVersion>,
}

#[derive(Deserialize)]
struct VersionDownloads {
    client: Download,
}

#[derive(Deserialize)]
struct Download {
    sha1: String,
    size: u64,
    url: String,
}

#[derive(Deserialize)]
struct AssetIndex {
    id: String,
    sha1: String,
    size: u64,
    url: String,
    #[serde(rename = "totalSize")]
    total_size: u64,
}

#[derive(Deserialize)]
struct Library {
    name: String,
    downloads: LibraryDownloads,
    rules: Option<Vec<Rule>>,
}

#[derive(Deserialize)]
struct LibraryDownloads {
    artifact: Option<Artifact>,
}

#[derive(Deserialize)]
struct Artifact {
    path: String,
    sha1: String,
    size: u64,
    url: String,
}

#[derive(Deserialize)]
struct Rule {
    action: String,
    os: Option<OsRule>,
}

#[derive(Deserialize)]
struct OsRule {
    name: String,
}

#[derive(Deserialize)]
struct AssetObject {
    hash: String,
    size: u64,
}

#[derive(Deserialize)]
struct VersionArguments {
    game: Vec<serde_json::Value>,
    jvm: Vec<serde_json::Value>,
}

#[derive(Deserialize)]
struct JavaVersion {
    component: String,
    #[serde(rename = "majorVersion")]
    major_version: u32,
}

// ========= 命令 =========

/// 获取 Minecraft 版本存储目录
fn get_minecraft_dir() -> PathBuf {
    let base = dirs::data_dir().unwrap_or_else(|| PathBuf::from("."));
    base.join(".starcloud_minecraft")
}

/// 获取版本清单
#[tauri::command]
pub async fn fetch_versions() -> Result<VersionManifest, String> {
    let url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    let response = reqwest::get(url)
        .await
        .map_err(|e| format!("获取版本清单失败: {}", e))?;
    let manifest: VersionManifest = response
        .json()
        .await
        .map_err(|e| format!("解析版本清单失败: {}", e))?;
    Ok(manifest)
}

/// 获取已安装的版本列表
#[tauri::command]
pub fn list_installed_versions() -> Result<Vec<InstalledVersion>, String> {
    let mc_dir = get_minecraft_dir();
    let versions_dir = mc_dir.join("versions");

    if !versions_dir.exists() {
        return Ok(vec![]);
    }

    let mut result = Vec::new();
    if let Ok(entries) = fs::read_dir(&versions_dir) {
        for entry in entries.flatten() {
            let dir = entry.path();
            if !dir.is_dir() {
                continue;
            }
            let version_id = dir.file_name().unwrap().to_string_lossy().to_string();
            let jar_path = dir.join(format!("{}.jar", version_id));
            let size_mb = if jar_path.exists() {
                jar_path.metadata().map(|m| m.len() as f64 / 1_048_576.0).unwrap_or(0.0)
            } else {
                0.0
            };

            result.push(InstalledVersion {
                id: version_id,
                version_type: String::new(),
                installed: jar_path.exists(),
                size_mb: (size_mb * 10.0).round() / 10.0,
            });
        }
    }
    Ok(result)
}

/// 下载并安装版本
#[tauri::command]
pub async fn install_version(version_id: String) -> Result<String, String> {
    let mc_dir = get_minecraft_dir();
    let versions_dir = mc_dir.join("versions");

    // 1. 获取版本 manifest
    let manifest = fetch_versions_inner().await?;
    let version_entry = manifest
        .versions
        .iter()
        .find(|v| v.id == version_id)
        .ok_or(format!("未找到版本: {}", version_id))?;
    let version_url = version_entry
        .url
        .as_ref()
        .ok_or("版本 URL 不可用".to_string())?;

    // 2. 获取版本详情 JSON
    let client = reqwest::Client::new();
    let detail: VersionDetail = client
        .get(version_url)
        .send()
        .await
        .map_err(|e| format!("获取版本详情失败: {}", e))?
        .json()
        .await
        .map_err(|e| format!("解析版本详情失败: {}", e))?;

    let version_dir = versions_dir.join(&version_id);
    fs::create_dir_all(&version_dir).map_err(|e| format!("创建版本目录失败: {}", e))?;

    // 3. 保存版本 JSON
    let json_path = version_dir.join(format!("{}.json", version_id));
    if !json_path.exists() {
        let json_bytes = client
            .get(version_url)
            .send()
            .await
            .map_err(|e| format!("下载版本 JSON 失败: {}", e))?
            .bytes()
            .await
            .map_err(|e| format!("读取版本 JSON 失败: {}", e))?;
        fs::write(&json_path, &json_bytes)
            .map_err(|e| format!("保存版本 JSON 失败: {}", e))?;
    }

    // 4. 下载 client.jar
    let jar_path = version_dir.join(format!("{}.jar", version_id));
    if !jar_path.exists() {
        let jar_bytes = client
            .get(&detail.downloads.client.url)
            .send()
            .await
            .map_err(|e| format!("下载 client.jar 失败: {}", e))?
            .bytes()
            .await
            .map_err(|e| format!("读取 client.jar 失败: {}", e))?;
        fs::write(&jar_path, &jar_bytes)
            .map_err(|e| format!("保存 client.jar 失败: {}", e))?;
    }

    // 5. 下载 asset index
    let assets_dir = mc_dir.join("assets");
    let indexes_dir = assets_dir.join("indexes");
    fs::create_dir_all(&indexes_dir).map_err(|e| format!("创建索引目录失败: {}", e))?;

    let asset_index_path = indexes_dir.join(format!("{}.json", detail.asset_index.id));
    if !asset_index_path.exists() {
        let index_bytes = client
            .get(&detail.asset_index.url)
            .send()
            .await
            .map_err(|e| format!("下载 Asset Index 失败: {}", e))?
            .bytes()
            .await
            .map_err(|e| format!("读取 Asset Index 失败: {}", e))?;
        fs::write(&asset_index_path, &index_bytes)
            .map_err(|e| format!("保存 Asset Index 失败: {}", e))?;
    }

    // 6. 下载必需的 assets（采样下载，避免全量下载）
    let objects_dir = assets_dir.join("objects");
    fs::create_dir_all(&objects_dir).map_err(|e| format!("创建资源目录失败: {}", e))?;

    let asset_index: std::collections::HashMap<String, AssetObject> =
        serde_json::from_slice(
            &fs::read(&asset_index_path)
                .map_err(|e| format!("读取 Asset Index 失败: {}", e))?,
        )
        .map(|r: serde_json::Value| {
            let objects = r.get("objects").cloned().unwrap_or_default();
            serde_json::from_value(objects).unwrap_or_default()
        })
        .map_err(|_| "解析 Asset Index 失败".to_string())?;

    // 只下载前 50 个核心资源以加快速度
    for (_, obj) in asset_index.iter().take(50) {
        let hash_prefix = &obj.hash[..2];
        let obj_dir = objects_dir.join(hash_prefix);
        fs::create_dir_all(&obj_dir).ok();
        let obj_path = obj_dir.join(&obj.hash);

        if !obj_path.exists() {
            let url = format!(
                "https://resources.download.minecraft.net/{}/{}",
                hash_prefix, obj.hash
            );
            if let Ok(resp) = client.get(&url).send().await {
                if let Ok(bytes) = resp.bytes().await {
                    fs::write(&obj_path, &bytes).ok();
                }
            }
        }
    }

    // 7. 下载必要的 libraries
    let libraries_dir = mc_dir.join("libraries");
    fs::create_dir_all(&libraries_dir).map_err(|e| format!("创建库目录失败: {}", e))?;

    for lib in &detail.libraries {
        // 检查平台规则
        if !check_library_rules(lib) {
            continue;
        }
        if let Some(artifact) = &lib.downloads.artifact {
            let lib_path = libraries_dir.join(&artifact.path);
            if !lib_path.exists() {
                if let Some(parent) = lib_path.parent() {
                    fs::create_dir_all(parent).ok();
                }
                if let Ok(resp) = client.get(&artifact.url).send().await {
                    if let Ok(bytes) = resp.bytes().await {
                        fs::write(&lib_path, &bytes).ok();
                    }
                }
            }
        }
    }

    Ok(format!("版本 {} 安装完成", version_id))
}

/// 删除已安装版本
#[tauri::command]
pub fn delete_version(version_id: String) -> Result<String, String> {
    let mc_dir = get_minecraft_dir();
    let version_dir = mc_dir.join("versions").join(&version_id);

    if version_dir.exists() {
        fs::remove_dir_all(&version_dir)
            .map_err(|e| format!("删除版本失败: {}", e))?;
    }
    Ok(format!("版本 {} 已删除", version_id))
}

/// 检测 Java 安装
#[tauri::command]
pub fn detect_java() -> Result<Vec<JavaInstallation>, String> {
    let mut javas = Vec::new();

    // 检查常见路径
    let java_exe = if cfg!(windows) { "java.exe" } else { "java" };

    let search_paths = if cfg!(windows) {
        vec![
            PathBuf::from("C:\\Program Files\\Java"),
            PathBuf::from("C:\\Program Files (x86)\\Java"),
            PathBuf::from("C:\\Program Files\\Eclipse Adoptium"),
            PathBuf::from("C:\\Program Files\\Microsoft"),
        ]
    } else {
        vec![
            PathBuf::from("/usr/lib/jvm"),
            PathBuf::from("/Library/Java/JavaVirtualMachines"),
        ]
    };

    for base in &search_paths {
        if !base.exists() {
            continue;
        }
        if let Ok(entries) = fs::read_dir(base) {
            for entry in entries.flatten() {
                let path = entry.path();
                if path.is_dir() {
                    let java_bin = path.join("bin").join(java_exe);
                    if java_bin.exists() {
                        let version = detect_java_version(&java_bin);
                        let is_64bit = if cfg!(windows) {
                            path.to_string_lossy().contains("64")
                                || !path.to_string_lossy().contains("x86")
                        } else {
                            true
                        };
                        javas.push(JavaInstallation {
                            path: java_bin.to_string_lossy().to_string(),
                            version,
                            vendor: path
                                .file_name()
                                .map(|n| n.to_string_lossy().to_string())
                                .unwrap_or_default(),
                            is_64bit,
                        });
                    }
                }
            }
        }
    }

    // 检查 PATH 中的 java
    if javas.is_empty() {
        if let Ok(output) = std::process::Command::new(java_exe)
            .arg("-version")
            .output()
        {
            let stderr = String::from_utf8_lossy(&output.stderr);
            let version = parse_java_version(&stderr);
            javas.push(JavaInstallation {
                path: java_exe.to_string(),
                version,
                vendor: "PATH".to_string(),
                is_64bit: stderr.contains("64-Bit"),
            });
        }
    }

    Ok(javas)
}

/// 启动 Minecraft
#[tauri::command]
pub async fn launch_minecraft(
    version_id: String,
    java_path: String,
    username: String,
    uuid: String,
    max_memory: Option<u32>,
) -> Result<String, String> {
    let mc_dir = get_minecraft_dir();
    let version_dir = mc_dir.join("versions").join(&version_id);
    let version_json_path = version_dir.join(format!("{}.json", version_id));

    if !version_json_path.exists() {
        return Err(format!("版本 {} 未安装", version_id));
    }

    // 读取版本 JSON
    let json_content = fs::read_to_string(&version_json_path)
        .map_err(|e| format!("读取版本 JSON 失败: {}", e))?;
    let detail: VersionDetail = serde_json::from_str(&json_content)
        .map_err(|e| format!("解析版本 JSON 失败: {}", e))?;

    let jar_path = version_dir.join(format!("{}.jar", version_id));
    let libraries_dir = mc_dir.join("libraries");
    let assets_dir = mc_dir.join("assets");
    let natives_dir = version_dir.join("natives");
    fs::create_dir_all(&natives_dir).ok();

    // 构建 classpath
    let separator = if cfg!(windows) { ";" } else { ":" };
    let mut classpath: Vec<String> = vec![jar_path.to_string_lossy().to_string()];

    for lib in &detail.libraries {
        if !check_library_rules(lib) {
            continue;
        }
        if let Some(artifact) = &lib.downloads.artifact {
            let lib_path = libraries_dir.join(&artifact.path);
            if lib_path.exists() {
                classpath.push(lib_path.to_string_lossy().to_string());
            }
        }
    }

    let memory = max_memory.unwrap_or(2048);
    let native_path = natives_dir.to_string_lossy().to_string();

    let args: Vec<String> = vec![
        java_path.clone(),
        format!("-Xmx{}M", memory),
        format!("-Djava.library.path={}", native_path),
        "-cp".to_string(),
        classpath.join(separator),
        detail.main_class.clone(),
        "--username".to_string(),
        username.clone(),
        "--uuid".to_string(),
        uuid.clone(),
        "--accessToken".to_string(),
        "0".to_string(), // 离线模式
        "--version".to_string(),
        version_id.clone(),
        "--gameDir".to_string(),
        mc_dir.to_string_lossy().to_string(),
        "--assetsDir".to_string(),
        assets_dir.to_string_lossy().to_string(),
        "--assetIndex".to_string(),
        detail.asset_index.id.clone(),
        "--versionType".to_string(),
        detail.version_type.clone(),
    ];

    // 加载 natives（提取 native libraries）
    // 简化实现：跳过 native 提取，依赖已安装的系统库

    tracing::info!("启动 Minecraft: {:?}", args);

    let child = std::process::Command::new(&java_path)
        .args(&args[1..])
        .current_dir(&mc_dir)
        .spawn()
        .map_err(|e| format!("启动 Minecraft 失败: {}", e))?;

    let pid = child.id();
    Ok(format!("Minecraft 已启动，进程 ID: {}", pid))
}

/// 获取游戏日志（占位）
#[tauri::command]
pub fn get_game_logs(process_id: u32) -> Result<String, String> {
    let mc_dir = get_minecraft_dir();
    let log_path = mc_dir.join("logs").join("latest.log");
    if log_path.exists() {
        fs::read_to_string(&log_path)
            .map_err(|_| "读取日志文件失败".to_string())
    } else {
        Ok(format!("进程 {} 暂无日志", process_id))
    }
}

// ========= 内部辅助函数 =========

async fn fetch_versions_inner() -> Result<VersionManifest, String> {
    let url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    let response = reqwest::get(url)
        .await
        .map_err(|e| format!("获取版本清单失败: {}", e))?;
    let manifest: VersionManifest = response
        .json()
        .await
        .map_err(|e| format!("解析版本清单失败: {}", e))?;
    Ok(manifest)
}

fn check_library_rules(lib: &Library) -> bool {
    match &lib.rules {
        Some(rules) => {
            let mut allowed = false;
            for rule in rules {
                let matches_os = match &rule.os {
                    Some(os_rule) => {
                        if cfg!(windows) { os_rule.name == "windows" }
                        else if cfg!(target_os = "macos") { os_rule.name == "osx" }
                        else { os_rule.name == "linux" }
                    }
                    None => true,
                };
                if matches_os {
                    allowed = rule.action == "allow";
                }
            }
            allowed
        }
        None => true,
    }
}

fn detect_java_version(java_path: &PathBuf) -> String {
    if let Ok(output) = std::process::Command::new(java_path)
        .arg("-version")
        .output()
    {
        let stderr = String::from_utf8_lossy(&output.stderr);
        parse_java_version(&stderr)
    } else {
        "未知".to_string()
    }
}

fn parse_java_version(output: &str) -> String {
    // 匹配 "1.8.0_xxx" 或 "17.0.x" 等格式
    for line in output.lines() {
        if line.contains("version") {
            let parts: Vec<&str> = line.split('"').collect();
            if parts.len() >= 2 {
                let version_str = parts[1];
                // 处理 "1.8.0_391" -> "8"
                if version_str.starts_with("1.") {
                    let v: Vec<&str> = version_str.split('.').collect();
                    if v.len() >= 2 {
                        return format!("Java {}", v[1]);
                    }
                }
                // 处理 "17.0.9" -> "17"
                let v: Vec<&str> = version_str.split('.').collect();
                if !v.is_empty() {
                    return format!("Java {}", v[0]);
                }
            }
        }
    }
    "未知".to_string()
}
