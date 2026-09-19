//! GUI 插件宿主 - plugin.json 扫描 + furry:// 通用资源协议 v2
//!
//! 协议适用场景：启动器内部任意嵌入位置（非公网网页）。
//! - `furry://view/{plugin_id}/{view_id}/{path}?query` → 具名视图，可挂载到任意 UI 插槽
//! - `furry://shell/{plugin_id}/{path}`                → 整体接管主窗口（replacement）
//! - `furry://assets/{plugin_id}/{path}`               → 插件静态资源（跨场景共享）
//! 兼容旧版 v1：
//! - `furry://views/{plugin_id}/{path}`                → view（view_id = main）
//! - `furry://replacement/{plugin_id}/{path}`          → shell
//!
//! 目录规范：`plugins/{id}/plugin.json`、`plugins/{id}/frontend/**`、
//! `plugins/{id}/native/**`（可选原生模块，本阶段预留接口）。
//! view 的资源路径相对 `frontend/` 根；view_id 仅作声明校验（plugin.json 的 gui.views）。

use serde::{Deserialize, Serialize};
use std::borrow::Cow;
use std::path::{Path, PathBuf};
use tauri::http;
use tauri::Manager;

/// 启动器版本（GUI 插件 min_launcher_version 校验基准）
///
/// 单一来源：src-tauri/Cargo.toml 的 `version` 字段（cargo 编译期注入，
/// 修改 Cargo.toml 版本后自动生效，无需再改此处）。
pub const LAUNCHER_VERSION: &str = env!("CARGO_PKG_VERSION");
/// 当前支持的 GUI 插件 API 版本
pub const GUI_PLUGIN_API_VERSION: u32 = 2;

/// 内置可嵌入插槽（view 允许挂载的位置）
/// 协议契约常量：插件开发与前端嵌入点均引用这些名字（无需直接在本 crate 调用）。
#[allow(dead_code)]
pub const SLOT_PAGE: &str = "page";       // 页面级视图
#[allow(dead_code)]
pub const SLOT_DRAWER: &str = "drawer";   // 抽屉
#[allow(dead_code)]
pub const SLOT_HOME: &str = "home";       // 首页微件
#[allow(dead_code)]
pub const SLOT_SETTINGS: &str = "settings"; // 设置页
#[allow(dead_code)]
pub const SLOT_SIDEBAR: &str = "sidebar"; // 侧栏微件
/// 全窗接管（shell 专用，非插槽）
#[allow(dead_code)]
pub const SLOT_SHELL: &str = "shell";

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

/// 具名视图声明（v2）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PluginViewDecl {
    /// 视图 ID（furry://view/{pid}/{vid}/... 路由用）
    pub id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub icon: Option<String>,
    /// 相对 frontend/ 的入口（默认 index.html）
    #[serde(default = "default_entry")]
    pub entry: String,
    /// 可嵌入插槽：page / drawer / home / settings / sidebar
    #[serde(default)]
    pub slots: Vec<String>,
}

/// 主壳接管声明（v2，原 replacement）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PluginShellDecl {
    #[serde(default)]
    pub title: String,
    /// 相对 frontend/ 的入口（默认 index.html）
    #[serde(default = "default_entry")]
    pub entry: String,
}

/// GUI 插件配置段（v2：views + shell；向后兼容 v1 的 type/title/entry）
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct GuiPluginConfig {
    #[serde(default)]
    pub views: Vec<PluginViewDecl>,
    #[serde(default)]
    pub shell: Option<PluginShellDecl>,
    /// 可选原生模块路径（native/xxx.dll，本阶段不加载）
    #[serde(default)]
    pub native_module: Option<String>,
    // ---- 兼容 v1（gui.type = view | replacement）----
    #[serde(rename = "type", default, skip_serializing)]
    pub legacy_type: Option<String>,
    #[serde(default, skip_serializing)]
    pub title: String,
    #[serde(default, skip_serializing)]
    pub icon: Option<String>,
    #[serde(default, skip_serializing)]
    pub entry: String,
}

impl GuiPluginConfig {
    /// 归一化：v1 格式（type=view/replacement）→ v2 的 views/shell
    pub fn normalize(&mut self) {
        let entry = || {
            if self.entry.is_empty() {
                "index.html".to_string()
            } else {
                self.entry.clone()
            }
        };
        match self.legacy_type.as_deref() {
            Some("replacement") => {
                if self.shell.is_none() {
                    self.shell = Some(PluginShellDecl {
                        title: self.title.clone(),
                        entry: entry(),
                    });
                }
            }
            Some("view") => {
                if self.views.is_empty() {
                    self.views.push(PluginViewDecl {
                        id: "main".into(),
                        title: self.title.clone(),
                        icon: self.icon.clone(),
                        entry: entry(),
                        slots: vec![SLOT_PAGE.into()],
                    });
                }
            }
            _ => {}
        }
        self.legacy_type = None;
    }

    /// 是否声明了任何 GUI（视图或主壳）
    pub fn has_gui(&self) -> bool {
        !self.views.is_empty() || self.shell.is_some()
    }

    /// 插件类别：view / shell / mixed / none
    pub fn category(&self) -> &'static str {
        match (self.views.is_empty(), self.shell.is_some()) {
            (false, true) => "mixed",
            (false, false) => "view",
            (true, true) => "shell",
            (true, false) => "none",
        }
    }
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
    /// 插件类别：view / shell / mixed
    pub gui_type: String,
    pub title: String,
    pub entry: String,
    pub has_native: bool,
    pub compatible: bool,
    pub reason: Option<String>,
    /// 是否启用（plugins/{id}/.disabled 标记文件不存在时为启用）
    pub enabled: bool,
    /// 具名视图列表（可嵌入插槽）
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub views: Vec<PluginViewInfo>,
    /// 主壳接管入口（相对 frontend/）
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub shell: Option<String>,
    /// 全部可嵌入插槽（去重）
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub slots: Vec<String>,
}

/// 前端可用的视图摘要
#[derive(Debug, Clone, Serialize)]
pub struct PluginViewInfo {
    pub id: String,
    pub title: String,
    pub entry: String,
    pub slots: Vec<String>,
}

impl GuiPluginInfo {
    /// 校验并构建摘要（gui 需已 normalize）
    fn from_meta(meta: &GuiPluginMeta, enabled: bool) -> Self {
        let views = meta
            .gui
            .views
            .iter()
            .map(|v| PluginViewInfo {
                id: v.id.clone(),
                title: v.title.clone(),
                entry: v.entry.clone(),
                slots: v.slots.clone(),
            })
            .collect::<Vec<_>>();
        let slots = meta
            .gui
            .views
            .iter()
            .flat_map(|v| v.slots.iter().cloned())
            .collect::<std::collections::HashSet<_>>();
        let shell = meta.gui.shell.as_ref().map(|s| s.entry.clone());

        let incompatible = |reason: String| Self {
            id: meta.id.clone(),
            name: meta.name.clone(),
            version: meta.version.clone(),
            gui_type: meta.gui.category().to_string(),
            title: meta.gui.views.first().map(|v| v.title.clone()).unwrap_or_default(),
            entry: meta
                .gui
                .views
                .first()
                .map(|v| v.entry.clone())
                .unwrap_or_else(|| {
                    meta.gui.shell.as_ref().map(|s| s.entry.clone()).unwrap_or_default()
                }),
            has_native: meta.gui.native_module.is_some(),
            compatible: false,
            reason: Some(reason),
            enabled,
            views: views.clone(),
            shell: shell.clone(),
            slots: slots.clone().into_iter().collect(),
        };

        // api_version 校验
        if meta.api_version != GUI_PLUGIN_API_VERSION {
            return incompatible(format!(
                "api_version {} 不受支持（当前 {}）",
                meta.api_version, GUI_PLUGIN_API_VERSION
            ));
        }
        // min_launcher_version 校验
        if let Some(min) = &meta.min_launcher_version {
            if starcloud_minecraft::version_manager::VersionComparator::compare(
                LAUNCHER_VERSION,
                min,
            ) == std::cmp::Ordering::Less
            {
                return incompatible(format!(
                    "需要启动器 >= {}（当前 {}）",
                    min, LAUNCHER_VERSION
                ));
            }
        }
        Self {
            id: meta.id.clone(),
            name: meta.name.clone(),
            version: meta.version.clone(),
            gui_type: meta.gui.category().to_string(),
            title: meta.gui.views.first().map(|v| v.title.clone()).unwrap_or_default(),
            entry: meta
                .gui
                .views
                .first()
                .map(|v| v.entry.clone())
                .unwrap_or_else(|| {
                    meta.gui.shell.as_ref().map(|s| s.entry.clone()).unwrap_or_default()
                }),
            has_native: meta.gui.native_module.is_some(),
            compatible: true,
            reason: None,
            enabled,
            views,
            shell,
            slots: slots.into_iter().collect(),
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
            Ok(mut meta) => {
                meta.gui.normalize();
                out.push(GuiPluginInfo::from_meta(&meta, true));
            }
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
    let mut meta: GuiPluginMeta = serde_json::from_str(&data).ok()?;
    meta.gui.normalize();
    Some(meta)
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
    let mut meta: GuiPluginMeta = match std::fs::read(tmp.join("plugin.json")) {
        Ok(data) => {
            serde_json::from_slice(&data).map_err(|e| format!("plugin.json 无效: {}", e))?
        }
        Err(_) => {
            let _ = std::fs::remove_dir_all(&tmp);
            return Err("插件包缺少 plugin.json".into());
        }
    };
    meta.gui.normalize(); // 兼容 v1 清单（type=view/replacement）
    if !valid_plugin_id(&meta.id) {
        let _ = std::fs::remove_dir_all(&tmp);
        return Err(format!("插件 ID 非法: {}", meta.id));
    }
    if !meta.gui.has_gui() {
        let _ = std::fs::remove_dir_all(&tmp);
        return Err("插件清单未声明任何 GUI（gui.views 或 gui.shell）".into());
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
        meta.gui.category()
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

/// 协议 host 语义
enum ProtocolKind {
    /// 具名视图（可嵌入任意 UI 插槽）
    View,
    /// 主壳整体接管
    Shell,
    /// 静态资源（跨场景共享）
    Assets,
}

impl ProtocolKind {
    fn from_host(host: &str) -> Option<Self> {
        match host {
            "view" => Some(Self::View),
            "shell" => Some(Self::Shell),
            "assets" => Some(Self::Assets),
            // v1 兼容
            "views" => Some(Self::View),
            "replacement" => Some(Self::Shell),
            _ => None,
        }
    }
}

/// 解析 furry:// 请求并返回响应
///
/// v2 语法（启动器内部任意嵌入场景，非公网网页）：
/// - `furry://view/{plugin_id}/{view_id}/{path}?query` → 具名视图
/// - `furry://shell/{plugin_id}/{path}`                → 主壳接管
/// - `furry://assets/{plugin_id}/{path}`               → 静态资源
/// v1 兼容：`furry://views/{pid}/{path}`（view_id=main）、
/// `furry://replacement/{pid}/{path}`（shell）
///
/// 所有 path 均防目录穿越；view 模式校验 view_id 已在 plugin.json 声明。
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

    let kind = match ProtocolKind::from_host(&host) {
        Some(k) => k,
        None => {
            return error_response(
                400,
                "furry:// 不支持的 host（支持 view / shell / assets）",
            )
        }
    };

    match resolve_plugin_resource(&plugins_dir, &kind, &path) {
        Ok(file_path) => match std::fs::read(&file_path) {
            Ok(bytes) => {
                let file_name = file_path
                    .file_name()
                    .and_then(|n| n.to_str())
                    .unwrap_or("index.html");
                let mime = mime_for(file_name);
                http::Response::builder()
                    .status(200)
                    .header("Content-Type", mime)
                    .header("Cache-Control", "no-cache")
                    .body(Cow::Owned(bytes))
                    .unwrap_or_else(|_| error_response(500, "构造响应失败"))
            }
            Err(_) => error_response(404, "文件不存在"),
        },
        Err(status) => {
            let msg = match status {
                400 => "furry:// 路径格式错误",
                403 => "访问被拒绝（非法 ID 或路径穿越）",
                404 => "视图不存在",
                _ => "请求失败",
            };
            error_response(status, msg)
        }
    }
}

/// 将协议请求解析为磁盘文件路径（纯函数，便于单元测试）
///
/// 返回 `Err(status)` 表示应返回的错误码：
/// - 400 路径格式错误；403 非法 ID/穿越；404 视图未声明或插件不存在
fn resolve_plugin_resource(
    plugins_dir: &Path,
    kind: &ProtocolKind,
    path: &str,
) -> std::result::Result<PathBuf, u16> {
    // 分层解析：先取 plugin_id，剩余部分按模式再分
    let mut segs = path.trim_start_matches('/').splitn(2, '/');
    let plugin_id = segs.next().unwrap_or("");
    let rest = segs.next().unwrap_or("");
    if !valid_plugin_id(plugin_id) {
        return Err(403u16);
    }

    // view 模式：rest 中第一段是 view_id（v1 兼容默认 main），其余是资源路径
    let (view_id, rel) = match kind {
        ProtocolKind::View => {
            let mut segs = rest.splitn(2, '/');
            let vid = segs.next().unwrap_or("");
            if !valid_plugin_id(vid) {
                return Err(403u16);
            }
            let rel = segs.next().unwrap_or("");
            if !view_declared(plugins_dir, plugin_id, vid) {
                return Err(404u16);
            }
            (Some(vid), rel)
        }
        ProtocolKind::Shell | ProtocolKind::Assets => (None, rest),
    };

    // 资源路径：相对 frontend/ 根
    let clean = clean_relative(rel).ok_or(403u16)?;
    // 根请求 → 使用声明入口（view 用视图入口，shell 用主壳入口）
    let file_name = if clean.is_empty() {
        match (kind, view_id) {
            (ProtocolKind::View, Some(vid)) => view_entry(plugins_dir, plugin_id, vid),
            (ProtocolKind::Shell, _) => shell_entry(plugins_dir, plugin_id),
            _ => "index.html".to_string(),
        }
    } else {
        clean
    };
    let frontend_base = plugins_dir.join(plugin_id).join("frontend");
    let file_path = safe_join(&frontend_base, &file_name).ok_or(403u16)?;

    // 目录请求 → 追加声明入口（view 模式），其他用 index.html
    if file_path.is_dir() {
        let default = match (kind, view_id) {
            (ProtocolKind::View, Some(vid)) => view_entry(plugins_dir, plugin_id, vid),
            (ProtocolKind::Shell, _) => shell_entry(plugins_dir, plugin_id),
            _ => "index.html".to_string(),
        };
        let file_path = safe_join(&file_path, &default).ok_or(403u16)?;
        if file_path.exists() {
            return Ok(file_path);
        }
    }
    Ok(file_path)
}

/// 校验视图 ID 已在插件清单中声明
fn view_declared(plugins_dir: &Path, plugin_id: &str, view_id: &str) -> bool {
    parse_plugin_meta(plugins_dir, plugin_id)
        .map(|meta| {
            meta.gui.views.iter().any(|v| v.id == view_id)
                || (view_id == "main" && meta.gui.shell.is_some())
        })
        .unwrap_or(false)
}

/// 获取视图声明入口（相对 frontend/）
fn view_entry(plugins_dir: &Path, plugin_id: &str, view_id: &str) -> String {
    parse_plugin_meta(plugins_dir, plugin_id)
        .and_then(|meta| {
            meta.gui
                .views
                .iter()
                .find(|v| v.id == view_id)
                .map(|v| v.entry.clone())
        })
        .unwrap_or_else(|| "index.html".into())
}

/// 获取主壳声明入口（相对 frontend/）
fn shell_entry(plugins_dir: &Path, plugin_id: &str) -> String {
    parse_plugin_meta(plugins_dir, plugin_id)
        .and_then(|meta| meta.gui.shell.map(|s| s.entry.clone()))
        .unwrap_or_else(|| "index.html".into())
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
                "api_version": 2,
                "gui": { "views": [{ "id": "main", "title": "示例视图", "entry": "index.html", "slots": ["page"] }] }
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
        assert_eq!(info.slots, vec!["page"]);

        // 不兼容版本
        fs::write(
            plugin.join("plugin.json"),
            r#"{
                "id": "demo",
                "name": "示例",
                "version": "0.1.0",
                "api_version": 99,
                "gui": { "views": [{ "id": "main", "title": "示例视图", "entry": "index.html", "slots": ["page"] }] }
            }"#,
        )
        .unwrap();
        let infos = scan_gui_plugins(&dir);
        assert!(!infos[0].compatible);
        assert!(infos[0].reason.is_some());

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn v1清单_归一化为views与shell() {
        // v1 type=view → views[main] + slot page
        let mut meta: GuiPluginMeta = serde_json::from_str(
            r#"{
                "id": "a", "name": "A", "version": "1.0.0", "api_version": 2,
                "gui": { "type": "view", "title": "视图A", "entry": "index.html" }
            }"#,
        )
        .unwrap();
        meta.gui.normalize();
        assert_eq!(meta.gui.views.len(), 1);
        assert_eq!(meta.gui.views[0].id, "main");
        assert_eq!(meta.gui.views[0].slots, vec!["page"]);
        assert_eq!(meta.gui.category(), "view");
        assert!(meta.gui.has_gui());

        // v1 type=replacement → shell
        let mut meta: GuiPluginMeta = serde_json::from_str(
            r#"{
                "id": "b", "name": "B", "version": "1.0.0", "api_version": 2,
                "gui": { "type": "replacement", "entry": "shell.html" }
            }"#,
        )
        .unwrap();
        meta.gui.normalize();
        assert!(meta.gui.shell.is_some());
        assert_eq!(meta.gui.shell.as_ref().unwrap().entry, "shell.html");
        assert_eq!(meta.gui.category(), "shell");
        assert!(meta.gui.has_gui());

        // 无 GUI → 拒绝安装
        let mut meta: GuiPluginMeta = serde_json::from_str(
            r#"{"id": "c", "name": "C", "version": "1.0.0", "gui": {}}"#,
        )
        .unwrap();
        meta.gui.normalize();
        assert!(!meta.gui.has_gui());
    }

    #[test]
    fn v2清单_多视图与主壳() {
        let mut meta: GuiPluginMeta = serde_json::from_str(
            r#"{
                "id": "c", "name": "C", "version": "1.0.0", "api_version": 2,
                "gui": {
                    "views": [
                        { "id": "main", "title": "主页", "entry": "index.html", "slots": ["page", "drawer"] },
                        { "id": "widget", "title": "微件", "entry": "widget.html", "slots": ["home"] }
                    ],
                    "shell": { "title": "壳", "entry": "shell.html" }
                }
            }"#,
        )
        .unwrap();
        meta.gui.normalize();
        assert_eq!(meta.gui.views.len(), 2);
        assert_eq!(meta.gui.category(), "mixed");

        let info = GuiPluginInfo::from_meta(&meta, true);
        assert_eq!(info.views.len(), 2);
        assert!(info.slots.contains(&"home".to_string()));
        assert!(info.shell.is_some());
        assert!(info.compatible);
        assert_eq!(info.gui_type, "mixed");
    }

    #[test]
    fn 协议v2_资源解析各模式() {
        let dir = std::env::temp_dir().join("furry-proto-v2");
        let _ = fs::remove_dir_all(&dir);
        let plugin = dir.join("demo");
        fs::create_dir_all(plugin.join("frontend/sub")).unwrap();
        fs::write(
            plugin.join("plugin.json"),
            r#"{
                "id": "demo", "name": "示例", "version": "1.0.0", "api_version": 2,
                "gui": {
                    "views": [{ "id": "main", "title": "主页", "entry": "index.html", "slots": ["page"] }],
                    "shell": { "title": "壳", "entry": "shell.html" }
                }
            }"#,
        )
        .unwrap();
        fs::write(plugin.join("frontend/index.html"), "<h1>home</h1>").unwrap();
        fs::write(plugin.join("frontend/shell.html"), "<h1>shell</h1>").unwrap();
        fs::write(plugin.join("frontend/sub/app.js"), "console.log(1)").unwrap();

        let base = plugin.join("frontend");

        // view：声明校验 + 资源解析
        assert_eq!(
            resolve_plugin_resource(&dir, &ProtocolKind::View, "/demo/main/index.html").unwrap(),
            base.join("index.html")
        );
        // view：根请求 → 视图声明入口
        assert_eq!(
            resolve_plugin_resource(&dir, &ProtocolKind::View, "/demo/main/").unwrap(),
            base.join("index.html")
        );
        // view：未声明视图 → 404
        assert_eq!(
            resolve_plugin_resource(&dir, &ProtocolKind::View, "/demo/nope/index.html").unwrap_err(),
            404
        );
        // shell：根请求 → 主壳声明入口
        assert_eq!(
            resolve_plugin_resource(&dir, &ProtocolKind::Shell, "/demo/").unwrap(),
            base.join("shell.html")
        );
        // shell：显式资源
        assert_eq!(
            resolve_plugin_resource(&dir, &ProtocolKind::Shell, "/demo/index.html").unwrap(),
            base.join("index.html")
        );
        // assets：任意资源
        assert_eq!(
            resolve_plugin_resource(&dir, &ProtocolKind::Assets, "/demo/sub/app.js").unwrap(),
            base.join("sub/app.js")
        );
        // assets：根请求 → index.html
        assert_eq!(
            resolve_plugin_resource(&dir, &ProtocolKind::Assets, "/demo/").unwrap(),
            base.join("index.html")
        );
        // 穿越拒绝
        assert_eq!(
            resolve_plugin_resource(&dir, &ProtocolKind::Assets, "/demo/../secret").unwrap_err(),
            403
        );
        // 非法插件 ID
        assert_eq!(
            resolve_plugin_resource(&dir, &ProtocolKind::Assets, "/../secret").unwrap_err(),
            403
        );
        // v1 兼容：views/main
        assert_eq!(
            resolve_plugin_resource(
                &dir,
                &ProtocolKind::from_host("views").unwrap(),
                "/demo/main/index.html"
            )
            .unwrap(),
            base.join("index.html")
        );
        // 未知 host 拒绝
        assert!(ProtocolKind::from_host("web").is_none());

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
                    r#"{{"id":"{id}","name":"测试插件","version":"1.0.0","api_version":2,"gui":{{"views":[{{"id":"main","title":"测试","entry":"index.html","slots":["page"]}}]}}}}"#
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
