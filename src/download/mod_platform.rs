//! Mod 下载平台：Modrinth（主）+ CurseForge（无 key 镜像）
//!
//! - Modrinth：官方开放 API（需 UA，无需 key）
//! - CurseForge：参考 PCL2，使用官网公开接口（无需 API key，需浏览器 UA，
//!   接口不稳定时 Modrinth 作为主来源兜底）

use crate::error::{LauncherError, Result};
use crate::download::mod_cache;
use percent_encoding::{utf8_percent_encode, AsciiSet, CONTROLS};
use reqwest::header::{HeaderMap, HeaderValue, USER_AGENT};
use serde::{Deserialize, Serialize};
use std::sync::{Arc, LazyLock};
use std::time::Duration;
use tokio::sync::Semaphore;

/// 请求 UA（Modrinth 强制要求携带）
const UA: &str = "StarcloudLauncher/0.2 (mod-download)";

/// URL 编码（保留安全字符）
const ENCODE_SET: &AsciiSet = &CONTROLS
    .add(b' ')
    .add(b'"')
    .add(b'#')
    .add(b'%')
    .add(b'<')
    .add(b'>')
    .add(b'?')
    .add(b'`')
    .add(b'{')
    .add(b'}')
    .add(b'[')
    .add(b']');

fn urlenc(s: &str) -> String {
    utf8_percent_encode(s, ENCODE_SET).to_string()
}

/// Mod 搜索结果
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ModSearchHit {
    /// "modrinth" | "curseforge"
    pub platform: String,
    /// 项目 ID（Modrinth project_id / CurseForge 数字 ID）
    pub id: String,
    /// 项目 slug（拼官网地址用）
    pub slug: String,
    pub title: String,
    /// 中文标题（优先内置词典 → MCIM → MC 百科 → 机器翻译；无合适译名时为 None）
    pub title_zh: Option<String>,
    pub description: String,
    /// 中文描述（优先 MCIM 翻译；无合适翻译时为 None）
    pub description_zh: Option<String>,
    pub author: String,
    pub downloads: u64,
    pub icon_url: Option<String>,
}

/// Mod 可下载文件（一个版本）
#[derive(Debug, Clone, Serialize)]
pub struct ModFile {
    /// 文件 ID（Modrinth version id / CurseForge file id）
    pub id: String,
    /// 版本名
    pub version: String,
    pub game_versions: Vec<String>,
    pub loaders: Vec<String>,
    pub file_name: String,
    pub size: u64,
    pub url: String,
}

fn build_client() -> Result<reqwest::Client> {
    let mut headers = HeaderMap::new();
    headers.insert(USER_AGENT, HeaderValue::from_static(UA));
    Ok(reqwest::Client::builder()
        .default_headers(headers)
        .gzip(true)
        .brotli(true)
        .deflate(true)
        .connect_timeout(Duration::from_secs(15))
        .timeout(Duration::from_secs(60))
        .build()?)
}

/// 全局复用的平台客户端：连接池跨请求共享，避免每次搜索/翻页重新 TCP/TLS 握手
/// （国内访问 Modrinth/CurseForge 时握手开销尤为明显）。翻译用的短超时客户端独立构建。
static MOD_CLIENT: LazyLock<Option<reqwest::Client>> = LazyLock::new(|| build_client().ok());

fn shared_client() -> Result<reqwest::Client> {
    if let Some(c) = MOD_CLIENT.clone() {
        return Ok(c);
    }
    build_client()
}

/// 短超时客户端（翻译用）：Google gtx 在国内常不可达，
/// 若用 60s 全局超时会拖死整个搜索流程，这里收紧到 2s 快速失败转 MyMemory。
fn build_quick_client() -> Result<reqwest::Client> {
    let mut headers = HeaderMap::new();
    headers.insert(USER_AGENT, HeaderValue::from_static(UA));
    Ok(reqwest::Client::builder()
        .default_headers(headers)
        .connect_timeout(Duration::from_secs(2))
        .timeout(Duration::from_secs(2))
        .build()?)
}

/// 翻译缓存：持久化到磁盘，启动器重启后仍可用
/// 写操作通过 mod_cache 模块自动同步到磁盘

fn cache_get(key: &str) -> Option<String> {
    mod_cache::translate_cache_get(key)
}

fn cache_put(key: String, val: String) {
    mod_cache::translate_cache_put(key, val);
}

// ---------------------------------------------------------------------------
// Modrinth
// ---------------------------------------------------------------------------

#[derive(Debug, Deserialize)]
struct ModrinthSearchResponse {
    hits: Vec<ModrinthHit>,
    /// 总命中数（用于"加载更多"分页判断）
    #[serde(default)]
    total_hits: u32,
}

#[derive(Debug, Deserialize)]
struct ModrinthHit {
    project_id: String,
    #[serde(default)]
    slug: String,
    title: String,
    description: String,
    author: String,
    downloads: u64,
    #[serde(default)]
    icon_url: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ModrinthVersion {
    id: String,
    version_number: String,
    #[serde(default)]
    game_versions: Vec<String>,
    #[serde(default)]
    loaders: Vec<String>,
    #[serde(default)]
    files: Vec<ModrinthFile>,
}

#[derive(Debug, Deserialize)]
struct ModrinthFile {
    url: String,
    filename: String,
    size: u64,
}

/// Modrinth 搜索结果（含总命中数，供分页/加载更多使用）
pub struct ModrinthSearchResult {
    pub hits: Vec<ModSearchHit>,
    pub total_hits: u32,
}

/// 搜索 Modrinth Mod（支持按 MC 版本、加载器、资源分类与类型标签过滤）
///
/// `category` 取值：`mod` / `modpack`（整合包）/ `datapack`（数据包）/
/// `shader`（光影包）/ `resourcepack`（材质包），为空表示不限。
/// `categories` 为逗号分隔的 Modrinth 类型标签（如 `magic,adventure`），
/// 多个标签之间为 OR 关系（同一 facet），为空表示不限。
/// 关键词为空时按下载量排序，返回热门/推荐内容。
pub async fn search_modrinth(
    query: &str,
    mc_version: &str,
    loader: &str,
    category: &str,
    categories: &str,
    limit: u32,
    offset: u32,
) -> Result<Vec<ModSearchHit>> {
    Ok(search_modrinth_total(query, mc_version, loader, category, categories, limit, offset)
        .await?
        .hits)
}

/// 与 [`search_modrinth`] 相同，额外返回总命中数
pub async fn search_modrinth_total(
    query: &str,
    mc_version: &str,
    loader: &str,
    category: &str,
    categories: &str,
    limit: u32,
    offset: u32,
) -> Result<ModrinthSearchResult> {
    let mut facets: Vec<Vec<String>> = Vec::new();
    if !category.is_empty() {
        facets.push(vec![format!("project_type:{}", category)]);
    }
    // 类型标签：Modrinth facets 同一 facet 内为 OR，多个标签取并集
    let tags: Vec<String> = categories
        .split(',')
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .map(|t| format!("categories:{}", t))
        .collect();
    if !tags.is_empty() {
        facets.push(tags);
    }
    if !mc_version.is_empty() {
        facets.push(vec![format!("versions:{}", mc_version)]);
    }
    if !loader.is_empty() {
        facets.push(vec![format!("categories:{}", loader)]);
    }
    let facets_json = serde_json::to_string(&facets)?;
    // 空关键词：按下载量排序，作为"热门推荐"展示
    let index = if query.trim().is_empty() {
        "&index=downloads"
    } else {
        ""
    };
    let url = format!(
        "https://api.modrinth.com/v2/search?query={}&facets={}&limit={}&offset={}{}",
        urlenc(query),
        urlenc(&facets_json),
        limit,
        offset,
        index
    );

    let client = shared_client()?;
    let resp = client.get(&url).send().await?;
    if !resp.status().is_success() {
        return Err(LauncherError::DownloadError(format!(
            "Modrinth 搜索失败: HTTP {}",
            resp.status()
        )));
    }
    let data: ModrinthSearchResponse = resp.json().await?;
    Ok(ModrinthSearchResult {
        hits: data
            .hits
            .into_iter()
            .map(|h| ModSearchHit {
                platform: "modrinth".into(),
                id: h.project_id,
                slug: h.slug,
                title: h.title,
                title_zh: None,
                description: h.description,
                description_zh: None,
                author: h.author,
                downloads: h.downloads,
                icon_url: h.icon_url,
            })
            .collect(),
        total_hits: data.total_hits,
    })
}

/// 列出 Modrinth 项目可用版本文件
pub async fn list_modrinth_versions(
    project_id: &str,
    mc_version: &str,
    loader: &str,
) -> Result<Vec<ModFile>> {
    let mut url = format!("https://api.modrinth.com/v2/project/{}/version", project_id);
    let mut parts: Vec<String> = Vec::new();
    if !mc_version.is_empty() {
        let arr = serde_json::to_string(&vec![mc_version])?;
        parts.push(format!("game_versions={}", urlenc(&arr)));
    }
    if !loader.is_empty() {
        let arr = serde_json::to_string(&vec![loader])?;
        parts.push(format!("loaders={}", urlenc(&arr)));
    }
    if !parts.is_empty() {
        url = format!("{}?{}", url, parts.join("&"));
    }

    let client = shared_client()?;
    let resp = client.get(&url).send().await?;
    if !resp.status().is_success() {
        return Err(LauncherError::DownloadError(format!(
            "Modrinth 版本查询失败: HTTP {}",
            resp.status()
        )));
    }
    let versions: Vec<ModrinthVersion> = resp.json().await?;
    Ok(versions
        .into_iter()
        .map(|v| {
            let f = v.files.into_iter().next();
            ModFile {
                id: v.id,
                version: v.version_number,
                game_versions: v.game_versions,
                loaders: v.loaders,
                file_name: f.as_ref().map(|f| f.filename.clone()).unwrap_or_default(),
                size: f.as_ref().map(|f| f.size).unwrap_or(0),
                url: f.as_ref().map(|f| f.url.clone()).unwrap_or_default(),
            }
        })
        .collect())
}

// ---------------------------------------------------------------------------
// CurseForge（无 key 镜像）
// ---------------------------------------------------------------------------

#[derive(Debug, Deserialize)]
struct CfSearchResponse {
    data: Vec<CfSearchItem>,
}

#[derive(Debug, Deserialize)]
struct CfSearchItem {
    id: u64,
    name: String,
    summary: String,
    #[serde(default)]
    slug: String,
    #[serde(default)]
    download_count: u64,
    #[serde(default)]
    authors: Vec<CfAuthor>,
    #[serde(default)]
    logo: Option<CfLogo>,
}

#[derive(Debug, Deserialize)]
struct CfAuthor {
    name: String,
}

#[derive(Debug, Deserialize)]
struct CfLogo {
    thumbnail_url: Option<String>,
    url: Option<String>,
}

#[derive(Debug, Deserialize)]
struct CfFilesResponse {
    data: Vec<CfFile>,
}

#[derive(Debug, Deserialize)]
struct CfFile {
    id: u64,
    display_name: String,
    file_name: String,
    file_length: u64,
    #[serde(default)]
    download_url: Option<String>,
    #[serde(default)]
    game_versions: Vec<String>,
    #[serde(default)]
    mod_loader_type: Option<u32>,
}

/// 加载器 → CurseForge modLoaderType
fn cf_loader_type(loader: &str) -> Option<u32> {
    match loader {
        "forge" => Some(1),
        "fabric" => Some(4),
        "quilt" => Some(5),
        "neoforge" => Some(6),
        _ => None,
    }
}

/// CurseForge CDN 直链（downloadUrl 为空时兜底）
fn cf_media_url(file_id: u64, file_name: &str) -> String {
    format!(
        "https://media.forgecdn.net/files/{}/{}/{}",
        file_id / 1000,
        file_id % 1000,
        file_name
    )
}

/// 资源分类 → CurseForge classId（mod=6, modpack=4471, resourcepack=6946,
/// shaderpack=6552, datapack=6756），未知分类回退到 mod
fn cf_class_id(category: &str) -> u32 {
    match category {
        "modpack" => 4471,
        "resourcepack" => 6946,
        "shader" => 6552,
        "datapack" => 6756,
        _ => 6,
    }
}

/// 搜索 CurseForge Mod（官网公开接口，无需 key）
pub async fn search_curseforge(
    query: &str,
    _mc_version: &str,
    _loader: &str,
    category: &str,
    limit: u32,
    offset: u32,
) -> Result<Vec<ModSearchHit>> {
    let url = format!(
        "https://www.curseforge.com/api/v1/mods/search?gameId=432&classId={}&index={}&pageSize={}&searchFilter={}",
        cf_class_id(category),
        offset,
        limit,
        urlenc(query)
    );

    let client = shared_client()?;
    let resp = client.get(&url).send().await?;
    if !resp.status().is_success() {
        return Err(LauncherError::DownloadError(format!(
            "CurseForge 搜索失败: HTTP {}",
            resp.status()
        )));
    }
    let data: CfSearchResponse = resp.json().await?;
    Ok(data
        .data
        .into_iter()
        .map(|m| ModSearchHit {
            platform: "curseforge".into(),
            id: m.id.to_string(),
            slug: m.slug,
            title: m.name,
            title_zh: None,
            description: m.summary,
            description_zh: None,
            author: m
                .authors
                .into_iter()
                .map(|a| a.name)
                .collect::<Vec<_>>()
                .join(", "),
            downloads: m.download_count,
            icon_url: m.logo.and_then(|l| l.thumbnail_url.or(l.url)),
        })
        .collect())
}

/// 列出 CurseForge 项目的可用文件
pub async fn list_curseforge_files(
    mod_id: &str,
    mc_version: &str,
    loader: &str,
) -> Result<Vec<ModFile>> {
    let id: u64 = mod_id
        .parse()
        .map_err(|_| LauncherError::Other("无效的 CurseForge 项目 ID".into()))?;
    let mut url = format!("https://www.curseforge.com/api/v1/mods/{}/files?pageSize=50", id);
    let mut parts: Vec<String> = Vec::new();
    if !mc_version.is_empty() {
        parts.push(format!("gameVersion={}", urlenc(mc_version)));
    }
    if let Some(lt) = cf_loader_type(loader) {
        parts.push(format!("modLoaderType={}", lt));
    }
    if !parts.is_empty() {
        url = format!("{}&{}", url, parts.join("&"));
    }

    let client = shared_client()?;
    let resp = client.get(&url).send().await?;
    if !resp.status().is_success() {
        return Err(LauncherError::DownloadError(format!(
            "CurseForge 文件查询失败: HTTP {}",
            resp.status()
        )));
    }
    let data: CfFilesResponse = resp.json().await?;
    Ok(data
        .data
        .into_iter()
        .map(|f| {
            let download = f
                .download_url
                .clone()
                .unwrap_or_else(|| cf_media_url(f.id, &f.file_name));
            ModFile {
                id: f.id.to_string(),
                version: f.display_name,
                game_versions: f.game_versions,
                loaders: f
                    .mod_loader_type
                    .map(|t| t.to_string())
                    .into_iter()
                    .collect(),
                file_name: f.file_name,
                size: f.file_length,
                url: download,
            }
        })
        .collect())
}

// ---------------------------------------------------------------------------
// 翻译（中文搜索 → 英文；结果标题 → 中文）
// ---------------------------------------------------------------------------

/// 内置译名词典：英文模组名 → 社区通用中文名
/// （参考 PCL2 内置词库与 CFPA 译名规范；优先于机器翻译，热门模组零网络开销即时命中）
static MOD_NAME_ZH: &[(&str, &str)] = &[
    // 性能优化
    ("Sodium", "钠"),
    ("Sodium Extra", "钠扩展"),
    ("Iris Shaders", "Iris 光影"),
    ("Iris", "Iris 光影"),
    ("Lithium", "锂"),
    ("Phosphor", "磷"),
    ("Starlight", "星光"),
    ("FerriteCore", "铁氧体核心"),
    ("Krypton", "氪"),
    ("ModernFix", "现代修复"),
    ("EntityCulling", "实体渲染剔除"),
    ("MemoryLeakFix", "内存泄漏修复"),
    ("OptiFine", "高清修复"),
    ("C2ME", "区块生成优化"),
    // 物品 / 配方
    ("Just Enough Items", "JEI 物品管理器"),
    ("JEI", "JEI 物品管理器"),
    ("Roughly Enough Items", "REI 物品管理器"),
    ("EMI", "EMI 物品管理器"),
    ("AppleSkin", "苹果皮"),
    ("Inventory Profiles Next", "物品栏管理"),
    ("Mouse Tweaks", "鼠标手势"),
    ("Controlling", "按键控制"),
    ("Just Enough Resources", "资源查看"),
    ("Toast Control", "提示框控制"),
    // 地图
    ("Journey Map", "旅行地图"),
    ("Xaero's Minimap", "Xaero 小地图"),
    ("Xaero's World Map", "Xaero 世界地图"),
    ("Map Atlases", "地图册"),
    ("Antique Atlas", "古式地图集"),
    // 世界生成
    ("Biomes O' Plenty", "更多生物群系"),
    ("Terralith", "地形扩展"),
    ("TerraForged", "地形锻造"),
    ("Oh The Biomes You'll Go", "你将去的生物群系"),
    ("Amplified Nether", "加强版下界"),
    ("YUNG's Better Mineshafts", "YUNG 更好的废弃矿井"),
    ("YUNG's Better Dungeons", "YUNG 更好的地牢"),
    ("YUNG's Better Strongholds", "YUNG 更好的要塞"),
    ("YUNG's Better Ocean Monuments", "YUNG 更好的海底神殿"),
    ("Repurposed Structures", "重设结构"),
    ("When Dungeons Arise", "当彼地牢崛起"),
    // 科技
    ("Create", "机械动力"),
    ("Create: Crafts & Additions", "机械动力：工匠与附加"),
    ("Mekanism", "通用机械"),
    ("Thermal Expansion", "热力膨胀"),
    ("Thermal Foundation", "热力基础"),
    ("Immersive Engineering", "沉浸工程"),
    ("Industrial Foregoing", "工业先锋"),
    ("Ender IO", "末影接口"),
    ("Applied Energistics 2", "应用能源2"),
    ("Refined Storage", "精致存储"),
    ("RFTools", "RF工具"),
    ("Integrated Dynamics", "集成动力"),
    ("Draconic Evolution", "龙之研究"),
    ("Mystical Agriculture", "神秘农业"),
    ("Actually Additions", "实用拓展"),
    ("Extra Utilities", "更多实用设备"),
    ("Simply Jetpacks", "简易喷气背包"),
    ("Iron Chests", "铁箱子"),
    ("Storage Drawers", "储物抽屉"),
    ("Sophisticated Backpacks", "精妙背包"),
    ("Traveler's Backpack", "旅行者背包"),
    ("Pipez", "管道"),
    // 魔法 / 冒险
    ("Botania", "植物魔法"),
    ("Blood Magic", "血魔法"),
    ("Astral Sorcery", "星辉魔法"),
    ("Thaumcraft", "神秘时代"),
    ("Ars Nouveau", "新生魔艺"),
    ("Occultism", "神秘学"),
    ("Electroblob's Wizardry", "巫术学"),
    ("Ice and Fire", "冰与火之歌"),
    ("ProjectE", "等价交换重置"),
    ("Tinkers' Construct", "匠魂"),
    ("Silent Gear", "寂静装备"),
    ("Spartan Weaponry", "斯巴达武器"),
    ("Epic Fight", "史诗战斗"),
    ("ParCool", "帕克的大冒险"),
    ("Lucky Block", "幸运方块"),
    ("Champions", "冠军"),
    ("Lycanites Mobs", "恐怖生物"),
    ("Mowzie's Mobs", "Mowzie 的生物"),
    ("Alex's Mobs", "Alex 的生物"),
    ("The Twilight Forest", "暮色森林"),
    ("Aether", "天境"),
    ("DivineRPG", "神圣RPG"),
    ("Advent of Ascension", "虚无世界"),
    // 辅助 / 便捷
    ("Mod Menu", "Mod 菜单"),
    ("Fabric Language Kotlin", "Fabric Kotlin 语言支持"),
    ("Cloth Config", "Cloth 配置"),
    ("The One Probe", "万用探测器"),
    ("HWYLA", "高亮显示"),
    ("WTHIT", "高亮显示"),
    ("Jade", "玉"),
    ("Neat", "清爽血条"),
    ("MiniHUD", "迷你HUD"),
    ("Litematica", "轻松投影"),
    ("Carpet", "地毯"),
    ("Carpet Extra", "地毯附加"),
    ("Replay Mod", "回放模组"),
    ("Waystones", "路标"),
    ("Gravestone Mod", "墓碑"),
    ("Corpse", "尸体"),
    ("Curios API", "饰品栏"),
    ("Trinkets", "饰品"),
    ("CraftTweaker", "合成修改器"),
    ("Custom NPCs", "自定义NPC"),
    ("Citadel", "城堡"),
    // 农业 / 食物
    ("Farmer's Delight", "农夫乐事"),
    ("Pam's HarvestCraft 2", "潘马斯的农场物语2"),
    ("Serene Seasons", "宁静的季节"),
    ("Simple Farming", "简易农业"),
    ("Croptopia", "作物盛景"),
    // 光影（资源包类）
    ("Complementary Shaders", "互补光影"),
    ("Complementary Reimagined", "互补·重塑光影"),
    ("BSL Shaders", "BSL 光影"),
    ("SEUS", "SEUS 光影"),
    ("Sildur's Vibrant Shaders", "Sildur 光影"),
    ("Chocapic13's Shaders", "Chocapic13 光影"),
    ("Rethinking Voxels", "重思体素光影"),
    ("MakeUp - Ultra Fast Shaders", "MakeUp 极速光影"),
    // 视角 / 杂项
    ("Better Third Person", "更好的第三人称"),
    ("First Person Model", "第一人称模型"),
    ("Shoulder Surfing Reloaded", "肩后视角"),
    ("Simple Voice Chat", "简易语音聊天"),
    ("Emotecraft", "表情动作"),
    ("Camera Utils", "相机工具"),
    ("Zoomify", "平滑缩放"),
    ("Bobby", "Bobby 视野拓展"),
    ("BetterF3", "更好F3"),
];

/// 常用中文搜索词 → 英文模组名（搜索前精确命中，避免机翻歧义）
static QUERY_ZH_TO_EN: &[(&str, &str)] = &[
    ("旅行地图", "Journey Map"),
    ("小地图", "Xaero's Minimap"),
    ("钠", "Sodium"),
    ("光影", "Shaders"),
    ("高清修复", "OptiFine"),
    ("物品管理器", "Just Enough Items"),
    ("机械动力", "Create"),
    ("通用机械", "Mekanism"),
    ("植物魔法", "Botania"),
    ("血魔法", "Blood Magic"),
    ("星辉魔法", "Astral Sorcery"),
    ("神秘时代", "Thaumcraft"),
    ("暮色森林", "The Twilight Forest"),
    ("天境", "Aether"),
    ("应用能源", "Applied Energistics 2"),
    ("精致存储", "Refined Storage"),
    ("匠魂", "Tinkers' Construct"),
    ("冰与火", "Ice and Fire"),
    ("农夫乐事", "Farmer's Delight"),
    ("更多生物群系", "Biomes O' Plenty"),
    ("路标", "Waystones"),
    ("苹果皮", "AppleSkin"),
    ("旅行者背包", "Traveler's Backpack"),
    ("幸运方块", "Lucky Block"),
    ("回放", "Replay Mod"),
    ("语音", "Simple Voice Chat"),
    ("表情", "Emotecraft"),
    ("铁箱子", "Iron Chests"),
    ("储物抽屉", "Storage Drawers"),
];

/// 词典查英文 → 中文（大小写不敏感）
fn dict_en_to_zh(title: &str) -> Option<&'static str> {
    let t = title.trim();
    MOD_NAME_ZH
        .iter()
        .find(|(en, _)| t.eq_ignore_ascii_case(en))
        .map(|(_, zh)| *zh)
}

/// 词典查中文 → 英文（搜索词精确匹配）
fn dict_zh_to_en(query: &str) -> Option<&'static str> {
    let q = query.trim();
    QUERY_ZH_TO_EN
        .iter()
        .find(|(zh, _)| *zh == q)
        .map(|(_, en)| *en)
}

/// 是否含 CJK 汉字
fn contains_cjk(s: &str) -> bool {
    s.chars().any(|c| (0x4E00..=0x9FFF).contains(&(c as u32)))
}

/// 单条翻译：Google gtx → MyMemory → 原文兜底（带会话级缓存）
async fn translate_one(text: &str, to: &str) -> String {
    let key = format!("{}|{}", to, text);
    if let Some(hit) = cache_get(&key) {
        return hit;
    }
    let out = match google_gtx(text, to).await {
        Ok(s) if !s.trim().is_empty() && !s.eq_ignore_ascii_case(text) => s,
        _ => match mymemory_translate(text, to).await {
            Ok(s) if !s.trim().is_empty() => s,
            _ => text.to_string(),
        },
    };
    cache_put(key, out.clone());
    out
}

/// Google 免费翻译接口（client=gtx；短超时，国内不可达时快速失败）
async fn google_gtx(text: &str, to: &str) -> Result<String> {
    let from = if contains_cjk(text) { "zh-CN" } else { "en" };
    let url = format!(
        "https://translate.googleapis.com/translate_a/single?client=gtx&sl={}&tl={}&dt=t&q={}",
        from,
        to,
        urlenc(text)
    );
    let resp = build_quick_client()?
        .get(&url)
        .send()
        .await?
        .json::<serde_json::Value>()
        .await?;
    let mut out = String::new();
    if let Some(rows) = resp
        .as_array()
        .and_then(|a| a.first())
        .and_then(|v| v.as_array())
    {
        for row in rows {
            if let Some(t) = row.get(0).and_then(|v| v.as_str()) {
                out.push_str(t);
            }
        }
    }
    if out.trim().is_empty() {
        Err(LauncherError::DownloadError("翻译结果为空".into()))
    } else {
        Ok(out)
    }
}

#[derive(Debug, Deserialize)]
struct MyMemoryResp {
    #[serde(rename = "responseStatus")]
    response_status: u32,
    #[serde(rename = "responseData")]
    response_data: MyMemoryData,
}

#[derive(Debug, Deserialize)]
struct MyMemoryData {
    #[serde(rename = "translatedText", default)]
    translated_text: String,
}

/// MyMemory 免费翻译接口（Google 不可用时的兜底）
async fn mymemory_translate(text: &str, to: &str) -> Result<String> {
    let from = if contains_cjk(text) { "zh-CN" } else { "en" };
    let url = format!(
        "https://api.mymemory.translated.net/get?q={}&langpair={}|{}",
        urlenc(text),
        from,
        to
    );
    let resp = build_quick_client()?
        .get(&url)
        .send()
        .await?
        .json::<MyMemoryResp>()
        .await?;
    if resp.response_status == 200 && !resp.response_data.translated_text.is_empty() {
        Ok(resp.response_data.translated_text)
    } else {
        Err(LauncherError::DownloadError("MyMemory 翻译失败".into()))
    }
}

/// 批量翻译（并发 6；单条失败回退原文）
#[allow(dead_code)]
async fn translate_batch(texts: &[String], to: &str) -> Vec<String> {
    if texts.is_empty() {
        return Vec::new();
    }
    let sem = Arc::new(Semaphore::new(6));
    let mut handles = Vec::with_capacity(texts.len());
    for text in texts {
        let text = text.clone();
        let to = to.to_string();
        let sem = sem.clone();
        handles.push(tokio::spawn(async move {
            let _permit = sem.acquire().await.ok();
            translate_one(&text, &to).await
        }));
    }
    let mut out = Vec::with_capacity(handles.len());
    for h in handles {
        out.push(h.await.unwrap_or_default());
    }
    out
}

/// 中文关键词 → 英文（搜索前翻译；不含中文时原样返回）
///
/// 优先级：内置词典精确命中（如「旅行地图」→ Journey Map）→ 机器翻译。
pub async fn translate_query_zh_to_en(query: &str) -> String {
    let q = query.trim();
    if q.is_empty() || !contains_cjk(q) {
        return q.to_string();
    }
    if let Some(en) = dict_zh_to_en(q) {
        return en.to_string();
    }
    translate_one(q, "en").await
}

/// 标题是否需要机器翻译：含英文字母、含空格（多词短语）、且不含中文
/// 单个单词通常是专有名词（Sodium / Iris 等），机器翻译易出错，跳过。
/// （词典 / MC 百科不受此限制，可覆盖单词专名）
fn should_translate(s: &str) -> bool {
    !contains_cjk(s)
        && s.chars().any(|c| c.is_ascii_alphabetic())
        && s.chars().any(|c| c.is_ascii_whitespace())
}

/// 标题是否值得翻译：含英文字母且不含中文（词典/百科可命中单词专名）
fn needs_translate(s: &str) -> bool {
    !contains_cjk(s) && s.chars().any(|c| c.is_ascii_alphabetic())
}

/// 单个标题翻译为中文（供搜索结果异步补译）
///
/// 优先级：内置词典（零网络，热门模组即时）→ MCIM 社区译名 →
/// MC 百科社区译名 → 机器翻译兜底（仅多词标题，单词专名保持原文）。
/// 结果带会话级缓存。
pub async fn translate_title_zh(title: &str) -> Option<String> {
    if !needs_translate(title) {
        return None;
    }
    let key = format!("tt|{}", title);
    if let Some(hit) = cache_get(&key) {
        return if hit.is_empty() { None } else { Some(hit) };
    }
    let zh = if let Some(zh) = dict_en_to_zh(title) {
        Some(zh.to_string())
    } else if let Some(zh) = mcim_lookup_zh(title).await {
        // MCIM 回显原文视为未收录
        if zh.eq_ignore_ascii_case(title) {
            None
        } else {
            Some(zh)
        }
    } else if let Some(zh) = mcmod_lookup_zh(title).await {
        // 百科回显原文（大小写无关）视为未收录，避免把英文当作中文译名
        if zh.eq_ignore_ascii_case(title) {
            None
        } else {
            Some(zh)
        }
    } else if should_translate(title) {
        let t = translate_one(title, "zh-CN").await;
        let t = t.trim();
        if !t.is_empty() && !t.eq_ignore_ascii_case(title) {
            Some(t.to_string())
        } else {
            None
        }
    } else {
        None
    };
    cache_put(key, zh.clone().unwrap_or_default());
    zh
}

/// 查询标题翻译的会话缓存（不触发网络请求）：
/// `Some(Some(zh))` = 缓存命中；`Some(None)` = 已缓存为未收录 / 无需翻译；
/// `None` = 未缓存，需调用 `translate_title_zh` 发起真实翻译。
pub fn translate_title_zh_cache_hit(title: &str) -> Option<Option<String>> {
    if !needs_translate(title) {
        return Some(None);
    }
    let key = format!("tt|{}", title);
    cache_get(&key).map(|hit| {
        if hit.is_empty() {
            None
        } else {
            Some(hit)
        }
    })
}

// ---------------------------------------------------------------------------
// MCIM（Minecraft Mod Information Mirror）翻译
// ---------------------------------------------------------------------------

/// MCIM API 根地址
const MCIM_BASE: &str = "https://mod.mcimirror.top";

/// MCIM 翻译 API 响应（Modrinth）
#[derive(Debug, Deserialize)]
struct McimTranslateModrinthResp {
    #[allow(dead_code)]
    project_id: String,
    translated: String,
    #[allow(dead_code)]
    original: String,
}

/// MCIM 翻译 API 响应（CurseForge）
#[derive(Debug, Deserialize)]
struct McimTranslateCurseforgeResp {
    #[serde(rename = "modId")]
    #[allow(dead_code)]
    mod_id: u64,
    translated: String,
    #[allow(dead_code)]
    original: String,
}

/// 通过 MCIM 翻译 API 获取 Modrinth 项目的中文描述
async fn mcim_translate_modrinth(project_id: &str) -> Option<String> {
    let url = format!(
        "{}/translate/modrinth?project_id={}",
        MCIM_BASE,
        urlenc(project_id)
    );
    let client = build_mcim_client().ok()?;
    let resp = client
        .post(&url)
        .send()
        .await
        .ok()?;
    if !resp.status().is_success() {
        return None;
    }
    let data: McimTranslateModrinthResp = resp.json().await.ok()?;
    let t = data.translated.trim().to_string();
    if t.is_empty() || t.eq_ignore_ascii_case(&data.original) {
        None
    } else {
        Some(t)
    }
}

/// 通过 MCIM 翻译 API 获取 CurseForge 项目的中文简介
async fn mcim_translate_curseforge(mod_id: u64) -> Option<String> {
    let url = format!("{}/translate/curseforge?modId={}", MCIM_BASE, mod_id);
    let client = build_mcim_client().ok()?;
    let resp = client
        .post(&url)
        .send()
        .await
        .ok()?;
    if !resp.status().is_success() {
        return None;
    }
    let data: McimTranslateCurseforgeResp = resp.json().await.ok()?;
    let t = data.translated.trim().to_string();
    if t.is_empty() || t.eq_ignore_ascii_case(&data.original) {
        None
    } else {
        Some(t)
    }
}

/// MCIM 搜索 Modrinth 结果（仅取 project_id）
#[derive(Debug, Deserialize)]
struct McimSearchResponse {
    hits: Vec<McimSearchHitItem>,
}

#[derive(Debug, Deserialize)]
struct McimSearchHitItem {
    project_id: String,
    title: String,
}

/// 通过 MCIM 搜索 Modrinth 获取项目 ID（按标题匹配）
async fn mcim_search_project(title: &str) -> Option<(String, String)> {
    let url = format!(
        "{}/modrinth/v2/search?query={}&limit=1",
        MCIM_BASE,
        urlenc(title)
    );
    let client = build_mcim_client().ok()?;
    let resp = client.get(&url).send().await.ok()?;
    if !resp.status().is_success() {
        return None;
    }
    let data: McimSearchResponse = resp.json().await.ok()?;
    data.hits
        .into_iter()
        .next()
        .map(|h| (h.project_id, h.title))
}

/// MCIM 客户端（短超时，国内 CDN 响应快）
fn build_mcim_client() -> Result<reqwest::Client> {
    let mut headers = HeaderMap::new();
    headers.insert(USER_AGENT, HeaderValue::from_static(UA));
    Ok(reqwest::Client::builder()
        .default_headers(headers)
        .connect_timeout(Duration::from_secs(5))
        .timeout(Duration::from_secs(8))
        .build()?)
}

/// 通过 MCIM 获取 Mod 中文名（搜索 Modrinth 镜像 + 翻译描述提取）
///
/// 策略：通过 MCIM Modrinth 镜像搜索项目 → 调用 MCIM 翻译 API 获取中文描述 →
/// 若描述较短（≤30 字符）则作为名称使用；否则返回 None 让 MC 百科兜底。
async fn mcim_lookup_zh(title: &str) -> Option<String> {
    let (project_id, _) = mcim_search_project(title).await?;
    let translated = mcim_translate_modrinth(&project_id).await?;
    // 仅当翻译结果较短时视为"名称"（≤30 字符），过长的描述交给 description_zh
    let t = translated.trim().to_string();
    if t.chars().count() <= 30 {
        Some(t)
    } else {
        None
    }
}

/// 通过 MCIM 获取 Mod 中文描述（公开接口）
///
/// `platform` 为 "modrinth" 时使用 project_id 查询翻译；
/// 为 "curseforge" 时 `id` 应为数字 modId。
pub async fn mcim_translate_description(platform: &str, id: &str) -> Option<String> {
    match platform {
        "curseforge" => {
            let mod_id: u64 = id.parse().ok()?;
            mcim_translate_curseforge(mod_id).await
        }
        _ => mcim_translate_modrinth(id).await,
    }
}

// ---------------------------------------------------------------------------
// MC 百科
// ---------------------------------------------------------------------------

/// MC 百科客户端：比翻译接口更宽松的超时（搜索页含正文，需 1-3s）
fn build_mcmod_client() -> Result<reqwest::Client> {
    let mut headers = HeaderMap::new();
    headers.insert(USER_AGENT, HeaderValue::from_static(UA));
    Ok(reqwest::Client::builder()
        .default_headers(headers)
        .connect_timeout(Duration::from_secs(5))
        .timeout(Duration::from_secs(8))
        .build()?)
}

/// MC 百科搜索结果（详情页 URL + 首页中文名）
struct McmodSearchHit {
    url: String,
    /// 结果列表首条的中文名（剥离 HTML 标签、去除 [XX] 前缀后）
    name: Option<String>,
}

/// 通过 MC 百科搜索页查找资源详情页与中文名（UTF-8 页面）。
/// 结果区为 `search-result-list`，项目链接形如
/// `https://www.mcmod.cn/class/198.html`，链接文字可能含 `<em>` 高亮标签。
/// 未收录 / 请求失败时返回 None。
async fn mcmod_search(title: &str) -> Option<McmodSearchHit> {
    let url = format!("https://search.mcmod.cn/s?key={}", urlenc(title));
    let resp = build_mcmod_client().ok()?.get(&url).send().await.ok()?;
    if !resp.status().is_success() {
        return None;
    }
    let text = resp.text().await.ok()?;
    // 定位结果区（未收录时页面没有该区块）
    let list_pos = text.find(r#"class="search-result-list""#)?;
    let rest = &text[list_pos..];
    // 在结果区内找第一个指向 /class/<数字>.html 的链接，并提取其文字
    let mut idx = 0;
    while idx < rest.len() {
        let Some(rel) = rest[idx..].find("<a") else {
            break;
        };
        let start = idx + rel;
        let Some(tag_end) = rest[start..].find('>') else {
            break;
        };
        let tag = &rest[start..start + tag_end];
        // 跳过分类链接（/class/category/…）等非项目链接：/class/ 后必须是数字
        let class_digits = tag.find("/class/").and_then(|p| {
            let after = &tag.as_bytes()[p + "/class/".len()..];
            let mut n = String::new();
            for &b in after {
                if b.is_ascii_digit() && n.len() < 8 {
                    n.push(b as char);
                } else {
                    break;
                }
            }
            (!n.is_empty()).then_some(n)
        });
        if let Some(id) = class_digits {
            let inner_start = start + tag_end + 1;
            let inner_end_rel = rest[inner_start..].find("</a>")?;
            let raw = &rest[inner_start..inner_start + inner_end_rel];
            let name = clean_mcmod_name(raw);
            return Some(McmodSearchHit {
                url: format!("https://www.mcmod.cn/class/{}.html", id),
                name,
            });
        }
        idx = start + tag_end + 1;
    }
    None
}

/// 清洗 MC 百科链接文字：去 HTML 标签、还原实体、去 [XX] 前缀、
/// 若形如「中文 (English)」且括号内全为 ASCII，则取括号前的中文部分。
fn clean_mcmod_name(raw: &str) -> Option<String> {
    // 去标签（<em> 等）
    let mut s = String::new();
    let mut in_tag = false;
    for c in raw.chars() {
        match c {
            '<' => in_tag = true,
            '>' => in_tag = false,
            _ if !in_tag => s.push(c),
            _ => {}
        }
    }
    let s = html_unescape(&s).trim().to_string();
    // 去 [XX] 前缀（如 "[JM] 旅行地图 (…)" → "旅行地图 (…)"；团队名最长约 20 字符）
    let s = if let Some(close) = s.find(']') {
        if s.starts_with('[') && close <= 24 {
            s[close + 1..].trim().to_string()
        } else {
            s
        }
    } else {
        s
    };
    if s.is_empty() {
        return None;
    }
    // 若为「中文 (English)」且括号内全为 ASCII，取括号前的中文（避免中英冗余）
    if s.ends_with(')') {
        if let Some(open) = s.rfind('(') {
            let inner = &s[open + 1..s.len() - 1];
            if !inner.is_empty()
                && inner.chars().all(|c| c.is_ascii() && !c.is_ascii_control())
            {
                let head = s[..open].trim();
                if contains_cjk(head) {
                    return Some(head.to_string());
                }
            }
        }
    }
    Some(s)
}

/// 还原常用 HTML 实体
fn html_unescape(s: &str) -> String {
    s.replace("&nbsp;", " ")
        .replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&#39;", "'")
        .replace("&rsquo;", "'")
        .replace("&ldquo;", "\u{201c}")
        .replace("&rdquo;", "\u{201d}")
}

/// 通过 MC 百科搜索页查找资源详情页（mcmod.cn/class/{id}.html）。
/// 失败/未收录时返回 None，调用方回退到搜索页。
pub async fn mcmod_lookup(title: &str) -> Option<String> {
    mcmod_search(title).await.map(|h| h.url)
}

/// 通过 MC 百科搜索页获取资源中文名（未收录 / 无中文名时返回 None）
async fn mcmod_lookup_zh(title: &str) -> Option<String> {
    mcmod_search(title).await.and_then(|h| h.name)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clean_name_strips_em_and_prefix() {
        assert_eq!(
            clean_mcmod_name("[JM] 旅行地图 (<em>Journey</em><em>Map</em>)").as_deref(),
            Some("旅行地图")
        );
    }

    #[test]
    fn clean_name_keeps_chinese_only() {
        assert_eq!(clean_mcmod_name("钠").as_deref(), Some("钠"));
        assert_eq!(
            clean_mcmod_name("[CaffeineMC] Sodium").as_deref(),
            Some("Sodium")
        );
    }

    #[test]
    fn dict_en_to_zh_matches_case_insensitive() {
        assert_eq!(dict_en_to_zh("Journey Map"), Some("旅行地图"));
        assert_eq!(dict_en_to_zh("sodium"), Some("钠"));
        assert_eq!(dict_en_to_zh("Unknown Mod"), None);
    }

    #[test]
    fn dict_zh_to_en_exact() {
        assert_eq!(dict_zh_to_en("旅行地图"), Some("Journey Map"));
        assert_eq!(dict_zh_to_en("不存在的东西"), None);
    }

    #[tokio::test]
    async fn mcmod_lookup_zh_real() {
        // 真实网络验证：Journey Map 在 MC 百科中应为「旅行地图」
        let zh = mcmod_lookup_zh("Journey Map").await;
        assert_eq!(zh.as_deref(), Some("旅行地图"));
    }
}
