// 插件系统命令
// StarCloud Minecraft Launcher - LGPL-3.0-only

use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;

#[derive(Serialize, Deserialize, Clone)]
pub struct PluginInfo {
    pub id: String,
    pub name: String,
    pub version: String,
    pub description: String,
    pub enabled: bool,
    pub author: Option<String>,
    pub homepage: Option<String>,
    pub permissions: Option<Vec<String>>,
}

#[derive(Deserialize)]
struct PluginManifest {
    id: String,
    name: String,
    #[serde(default)]
    description: String,
    version: String,
    #[serde(default)]
    author: Option<String>,
    #[serde(default)]
    homepage: Option<String>,
    #[serde(default)]
    permissions: Vec<String>,
    #[serde(default)]
    main: String,
}

/// 获取插件目录
fn get_plugins_dir() -> PathBuf {
    let exe_dir = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));
    // 尝试多个可能的位置
    let candidates = vec![
        exe_dir.join("plugins"),
        exe_dir.join("../plugins"),
        exe_dir.join("../../plugins"),
    ];
    for dir in &candidates {
        if dir.exists() {
            return dir.clone();
        }
    }
    // 默认返回启动器所在目录下的 plugins
    exe_dir.join("plugins")
}

/// 扫描插件目录，获取所有可用插件
fn scan_plugins_dir() -> Vec<PluginInfo> {
    let plugins_dir = get_plugins_dir();
    let mut plugins = Vec::new();

    if let Ok(entries) = fs::read_dir(&plugins_dir) {
        for entry in entries.flatten() {
            let plugin_dir = entry.path();
            if !plugin_dir.is_dir() {
                continue;
            }
            let manifest_path = plugin_dir.join("plugin.json");
            if !manifest_path.exists() {
                continue;
            }

            if let Ok(content) = fs::read_to_string(&manifest_path) {
                if let Ok(manifest) = serde_json::from_str::<PluginManifest>(&content) {
                    plugins.push(PluginInfo {
                        id: manifest.id,
                        name: manifest.name,
                        version: manifest.version,
                        description: manifest.description,
                        enabled: false,
                        author: manifest.author,
                        homepage: manifest.homepage,
                        permissions: if manifest.permissions.is_empty() {
                            None
                        } else {
                            Some(manifest.permissions)
                        },
                    });
                }
            }
        }
    }

    plugins
}

/// 加载插件
#[tauri::command]
pub fn plugin_load(path: String) -> Result<PluginInfo, String> {
    // 如果指定了路径，从指定路径加载
    let manifest_path = if path.is_empty() {
        return Err("请指定插件路径".to_string());
    } else {
        PathBuf::from(&path).join("plugin.json")
    };

    let content = fs::read_to_string(&manifest_path)
        .map_err(|e| format!("读取 plugin.json 失败: {}", e))?;

    let manifest: PluginManifest =
        serde_json::from_str(&content).map_err(|e| format!("解析 plugin.json 失败: {}", e))?;

    Ok(PluginInfo {
        id: manifest.id,
        name: manifest.name,
        version: manifest.version,
        description: manifest.description,
        enabled: true,
        author: manifest.author,
        homepage: manifest.homepage,
        permissions: if manifest.permissions.is_empty() {
            None
        } else {
            Some(manifest.permissions)
        },
    })
}

/// 卸载插件
#[tauri::command]
pub fn plugin_unload(plugin_id: String) -> Result<bool, String> {
    // 实际卸载由前端 PluginManager 完成，这里只是后端状态清理
    println!("卸载插件: {}", plugin_id);
    Ok(true)
}

/// 获取插件列表
#[tauri::command]
pub fn plugin_list() -> Result<Vec<PluginInfo>, String> {
    Ok(scan_plugins_dir())
}
