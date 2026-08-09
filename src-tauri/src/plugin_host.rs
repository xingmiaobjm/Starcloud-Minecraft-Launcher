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
    /// 是否启用（plugins/{id}/.disabled 标记文件不存在时为启用）
    pub enabled: bool,
}

impl GuiPluginInfo {
    /// 校验并构建摘要
    fn from_meta(meta: &GuiPluginMeta, enabled: bool) -> Self {
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
                enabled,
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
                    enabled,
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
            enabled,
        }
    }
}

/// 扫描插件目录，返回全部 GUI 插件摘要（跳过禁用与临时安装目录）
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
        // 禁用标记或临时安装目录 → 跳过
        if dir.join(".disabled").exists() {
            continue;
        }
        if let Some(name) = dir.file_name().and_then(|n| n.to_str()) {
            if name.starts_with(".tmp-install-") {
                continue;
            }
        }
        let Ok(data) = std::fs::read_to_string(dir.join("plugin.json")) else {
            continue;
        };
        match serde_json::from_str::<GuiPluginMeta>(&data) {
            Ok(meta) => out.push(GuiPluginInfo::from_meta(&meta, true)),
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

// ---------------------------------------------------------------------------
// 插件包管理（安装/删除/启用禁用）
// ---------------------------------------------------------------------------

/// 插件 ID 合法字符（防路径穿越）
fn valid_plugin_id(id: &str) -> bool {
    !id.is_empty()
        && id != "."
        && id != ".."
        && id.len() <= 128
        && id
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '.' || c == '_' || c == '-')
}

/// 安装 GUI 插件（zip 包，来源为 URL 或本地路径）
///
/// 包内必须包含根级 `plugin.json`；安装到 `plugins/{id}/`（先删除旧目录）。
pub async fn install_gui_plugin(plugins_dir: &Path, source: &str) -> Result<GuiPluginInfo, String> {
    // 1. 获取 zip 字节
    let bytes = if source.starts_with("http://") || source.starts_with("https://") {
        let http = starcloud_minecraft::download::http_client::HttpClient::new()
            .map_err(|e| format!("初始化下载器失败: {}", e))?;
        http.get_bytes_with_retry(source)
            .await
            .map_err(|e| format!("下载插件包失败: {}", e))?
    } else {
        std::fs::read(source).map_err(|e| format!("读取本地插件包失败: {}", e))?
    };

    std::fs::create_dir_all(plugins_dir).map_err(|e| format!("创建插件目录失败: {}", e))?;

    // 2. 解压到临时目录
    let tmp = plugins_dir.join(format!(".tmp-install-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&tmp);
    std::fs::create_dir_all(&tmp).map_err(|e| format!("创建临时目录失败: {}", e))?;
    if let Err(e) = extract_zip(&bytes, &tmp) {
        let _ = std::fs::remove_dir_all(&tmp);
        return Err(format!("解压插件包失败: {}", e));
    }

    // 3. 校验 plugin.json
    let meta: GuiPluginMeta = match std::fs::read(tmp.join("plugin.json")) {
        Ok(data) => {
            serde_json::from_slice(&data).map_err(|e| format!("plugin.json 无效: {}", e))?
        }
        Err(_) => {
            let _ = std::fs::remove_dir_all(&tmp);
            return Err("插件包缺少 plugin.json".into());
        }
    };
    if !valid_plugin_id(&meta.id) {
        let _ = std::fs::remove_dir_all(&tmp);
        return Err(format!("插件 ID 非法: {}", meta.id));
    }
    if meta.gui.gui_type != "view" && meta.gui.gui_type != "replacement" {
        let _ = std::fs::remove_dir_all(&tmp);
        return Err(format!("未知的 GUI 插件类型: {}", meta.gui.gui_type));
    }

    // 4. 移入目标目录（覆盖旧版本）
    let target = plugins_dir.join(&meta.id);
    if target.exists() {
        std::fs::remove_dir_all(&target).map_err(|e| format!("清理旧插件失败: {}", e))?;
    }
    std::fs::rename(&tmp, &target).map_err(|e| format!("移动插件目录失败: {}", e))?;

    log::info!(
        "已安装 GUI 插件 {} v{}（{}）",
        meta.id,
        meta.version,
        meta.gui.gui_type
    );
    Ok(GuiPluginInfo::from_meta(&meta, true))
}

/// 删除 GUI 插件
pub fn remove_gui_plugin(plugins_dir: &Path, plugin_id: &str) -> Result<(), String> {
    if !valid_plugin_id(plugin_id) {
        return Err(format!("插件 ID 非法: {}", plugin_id));
    }
    let dir = plugins_dir.join(plugin_id);
    if !dir.exists() {
        return Err(format!("插件不存在: {}", plugin_id));
    }
    std::fs::remove_dir_all(&dir).map_err(|e| format!("删除插件失败: {}", e))?;
    log::info!("已删除 GUI 插件 {}", plugin_id);
    Ok(())
}

/// 启用/禁用 GUI 插件（.disabled 标记文件）
pub fn set_gui_plugin_enabled(
    plugins_dir: &Path,
    plugin_id: &str,
    enabled: bool,
) -> Result<(), String> {
    if !valid_plugin_id(plugin_id) {
        return Err(format!("插件 ID 非法: {}", plugin_id));
    }
    let marker = plugins_dir.join(plugin_id).join(".disabled");
    if enabled {
        if marker.exists() {
            std::fs::remove_file(&marker).map_err(|e| format!("启用插件失败: {}", e))?;
        }
        log::info!("已启用 GUI 插件 {}", plugin_id);
    } else {
        std::fs::write(&marker, "disabled").map_err(|e| format!("禁用插件失败: {}", e))?;
        log::info!("已禁用 GUI 插件 {}", plugin_id);
    }
    Ok(())
}

/// 解压 zip 到目标目录（enclosed_name 防路径穿越）
fn extract_zip(bytes: &[u8], dest: &Path) -> std::result::Result<(), String> {
    let mut archive =
        zip::ZipArchive::new(std::io::Cursor::new(bytes)).map_err(|e| e.to_string())?;
    for i in 0..archive.len() {
        let mut file = archive.by_index(i).map_err(|e| e.to_string())?;
        let Some(rel) = file.enclosed_name() else {
            continue; // 拒绝绝对路径与 .. 穿越条目
        };
        let out = dest.join(rel);
        if file.is_dir() {
            std::fs::create_dir_all(&out).map_err(|e| e.to_string())?;
            continue;
        }
        if let Some(parent) = out.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let mut writer =
            std::fs::File::create(&out).map_err(|e| format!("创建 {}: {}", out.display(), e))?;
        std::io::copy(&mut file, &mut writer).map_err(|e| e.to_string())?;
    }
    Ok(())
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

    #[test]
    fn valid_plugin_id_校验() {
        assert!(valid_plugin_id("com.example.view"));
        assert!(valid_plugin_id("demo_1"));
        assert!(valid_plugin_id("a-b.c_d"));
        assert!(!valid_plugin_id(""));
        assert!(!valid_plugin_id(".."));
        assert!(!valid_plugin_id("../evil"));
        assert!(!valid_plugin_id("a/b"));
        assert!(!valid_plugin_id("a\\b"));
        assert!(!valid_plugin_id(&"x".repeat(129)));
    }

    /// 构造一个最小 GUI 插件 zip（plugin.json + frontend/index.html）
    fn build_plugin_zip(id: &str) -> Vec<u8> {
        use std::io::Write;
        let mut buf = std::io::Cursor::new(Vec::new());
        {
            let mut zip = zip::ZipWriter::new(&mut buf);
            let opts: zip::write::FileOptions<'_, ()> = Default::default();
            zip.start_file("plugin.json", opts).unwrap();
            zip.write_all(
                format!(
                    r#"{{"id":"{id}","name":"测试插件","version":"1.0.0","api_version":1,"gui":{{"type":"view","title":"测试"}}}}"#
                )
                .as_bytes(),
            )
            .unwrap();
            zip.start_file("frontend/index.html", opts).unwrap();
            zip.write_all(b"<h1>hello</h1>").unwrap();
            zip.finish().unwrap();
        }
        buf.into_inner()
    }

    #[tokio::test]
    async fn 插件安装_删除_禁用流程() {
        let dir = std::env::temp_dir().join("furry-gui-plugin-mgmt");
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();

        // 写入 zip 文件
        let zip_path = dir.join("demo.zip");
        fs::write(&zip_path, build_plugin_zip("demo")).unwrap();

        // 安装
        let info = install_gui_plugin(&dir, zip_path.to_str().unwrap())
            .await
            .unwrap();
        assert_eq!(info.id, "demo");
        assert_eq!(info.gui_type, "view");
        assert!(info.enabled);
        assert!(dir.join("demo").join("plugin.json").exists());
        assert!(dir.join("demo").join("frontend/index.html").exists());

        // 扫描可见
        let infos = scan_gui_plugins(&dir);
        assert_eq!(infos.len(), 1);
        assert!(infos[0].enabled);

        // 禁用 → 扫描跳过
        set_gui_plugin_enabled(&dir, "demo", false).unwrap();
        assert!(scan_gui_plugins(&dir).is_empty());

        // 启用 → 重新可见
        set_gui_plugin_enabled(&dir, "demo", true).unwrap();
        assert_eq!(scan_gui_plugins(&dir).len(), 1);

        // 删除
        remove_gui_plugin(&dir, "demo").unwrap();
        assert!(!dir.join("demo").exists());
        assert!(scan_gui_plugins(&dir).is_empty());

        // 删除不存在的插件 → 报错
        assert!(remove_gui_plugin(&dir, "demo").is_err());

        // 重新安装同 ID 插件（覆盖安装）→ 成功
        let info = install_gui_plugin(&dir, zip_path.to_str().unwrap())
            .await
            .unwrap();
        assert_eq!(info.id, "demo");

        // 路径穿越 ID 被拒绝
        assert!(remove_gui_plugin(&dir, "../evil").is_err());
        assert!(set_gui_plugin_enabled(&dir, "../evil", true).is_err());

        let _ = fs::remove_dir_all(&dir);
    }

    #[tokio::test]
    async fn 插件包缺少plugin_json报错() {
        use std::io::Write;
        let dir = std::env::temp_dir().join("furry-gui-plugin-bad");
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();

        // 无 plugin.json 的 zip
        let mut buf = std::io::Cursor::new(Vec::new());
        {
            let mut zip = zip::ZipWriter::new(&mut buf);
            let opts: zip::write::FileOptions<'_, ()> = Default::default();
            zip.start_file("readme.txt", opts).unwrap();
            zip.write_all(b"hi").unwrap();
            zip.finish().unwrap();
        }
        let zip_path = dir.join("bad.zip");
        fs::write(&zip_path, buf.into_inner()).unwrap();

        let err = install_gui_plugin(&dir, zip_path.to_str().unwrap())
            .await
            .unwrap_err();
        assert!(err.contains("plugin.json"), "实际错误: {}", err);
        // 临时目录应被清理
        assert!(fs::read_dir(&dir).unwrap().all(|e| !e
            .unwrap()
            .file_name()
            .to_string_lossy()
            .starts_with(".tmp-")));

        let _ = fs::remove_dir_all(&dir);
    }
}
