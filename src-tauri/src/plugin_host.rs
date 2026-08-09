//! GUI 插件宿主 - plugin.json 扫描 + furry:// 静态资源协议
//!
//! 双通道机制：
//! - **view**：`furry://views/{plugin_id}/{path}` 挂载为 iframe
//! - **replacement**：`furry://replacement/{plugin_id}/{path}` 作为主 shell（默认 GUI 兜底）
//!
//! 目录规范：`plugins/{id}/plugin.json`、`plugins/{id}/frontend/**`、
//! `plugins/{id}/native/**`（可选原生模块，本阶段预留接口）。

use serde::{Deserialize, Serialize};
use std::borrow::Cow;
use std::path::{Path, PathBuf};
use tauri::http;
use tauri::Manager;

/// 启动器版本（GUI 插件 min_launcher_version 校验基准）
pub const LAUNCHER_VERSION: &str = "0.2.0";
/// 当前支持的 GUI 插件 API 版本
pub const GUI_PLUGIN_API_VERSION: u32 = 1;

/// GUI 插件清单（plugin.json）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GuiPluginMeta {
    pub id: String,
    pub name: String,
    pub version: String,
    #[serde(default = "default_api_version")]
    pub api_version: u32,
    #[serde(default)]
    pub min_launcher_version: Option<String>,
    pub gui: GuiPluginConfig,
}

fn default_api_version() -> u32 {
    GUI_PLUGIN_API_VERSION
}

/// GUI 插件配置段
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GuiPluginConfig {
    /// "view" | "replacement"
    #[serde(rename = "type")]
    pub gui_type: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub icon: Option<String>,
    /// 相对 frontend/ 的入口（默认 index.html）
    #[serde(default = "default_entry")]
    pub entry: String,
    /// 可选原生模块路径（native/xxx.dll，本阶段不加载）
    #[serde(default)]
    pub native_module: Option<String>,
}

fn default_entry() -> String {
    "index.html".into()
}

/// 前端可用的插件摘要
#[derive(Debug, Clone, Serialize)]
pub struct GuiPluginInfo {
    pub id: String,
    pub name: String,
    pub version: String,
    pub gui_type: String,
    pub title: String,
    pub entry: String,
    pub has_native: bool,
    pub compatible: bool,
    pub reason: Option<String>,
}

impl GuiPluginInfo {
    /// 校验并构建摘要
    fn from_meta(meta: &GuiPluginMeta) -> Self {
        // api_version 校验
        if meta.api_version != GUI_PLUGIN_API_VERSION {
            return Self {
                id: meta.id.clone(),
                name: meta.name.clone(),
                version: meta.version.clone(),
                gui_type: meta.gui.gui_type.clone(),
                title: meta.gui.title.clone(),
                entry: meta.gui.entry.clone(),
                has_native: meta.gui.native_module.is_some(),
                compatible: false,
                reason: Some(format!(
                    "api_version {} 不受支持（当前 {}）",
                    meta.api_version, GUI_PLUGIN_API_VERSION
                )),
            };
        }
        // min_launcher_version 校验
        if let Some(min) = &meta.min_launcher_version {
            if starcloud_minecraft::version_manager::VersionComparator::compare(
                LAUNCHER_VERSION,
                min,
            ) == std::cmp::Ordering::Less
            {
                return Self {
                    id: meta.id.clone(),
                    name: meta.name.clone(),
                    version: meta.version.clone(),
                    gui_type: meta.gui.gui_type.clone(),
                    title: meta.gui.title.clone(),
                    entry: meta.gui.entry.clone(),
                    has_native: meta.gui.native_module.is_some(),
                    compatible: false,
                    reason: Some(format!(
                        "需要启动器 >= {}（当前 {}）",
                        min, LAUNCHER_VERSION
                    )),
                };
            }
        }
        Self {
            id: meta.id.clone(),
            name: meta.name.clone(),
            version: meta.version.clone(),
            gui_type: meta.gui.gui_type.clone(),
            title: meta.gui.title.clone(),
            entry: meta.gui.entry.clone(),
            has_native: meta.gui.native_module.is_some(),
            compatible: true,
            reason: None,
        }
    }
}

/// 扫描插件目录，返回全部 GUI 插件摘要
pub fn scan_gui_plugins(plugins_dir: &Path) -> Vec<GuiPluginInfo> {
    let mut out = Vec::new();
    let Ok(entries) = std::fs::read_dir(plugins_dir) else {
        return out;
    };
    for entry in entries.flatten() {
        let dir = entry.path();
        if !dir.is_dir() {
            continue;
        }
        let Ok(data) = std::fs::read_to_string(dir.join("plugin.json")) else {
            continue;
        };
        match serde_json::from_str::<GuiPluginMeta>(&data) {
            Ok(meta) => out.push(GuiPluginInfo::from_meta(&meta)),
            Err(e) => log::warn!("跳过无效 GUI 插件清单 {}: {}", dir.display(), e),
        }
    }
    out
}

/// 解析 GUI 插件清单（供加载/校验用；阶段 3 原生模块加载时使用）
#[allow(dead_code)]
pub fn parse_plugin_meta(plugins_dir: &Path, plugin_id: &str) -> Option<GuiPluginMeta> {
    let path = plugins_dir.join(plugin_id).join("plugin.json");
    let data = std::fs::read_to_string(path).ok()?;
    serde_json::from_str(&data).ok()
}

/// 解析 furry:// 请求并返回响应
///
/// URL 格式：
/// - `furry://views/{plugin_id}/{path}` → `plugins/{plugin_id}/frontend/{path}`
/// - `furry://replacement/{plugin_id}/{path}` → 同上（整体接管模式）
pub fn handle_furry_request(
    ctx: tauri::UriSchemeContext<tauri::Wry>,
    request: http::Request<Vec<u8>>,
) -> http::Response<Cow<'static, [u8]>> {
    // 获取插件目录（来自应用配置）
    let plugins_dir = {
        let app = ctx.app_handle();
        app.state::<crate::state::AppState>()
            .config
            .lock()
            .unwrap()
            .plugins_dir
            .clone()
    };

    let uri = request.uri();
    let host = uri.host().unwrap_or("").to_string();
    let path = uri.path().to_string();

    // host 必须是 views / replacement
    match host.as_str() {
        "views" | "replacement" => {}
        _ => return error_response(400, "furry:// 仅支持 views / replacement 模式"),
    }
    let rest = path;

    // 解析 plugin_id 与相对路径
    let rest_trimmed = rest.trim_start_matches('/');
    let mut parts = rest_trimmed.splitn(2, '/');
    let (Some(plugin_id), rel) = (parts.next(), parts.next()) else {
        return error_response(400, "furry:// 路径格式错误");
    };
    if plugin_id.is_empty() {
        return error_response(400, "缺少插件 ID");
    }
    let rel = rel.unwrap_or("");

    // 插件目录 + frontend 基础目录（目录穿越防护）
    let frontend_base = plugins_dir.join(plugin_id).join("frontend");
    let Some(clean_rel) = clean_relative(rel) else {
        return error_response(403, "路径穿越已被拒绝");
    };
    // 空路径或目录 → 默认入口 index.html
    let file_name = if clean_rel.is_empty() {
        "index.html".to_string()
    } else {
        clean_rel
    };

    let Some(file_path) = safe_join(&frontend_base, &file_name) else {
        return error_response(403, "路径穿越已被拒绝");
    };

    match std::fs::read(&file_path) {
        Ok(bytes) => {
            let mime = mime_for(&file_name);
            http::Response::builder()
                .status(200)
                .header("Content-Type", mime)
                .header("Cache-Control", "no-cache")
                .body(Cow::Owned(bytes))
                .unwrap_or_else(|_| error_response(500, "构造响应失败"))
        }
        Err(_) => error_response(404, "文件不存在"),
    }
}

/// 规范化相对路径：拒绝空组件穿越，返回可安全拼接的相对路径
fn clean_relative(path: &str) -> Option<String> {
    let mut out = Vec::new();
    for comp in path.split('/') {
        match comp {
            "" | "." => continue,
            ".." => return None,
            c => out.push(c),
        }
    }
    Some(out.join("/"))
}

/// 安全拼接：base + 相对路径（不允许逃逸 base）
fn safe_join(base: &Path, rel: &str) -> Option<PathBuf> {
    let mut result = base.to_path_buf();
    for comp in rel.split('/') {
        if comp.is_empty() || comp == "." {
            continue;
        }
        if comp == ".." {
            return None;
        }
        result.push(comp);
    }
    Some(result)
}

/// MIME 类型映射
fn mime_for(path: &str) -> &'static str {
    match Path::new(path)
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
    {
        "html" => "text/html; charset=utf-8",
        "css" => "text/css; charset=utf-8",
        "js" | "mjs" => "text/javascript; charset=utf-8",
        "json" => "application/json",
        "svg" => "image/svg+xml",
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "ico" => "image/x-icon",
        "woff" => "font/woff",
        "woff2" => "font/woff2",
        "wasm" => "application/wasm",
        _ => "application/octet-stream",
    }
}

/// 构造错误响应
fn error_response(status: u16, message: &str) -> http::Response<Cow<'static, [u8]>> {
    http::Response::builder()
        .status(status)
        .header("Content-Type", "text/plain; charset=utf-8")
        .body(Cow::Owned(message.as_bytes().to_vec()))
        .unwrap_or_else(|_| http::Response::new(Cow::Borrowed(b"error".as_slice())))
}

// ---------------------------------------------------------------------------
// 单元测试
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn clean_relative_拒绝穿越() {
        assert_eq!(clean_relative("a/b/c").as_deref(), Some("a/b/c"));
        assert_eq!(clean_relative("index.html").as_deref(), Some("index.html"));
        assert_eq!(clean_relative("").as_deref(), Some(""));
        assert_eq!(clean_relative("./a/./b").as_deref(), Some("a/b"));
        // 任何 .. 组件直接拒绝
        assert_eq!(clean_relative("../secret"), None);
        assert_eq!(clean_relative("a/../../etc"), None);
    }

    #[test]
    fn safe_join_不逃逸base() {
        let base = PathBuf::from("/plugins/demo/frontend");
        assert_eq!(
            safe_join(&base, "index.html"),
            Some(PathBuf::from("/plugins/demo/frontend/index.html"))
        );
        assert_eq!(
            safe_join(&base, "sub/app.js"),
            Some(base.join("sub/app.js"))
        );
        assert_eq!(safe_join(&base, "../x"), None);
        assert_eq!(safe_join(&base, "a/../../x"), None);
    }

    #[test]
    fn mime_映射() {
        assert!(mime_for("index.html").contains("text/html"));
        assert!(mime_for("style.css").contains("text/css"));
        assert!(mime_for("app.js").contains("text/javascript"));
        assert_eq!(mime_for("data.json"), "application/json");
        assert_eq!(mime_for("icon.png"), "image/png");
        assert_eq!(mime_for("noext"), "application/octet-stream");
    }

    #[test]
    fn 扫描示例插件并校验api版本() {
        let dir = std::env::temp_dir().join("furry-plugin-test");
        let _ = fs::remove_dir_all(&dir);
        let plugin = dir.join("demo");
        fs::create_dir_all(&plugin).unwrap();
        fs::write(
            plugin.join("plugin.json"),
            r#"{
                "id": "demo",
                "name": "示例",
                "version": "0.1.0",
                "api_version": 1,
                "gui": { "type": "view", "title": "示例视图", "entry": "index.html" }
            }"#,
        )
        .unwrap();

        let infos = scan_gui_plugins(&dir);
        assert_eq!(infos.len(), 1);
        let info = &infos[0];
        assert_eq!(info.id, "demo");
        assert_eq!(info.gui_type, "view");
        assert!(info.compatible);
        assert!(info.reason.is_none());

        // 不兼容版本
        fs::write(
            plugin.join("plugin.json"),
            r#"{
                "id": "demo",
                "name": "示例",
                "version": "0.1.0",
                "api_version": 99,
                "gui": { "type": "view", "title": "示例视图", "entry": "index.html" }
            }"#,
        )
        .unwrap();
        let infos = scan_gui_plugins(&dir);
        assert!(!infos[0].compatible);
        assert!(infos[0].reason.is_some());

        let _ = fs::remove_dir_all(&dir);
    }
}
