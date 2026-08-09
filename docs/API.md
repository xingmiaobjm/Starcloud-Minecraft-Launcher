# Starcloud Minecraft Launcher Core — API 参考文档

> 版本 0.2.0 | 2026-08-09  
> 完整的 Minecraft 启动器核心库 API 文档，包含调用方法、参数说明、返回结构、错误处理与开发规范。

---

## 目录

1. [快速入门](#1-快速入门)
2. [错误处理与 Result 类型](#2-错误处理与-result-类型)
3. [下载引擎 (download)](#3-下载引擎-download)
4. [启动引擎 (launch)](#4-启动引擎-launch)
5. [版本数据模型 (version)](#5-版本数据模型-version)
6. [版本管理器 (version_manager)](#6-版本管理器-version_manager)
7. [插件系统 (plugin)](#7-插件系统-plugin)
8. [配置管理 (config)](#8-配置管理-config)
9. [账户管理 (account)](#9-账户管理-account)
10. [ModLoader 支持 (modloader)](#10-modloader-支持-modloader)
11. [服务层 (core)](#11-服务层-core)
12. [开发规范与最佳实践](#12-开发规范与最佳实践)

---

## 1. 快速入门

### 1.1 添加依赖

```toml
[dependencies]
starcloud-minecraft = { version = "0.1", features = ["download", "launch", "plugin"] }
```

### 1.2 Feature Flags

| Feature | 默认启用 | 说明 |
|---------|----------|------|
| `download` | 是 | 下载引擎（版本清单、资源、库文件） |
| `launch` | 是 | 启动引擎（Java 检测、参数构建、进程管理） |
| `plugin` | 是 | 插件系统（插件 API、动态加载、安装） |

### 1.3 最低完整示例

```rust
use starcloud_minecraft::{
    download::{DownloadEngine, fetch_version_manifest, fetch_version_details},
    launch::LaunchEngine,
    LaunchConfig,
};

#[tokio::main]
async fn main() -> starcloud_minecraft::Result<()> {
    // 1. 创建下载引擎
    let engine = DownloadEngine::new("/path/to/.minecraft");

    // 2. 获取版本清单
    let manifest = fetch_version_manifest().await?;
    let entry = &manifest.versions[0];

    // 3. 获取版本详情
    let details = fetch_version_details(&entry.url).await?;

    // 4. 下载库文件
    let libs = engine.library_downloader()
        .download_libraries(&details.libraries).await?;

    // 5. 下载资源
    if let Some(ai) = &details.asset_index {
        engine.asset_downloader().download_from_url(&ai.url).await?;
    }

    // 6. 启动
    let launcher = LaunchEngine::new(engine)?;
    let config = LaunchConfig::basic("1.21", "/path/to/.minecraft", "Player");
    let mut child = launcher.launch(&config, &details, &libs)?;
    child.wait()?;
    Ok(())
}
```

---

## 2. 错误处理与 Result 类型

### 2.1 类型定义

```rust
/// 所有公开 API 的统一返回类型
pub type Result<T> = std::result::Result<T, LauncherError>;
```

**使用方式：**

```rust
use starcloud_minecraft::Result;

fn do_something() -> Result<()> {
    // 直接使用 ? 传播错误
    let manifest = fetch_version_manifest().await?;
    Ok(())
}
```

### 2.2 LauncherError 枚举

```rust
#[derive(Error, Debug)]
pub enum LauncherError {
    DownloadError(String),                         // 下载失败（含 URL/HTTP 状态码）
    HttpError(reqwest::Error),                      // HTTP 客户端底层错误
    IoError(std::io::Error),                        // 文件系统 IO 错误
    JsonError(serde_json::Error),                   // JSON 解析错误
    ZipError(zip::result::ZipError),                // ZIP 解压错误
    ValidationError { expected: String, actual: String }, // 校验失败（SHA1 不匹配等）
    VersionNotFound(String),                        // 版本未找到
    JavaNotFound(u32),                              // Java 未找到，携带最低要求版本号
    LaunchError(String),                            // 启动进程失败
    PluginError(String),                            // 插件通用错误
    PluginLoadError(String),                        // 插件加载失败（库不存在/符号不匹配）
    PluginNotFound(String),                         // 插件未找到
    ConfigError(String),                            // 配置文件错误
    ArgumentError(String),                          // 参数校验失败
    JoinError(String),                              // 异步任务 panicked
    Other(String),                                  // 未分类错误
}
```

**模式匹配示例：**

```rust
match result {
    Err(LauncherError::JavaNotFound(ver)) => {
        eprintln!("需要安装 Java {} 或更高版本", ver);
    }
    Err(LauncherError::ValidationError { expected, actual }) => {
        eprintln!("文件校验失败: 期望 SHA1={}, 实际 SHA1={}", expected, actual);
    }
    Err(LauncherError::VersionNotFound(id)) => {
        eprintln!("版本 '{}' 在清单中未找到", id);
    }
    Err(LauncherError::DownloadError(msg)) => {
        eprintln!("下载错误: {}", msg);
    }
    Err(LauncherError::ArgumentError(msg)) => {
        eprintln!("参数错误: {}", msg);
    }
    Err(e) => {
        eprintln!("未知错误: {}", e);
    }
    Ok(data) => { /* 正常处理 */ }
}
```

### 2.3 From 自动转换

以下类型可通过 `?` 自动转为 `LauncherError`：

| 源类型 | 目标变体 |
|--------|----------|
| `reqwest::Error` | `HttpError` |
| `std::io::Error` | `IoError` |
| `serde_json::Error` | `JsonError` |
| `zip::result::ZipError` | `ZipError` |
| `tokio::task::JoinError` | `JoinError` |

---

## 3. 下载引擎 (download)

### 3.1 DownloadEngine

**导入：**

```rust
use starcloud_minecraft::download::DownloadEngine;
// 或
use starcloud_minecraft::DownloadEngine;
```

**构造函数：**

```rust
pub fn new(dot_minecraft: impl Into<PathBuf>) -> Self
```

| 参数 | 类型 | 说明 |
|------|------|------|
| `dot_minecraft` | `impl Into<PathBuf>` | `.minecraft` 目录路径 |

**示例：**

```rust
let engine = DownloadEngine::new("/home/user/.minecraft");
let engine = DownloadEngine::new(PathBuf::from("D:\\MC\\.minecraft"));
```

**实例方法：**

| 方法签名 | 返回类型 | 说明 |
|----------|----------|------|
| `dot_minecraft(&self)` | `&Path` | 获取 .minecraft 根目录路径 |
| `versions_dir(&self)` | `PathBuf` | 获取 `versions/` 子目录路径 |
| `assets_dir(&self)` | `PathBuf` | 获取 `assets/` 子目录路径 |
| `libraries_dir(&self)` | `PathBuf` | 获取 `libraries/` 子目录路径 |
| `natives_dir(&self, version_id: &str)` | `PathBuf` | 获取 `versions/{id}/natives/` 路径 |
| `asset_downloader(&self)` | `AssetDownloader` | 创建资源配置的下载器 |
| `library_downloader(&self)` | `LibraryDownloader` | 创建库文件配置的下载器 |

**辅助函数：**

```rust
/// 解压 native 库文件（JAR/ZIP），跳过 META-INF/ 目录
pub fn extract_native(native_path: &Path, extract_dir: &Path) -> Result<()>
```

| 参数 | 类型 | 说明 |
|------|------|------|
| `native_path` | `&Path` | native JAR 文件路径 |
| `extract_dir` | `&Path` | 解压目标目录 |

---

### 3.2 版本清单获取

```rust
use starcloud_minecraft::download::{fetch_version_manifest, fetch_version_details};
```

#### fetch_version_manifest

```rust
pub async fn fetch_version_manifest() -> Result<VersionManifest>
```

从 `https://piston-meta.mojang.com/mc/game/version_manifest_v2.json` 获取所有可用版本列表。

- **超时**：30 秒
- **返回**：`VersionManifest` 结构（详见 [§5.1](#51-versionmanifest)）
- **错误**：`DownloadError`（非 2xx 状态码）、`HttpError`（网络错误）、`JsonError`（解析失败）

**返回结构关键字段：**

```rust
VersionManifest {
    latest: LatestVersion {
        release: String,    // 如 "1.21"
        snapshot: String,   // 如 "24w21b"
    },
    versions: Vec<VersionEntry> {
        id: String,              // 如 "1.20.1"
        version_type: VersionType, // Release / Snapshot / OldBeta / OldAlpha / Other
        url: String,             // version.json 的下载地址
        time: String,            // ISO 时间
        release_time: String,    // ISO 发布时间
        sha1: Option<String>,    // SHA1 校验值
        compliance_level: Option<u32>,
    },
}
```

**示例：**

```rust
let manifest = fetch_version_manifest().await?;
println!("最新发布版: {}", manifest.latest.release);
println!("最新快照版: {}", manifest.latest.snapshot);

for v in manifest.versions.iter().take(5) {
    println!("  - {} ({:?})", v.id, v.version_type);
}
```

#### fetch_version_details

```rust
pub async fn fetch_version_details(url: &str) -> Result<VersionDetails>
```

根据版本条目中的 `url` 获取该版本的完整详情（libraries、arguments、assetIndex 等）。

- **参数**：`url` - `VersionEntry.url` 中的 URL
- **超时**：30 秒
- **返回**：`VersionDetails` 结构（详见 [§5.2](#52-versiondetails)）
- **错误**：`DownloadError`、`HttpError`、`JsonError`

**示例：**

```rust
let entry = &manifest.versions[0];
let details = fetch_version_details(&entry.url).await?;
println!("主类: {}", details.main_class);
println!("库文件数: {}", details.libraries.len());
```

---

### 3.3 AssetDownloader

**导入：**

```rust
use starcloud_minecraft::download::AssetDownloader;
```

**构造函数（通常通过 DownloadEngine 获取）：**

```rust
// 推荐方式
let downloader = engine.asset_downloader();

// 手动创建
pub fn new(base_dir: impl Into<PathBuf>) -> Self
// base_dir 应为 .minecraft/assets 目录
```

**方法：**

#### concurrency

```rust
pub fn concurrency(self, n: usize) -> Self
```

设置并发下载数。默认为 8。

| 参数 | 类型 | 说明 |
|------|------|------|
| `n` | `usize` | 并发下载线程数 |

**返回**：`Self`（Builder 模式）

#### download_assets

```rust
pub async fn download_assets(&self, asset_index: &AssetIndex) -> Result<()>
```

下载资源索引中的所有资源文件到 `{base_dir}/objects/{hash[0..2]}/{hash}`。

| 参数 | 类型 | 说明 |
|------|------|------|
| `asset_index` | `&AssetIndex` | 资源索引对象 |

**返回**：`Ok(())` 或下载/IO 错误

**实现细节：**
- 文件按 `hash[..2]/hash` 路径存储（如 `a1/b23...`）
- 已存在的文件自动跳过（断点续传）
- 原子写入：先写 `.tmp` 文件再 `rename`
- 分批并发，每批等待完成后再启下一批

#### download_from_url

```rust
pub async fn download_from_url(&self, index_url: &str) -> Result<()>
```

便捷方法：从 URL 获取资源索引 JSON 并下载所有资源。

| 参数 | 类型 | 说明 |
|------|------|------|
| `index_url` | `&str` | 资源索引 JSON 的 URL（来自 `AssetIndexInfo.url`） |

**返回**：`Ok(())` 或下载/IO/JSON 错误

**示例：**

```rust
let downloader = engine.asset_downloader().concurrency(16);

if let Some(ai) = &details.asset_index {
    // 方式 1：从 URL 下载
    downloader.download_from_url(&ai.url).await?;

    // 方式 2：先获取索引再下载（可分步处理）
    let client = reqwest::Client::new();
    let resp = client.get(&ai.url).send().await?;
    let index: AssetIndex = resp.json().await?;
    downloader.download_assets(&index).await?;
}
```

---

### 3.4 LibraryDownloader

**导入：**

```rust
use starcloud_minecraft::download::LibraryDownloader;
```

**构造函数：**

```rust
// 推荐方式
let downloader = engine.library_downloader();

// 手动创建
pub fn new(base_dir: impl Into<PathBuf>) -> Self
// base_dir 应为 .minecraft/libraries 目录
```

**Builder 方法：**

```rust
pub fn concurrency(self, n: usize) -> Self
// 设置并发下载数，默认 6

pub fn os_name(self, name: impl Into<String>) -> Self
// 设置 OS 名称用于 native 库筛选，默认自动检测
// 可选值: "windows" / "linux" / "osx"
```

#### download_libraries

```rust
pub async fn download_libraries(&self, libraries: &[Library]) -> Result<Vec<String>>
```

**这是下载引擎最核心的方法**，完成以下所有工作：

1. 遍历 `libraries` 列表
2. 按 OS/Arch 规则 (`rules`) 筛选需要的库
3. 识别并下载 native 库（`natives`）
4. 自动解压需要 extract 的 native 库
5. 对每个文件做 SHA1 校验
6. 原子写入（`.tmp` → 最终路径）

**参数：**

| 参数 | 类型 | 说明 |
|------|------|------|
| `libraries` | `&[Library]` | 从 `VersionDetails.libraries` 获取 |

**返回：** `Result<Vec<String>>`

返回所有主 artifact 的绝对路径列表（classpath），用于启动时 `-cp` 参数。

**示例：**

```rust
let downloader = engine.library_downloader()
    .concurrency(10);

let classpath_entries: Vec<String> = downloader
    .download_libraries(&details.libraries)
    .await?;

println!("classpath 条目数: {}", classpath_entries.len());
for path in &classpath_entries {
    println!("  {}", path);
}
```

**Native 库筛选逻辑：**

1. 库的 `rules` 中 `os.name` 需匹配当前系统
2. `natives.{os_name}` 的值如 `"natives-linux"` 会被替换 `${arch}` → `64`
3. 对应 `classifiers` 中的 native artifact 会被下载
4. 若 `extract` 字段存在，native JAR 会自动解压到同目录

---

### 3.5 HttpClient（连接池优化）

**导入：**

```rust
use starcloud_minecraft::download::HttpClient;
```

**构造函数：**

```rust
pub fn new() -> Result<Self>
```

创建优化的 HTTP 客户端，内置：

| 优化项 | 配置值 |
|--------|--------|
| 自动解压 | gzip + brotli + deflate |
| 连接超时 | 15 秒 |
| 请求超时 | 300 秒 |
| TCP Keepalive | 60 秒 |
| TCP No Delay | 启用 |
| 连接池（每主机最大空闲） | 10 |
| 连接池空闲超时 | 90 秒 |

**方法：**

```rust
/// 自定义重试配置
pub fn with_retry(self, config: RetryConfig) -> Self

/// 获取内部 reqwest::Client
pub fn inner(&self) -> &reqwest::Client

/// 带重试的 GET 请求，返回 Response
pub async fn get_with_retry(&self, url: &str) -> Result<reqwest::Response>

/// 带重试的 GET 请求，返回 bytes
pub async fn get_bytes_with_retry(&self, url: &str) -> Result<Vec<u8>>

/// 带重试的 GET 请求，返回 JSON 反序列化结果
pub async fn get_json_with_retry<T: DeserializeOwned>(&self, url: &str) -> Result<T>
```

### 3.6 RetryConfig

```rust
pub struct RetryConfig {
    pub max_retries: u32,                // 最大重试次数，默认 3
    pub initial_delay: Duration,         // 初始重试间隔，默认 1s
    pub max_delay: Duration,             // 最大重试间隔，默认 60s
    pub backoff_factor: f64,             // 退避因子，默认 2.0
    pub retryable_statuses: Vec<u16>,    // 可重试的状态码，默认 [408,429,500,502,503,504]
}
```

**方法：**

```rust
/// 执行带重试的操作
pub async fn retry<F, Fut, T>(&self, op: F) -> Result<T>
where
    F: Fn() -> Fut,
    Fut: Future<Output = Result<T>>,
```

**示例：**

```rust
use starcloud_minecraft::download::{HttpClient, RetryConfig};
use std::time::Duration;

let client = HttpClient::new()?
    .with_retry(RetryConfig {
        max_retries: 5,
        initial_delay: Duration::from_millis(500),
        ..RetryConfig::default()
    });

// 自动重试的 GET 请求
let bytes = client.get_bytes_with_retry("https://example.com/data.bin").await?;
let json: MyStruct = client.get_json_with_retry("https://api.example.com/info").await?;
```

---

### 3.7 ProgressTracker

**导入：**

```rust
use starcloud_minecraft::download::ProgressTracker;
```

线程安全的下载进度追踪器，基于原子操作。

**构造函数：**

```rust
pub fn new() -> Self
```

**方法：**

```rust
/// 设置下载总大小（字节）
pub fn set_total(&self, bytes: u64)

/// 累加已下载字节
pub fn add_progress(&self, bytes: u64)

/// 设置当前已下载字节（绝对值）
pub fn set_progress(&self, bytes: u64)

/// 检查是否已完成（已下载 >= 总大小）
pub fn is_complete(&self) -> bool

/// 获取进度快照
pub fn snapshot(&self) -> ProgressInfo

/// 重置进度
pub fn reset(&self)
```

**异步方法：**

```rust
/// 设置进度状态
pub async fn set_status(&self, status: ProgressStatus)

/// 获取进度状态
pub async fn status(&self) -> ProgressStatus

/// 注册进度回调
pub async fn on_progress<F>(&self, callback: F)
where F: Fn(&ProgressInfo) + Send + Sync + 'static

/// 手动触发所有回调
pub async fn fire_callbacks(&self)
```

**返回结构 ProgressInfo：**

```rust
pub struct ProgressInfo {
    pub downloaded_bytes: u64,     // 已下载字节
    pub total_bytes: u64,          // 总字节
    pub percentage: f64,           // 百分比 (0.0 ~ 100.0)
    pub stage: String,             // 当前阶段名称
    pub status: ProgressStatus,    // 状态
    pub eta_secs: Option<f64>,     // 估算剩余时间（秒）
    pub speed_bytes_per_sec: f64,  // 速度（字节/秒）
}
```

**ProgressStatus 枚举：**

```rust
pub enum ProgressStatus {
    Pending,               // 等待开始
    Downloading,           // 下载中
    Verifying,             // 校验中
    Extracting,            // 解压中
    Completed,             // 已完成
    Failed(String),        // 已失败（带原因）
    Cancelled,             // 已取消
}
```

**示例：**

```rust
let tracker = ProgressTracker::new();
tracker.set_total(1024 * 1024 * 100); // 100 MB

tracker.on_progress(|info| {
    println!("进度: {:.1}% | 速度: {:.1} MB/s",
        info.percentage,
        info.speed_bytes_per_sec / 1048576.0);
}).await;

// 模拟下载更新
tracker.add_progress(50 * 1024 * 1024);
tracker.fire_callbacks().await;

// 完成检查
if tracker.is_complete() {
    tracker.set_status(ProgressStatus::Completed).await;
}
```

---

## 4. 启动引擎 (launch)

### 4.1 LaunchConfig

**导入：**

```rust
use starcloud_minecraft::LaunchConfig;
```

**完整字段：**

```rust
pub struct LaunchConfig {
    pub version_id: String,             // 要启动的版本 ID
    pub dot_minecraft: PathBuf,         // .minecraft 目录路径
    pub username: String,               // 玩家名（1-32 字符）
    pub uuid: String,                   // 玩家 UUID（v4 格式）
    pub min_memory_mb: u32,             // 最小堆内存 (MB)，≥256
    pub max_memory_mb: u32,             // 最大堆内存 (MB)，≤65536
    pub window_width: u32,              // 窗口宽度，≥320
    pub window_height: u32,             // 窗口高度，≥240
    pub access_token: Option<String>,   // 正版登录令牌（离线填 None）
    pub client_token: Option<String>,   // 客户端令牌
    pub xuid: Option<String>,           // Xbox Live XUID
    pub user_type: String,              // "mojang" 或 "msa"
    pub extra_jvm_args: Vec<String>,    // 自定义 JVM 参数
    pub extra_game_args: Vec<String>,   // 自定义游戏参数
    pub java_executable: Option<PathBuf>, // 手动指定 Java 路径
}
```

**构造函数：**

```rust
/// 默认值：min=1024 max=4096 窗口=854x480 user_type="mojang"
pub fn basic(
    version_id: impl Into<String>,
    dot_minecraft: impl Into<PathBuf>,
    username: impl Into<String>,
) -> Self
```

**Builder 方法（链式调用）：**

```rust
/// 设置正版账户
pub fn with_account(
    self,
    uuid: impl Into<String>,
    access_token: impl Into<String>,
    user_type: impl Into<String>,  // "mojang" | "msa"
) -> Self

/// 设置内存分配 (MB)
pub fn with_memory(self, min_mb: u32, max_mb: u32) -> Self

/// 设置窗口大小
pub fn with_window(self, width: u32, height: u32) -> Self
```

**示例：**

```rust
let config = LaunchConfig::basic("1.21", "/home/user/.minecraft", "Steve")
    .with_memory(2048, 8192)
    .with_window(1920, 1080);

// 正版登录：
let config = LaunchConfig::basic("1.21", "/tmp/.mc", "Notch")
    .with_account("uuid", "access_token", "msa");

// 手动指定 Java：
let mut config = LaunchConfig::basic("1.20.1", ".minecraft", "Player");
config.java_executable = Some(PathBuf::from("/usr/lib/jvm/java-21/bin/java"));
```

---

### 4.2 LaunchEngine

**导入：**

```rust
use starcloud_minecraft::launch::LaunchEngine;
```

**构造函数：**

```rust
/// 自动检测 Java
pub fn new(download_engine: DownloadEngine) -> Result<Self>

/// 使用指定 Java 运行时
pub fn with_java(download_engine: DownloadEngine, java_runtime: JavaRuntime) -> Self
```

**实例方法：**

```rust
/// 获取内部 DownloadEngine 引用
pub fn download_engine(&self) -> &DownloadEngine

/// 获取检测到的 Java 运行时
pub fn java_runtime(&self) -> &JavaRuntime
```

#### build_process

```rust
pub fn build_process(
    &self,
    config: &LaunchConfig,
    version: &VersionDetails,
    library_paths: &[String],
) -> Result<Command>
```

构建 `std::process::Command`，由调用者决定 `spawn()` 或仅输出命令行。**此方法不启动进程。**

| 参数 | 类型 | 说明 |
|------|------|------|
| `config` | `&LaunchConfig` | 启动配置 |
| `version` | `&VersionDetails` | 版本详情 |
| `library_paths` | `&[String]` | classpath 路径列表 |

**返回**：`std::process::Command`（已配置 JVM 参数、主类、游戏参数、工作目录、IO 管道）

#### launch

```rust
pub fn launch(
    &self,
    config: &LaunchConfig,
    version: &VersionDetails,
    library_paths: &[String],
) -> Result<Child>
```

构建并启动 Minecraft 进程。

**返回**：`std::process::Child`（进程句柄，含 stdout/stderr 管道）

**错误**：`LaunchError`

#### print_command

```rust
pub fn print_command(
    &self,
    config: &LaunchConfig,
    version: &VersionDetails,
    library_paths: &[String],
) -> Result<String>
```

输出完整的启动命令行（调试用）。

**返回**：空格分隔的命令行字符串

**示例：**

```rust
let launcher = LaunchEngine::new(engine)?;

// 调试：查看完整命令
let cmd = launcher.print_command(&config, &details, &libs)?;
println!("Java 命令: {}", cmd);

// 启动
let mut child = launcher.launch(&config, &details, &libs)?;
println!("MC PID: {}", child.id());

// 读取输出流
use std::io::BufRead;
let stdout = child.stdout.take().unwrap();
let reader = std::io::BufReader::new(stdout);
for line in reader.lines() {
    println!("[MC] {}", line?);
}

let status = child.wait()?;
println!("退出码: {:?}", status.code());
```

---

### 4.3 JavaDetector

**导入：**

```rust
use starcloud_minecraft::launch::JavaDetector;
```

**构造函数：**

```rust
pub fn new() -> Self
```

**Builder 方法：**

```rust
/// 添加额外搜索路径
pub fn with_extra_path(self, path: impl Into<PathBuf>) -> Self

/// 设置最低 Java 版本要求（默认 8）
pub fn with_min_version(self, version: u32) -> Self
```

**方法：**

```rust
/// 自动检测最合适的 Java
pub fn detect(&self) -> Result<JavaRuntime>

/// 检测指定目录下的 Java
pub fn detect_at(path: &PathBuf) -> Result<JavaRuntime>
```

**检测顺序：**
1. `JAVA_HOME` 环境变量 → `{JAVA_HOME}/bin/javaw` (win) / `java` (unix)
2. `PATH` 环境变量中的 `java`/`javaw`
3. `extra_paths` 额外搜索路径
4. Windows: `C:/D:/E:/Program Files/Java/*`, `Program Files/*java*/` 等
5. Linux/macOS: `/usr/lib/jvm/*`, `/opt/*`, `/Library/Java/JavaVirtualMachines/*`

选择策略：按主版本号降序，取第一个满足 `min_version` 的。

**返回结构 JavaRuntime：**

```rust
pub struct JavaRuntime {
    pub executable: PathBuf,        // 可执行文件完整路径
    pub major_version: u32,         // 主版本号（8, 17, 21...）
    pub version_string: String,     // 完整版本字符串
    pub java_home: PathBuf,         // JAVA_HOME 目录
    pub is_64bit: bool,             // 是否为 64 位
}
```

**示例：**

```rust
let detector = JavaDetector::new()
    .with_min_version(17)
    .with_extra_path("/custom/jdk-21");

let java = detector.detect()?;
println!("Java {} (64bit: {})", java.major_version, java.is_64bit);

// 传参给 LaunchEngine
let launcher = LaunchEngine::with_java(engine, java);
```

---

### 4.4 OsKind

```rust
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum OsKind {
    Windows,
    Linux,
    MacOS,
}
```

**方法：**

```rust
impl OsKind {
    /// 转为 Mojang 格式的 OS 名 ("windows" / "linux" / "osx")
    pub fn as_mojang(&self) -> &str

    /// 自动检测当前操作系统
    pub fn detect() -> Self

    /// 获取系统架构 ("x86_64" / "aarch64" 等)
    pub fn arch() -> &'static str
}
```

---

## 5. 版本数据模型 (version)

### 5.1 VersionManifest

```rust
pub struct VersionManifest {
    pub latest: LatestVersion,       // 最新发布版和快照版
    pub versions: Vec<VersionEntry>, // 所有版本列表
}

pub struct LatestVersion {
    pub release: String,             // 如 "1.21"
    pub snapshot: String,            // 如 "24w21b"
}
```

### 5.2 VersionDetails

```rust
pub struct VersionDetails {
    pub id: String,                                  // 版本 ID
    pub main_class: String,                          // 主类名（如 "net.minecraft.client.main.Main"）
    pub libraries: Vec<Library>,                     // 库文件列表
    pub asset_index: Option<AssetIndexInfo>,         // 资源索引信息
    pub assets: Option<String>,                      // 资源版本 ID（如 "17"）
    pub downloads: Downloads,                        // 核心文件下载信息
    pub logging: LoggingConfig,                      // 日志配置
    pub minecraft_arguments: Option<String>,         // 旧版启动参数
    pub arguments: Option<VersionArguments>,         // 新版 JVM+游戏参数
    pub minimum_launcher_version: i32,               // 最低启动器版本
    pub java_version: Option<JavaVersionInfo>,       // Java 版本要求
    pub version_type: VersionType,                   // 版本类型
    pub release_time: String,
    pub time: String,
}
```

**Downloads 结构（核心客户端 jar）：**

```rust
pub struct Downloads {
    pub client: Option<DownloadInfo>,                // 客户端 jar
    pub client_mappings: Option<DownloadInfo>,       // 混淆映射表
    pub server: Option<DownloadInfo>,                // 服务端 jar
    pub server_mappings: Option<DownloadInfo>,
}

pub struct DownloadInfo {
    pub sha1: String,                                // SHA1 校验值
    pub size: u64,                                   // 文件大小（字节）
    pub url: String,                                 // 下载 URL
    pub path: Option<String>,
}
```

**Library 结构：**

```rust
pub struct Library {
    pub name: String,                                       // Maven 坐标（如 "com.mojang:logging:1.1.1"）
    pub downloads: Option<LibraryDownloads>,
    pub url: Option<String>,                                // 自定义下载源
    pub rules: Option<Vec<Rule>>,                           // 加载规则
    pub natives: Option<HashMap<String, String>>,           // native 库映射
    pub extract: Option<ExtractConfig>,                     // 是否需解压
}

pub struct LibraryDownloads {
    pub artifact: Option<LibraryArtifact>,                  // 主 artifact
    pub classifiers: Option<HashMap<String, LibraryArtifact>>, // native classifiers
}

pub struct LibraryArtifact {
    pub path: String,                                       // 本地路径
    pub sha1: String,
    pub size: u64,
    pub url: String,
}
```

**条件加载规则：**

```rust
pub struct Rule {
    pub action: RuleAction,   // Allow / Disallow
    pub os: Option<OsRule>,
    pub features: Option<HashMap<String, bool>>,
}

pub enum RuleAction { Allow, Disallow }

pub struct OsRule {
    pub name: String,          // "windows" / "linux" / "osx"
    pub version: Option<String>,
    pub arch: Option<String>,  // "x86" / "x86_64"
}
```

### 5.3 VersionArguments

```rust
pub struct VersionArguments {
    pub game: Vec<ArgumentValue>,  // 游戏参数
    pub jvm: Vec<ArgumentValue>,   // JVM 参数
}

pub enum ArgumentValue {
    Simple(String),                        // 简单字符串参数
    Conditional(ConditionalArgument),      // 条件参数
}

pub struct ConditionalArgument {
    pub rules: Vec<Rule>,
    pub value: ArgumentValueInner,
}

pub enum ArgumentValueInner {
    Single(String),
    Multiple(Vec<String>),
}
```

### 5.4 AssetIndex

```rust
pub struct AssetIndex {
    pub objects: HashMap<String, AssetObject>,  // key=文件名, value=hash+size
}

pub struct AssetObject {
    pub hash: String,    // SHA1，也是文件存储名
    pub size: u64,
}
```

---

## 6. 版本管理器 (version_manager)

### 6.1 VersionComparator

**导入：**

```rust
use starcloud_minecraft::version_manager::VersionComparator;
```

**静态方法（全部为纯函数，无状态）：**

```rust
/// 语义化版本比较，返回 Ordering::Less/Equal/Greater
pub fn compare(a: &str, b: &str) -> std::cmp::Ordering

/// 比较两个版本的差异方向
pub fn diff(current: &str, target: &str) -> VersionDiff

/// 判断是否为快照版（包含 'w' 或 'pre'）
pub fn is_snapshot(version_id: &str) -> bool

/// 判断是否为正式发布版
pub fn is_release(version_id: &str) -> bool

/// 校验版本 ID 格式（1-64字符，仅字母数字 . - _）
pub fn is_valid_id(id: &str) -> bool

/// 从版本列表中找最新版（仅正式版）
pub fn latest(versions: &[VersionEntry]) -> Option<&VersionEntry>

/// 从版本列表中找最旧版（仅正式版）
pub fn oldest(versions: &[VersionEntry]) -> Option<&VersionEntry>

/// 按版本号范围筛选
pub fn filter_range(
    versions: &[VersionEntry],
    min: Option<&str>,
    max: Option<&str>,
    include_snapshots: bool,
) -> Vec<&VersionEntry>
```

**VersionDiff 枚举：**

```rust
pub enum VersionDiff {
    Same,               // 版本相同
    Newer { id: String }, // 目标版本更新
    Older,              // 目标版本更旧
    Incomparable,       // 无法比较
}
```

**示例：**

```rust
use std::cmp::Ordering;

assert_eq!(VersionComparator::compare("1.20.1", "1.21"), Ordering::Less);

let diff = VersionComparator::diff("1.19", "1.20");
match diff {
    VersionDiff::Newer { id } => println!("有新版本: {}", id),
    _ => {}
}

// 筛选 1.16 ~ 1.20 之间的正式版
let filtered = VersionComparator::filter_range(
    &manifest.versions,
    Some("1.16"),
    Some("1.20"),
    false, // 不含快照
);
```

---

### 6.2 VersionCache

**导入：**

```rust
use starcloud_minecraft::version_manager::VersionCache;
```

两层级缓存：L1 内存 LRU → L2 磁盘文件 → L3 网络。

**构造函数：**

```rust
pub fn new(dot_minecraft: impl Into<PathBuf>, max_memory_entries: usize) -> Self
```

| 参数 | 类型 | 说明 |
|------|------|------|
| `dot_minecraft` | `impl Into<PathBuf>` | .minecraft 目录 |
| `max_memory_entries` | `usize` | 内存 LRU 最大条目数 |

**方法：**

```rust
/// 带缓存的版本清单获取
pub async fn get_manifest<F, Fut>(&self, fetch: F) -> Result<VersionManifest>
where
    F: FnOnce() -> Fut,
    Fut: Future<Output = Result<VersionManifest>>,

/// 带缓存的版本详情获取
pub async fn get_version_details<F, Fut>(
    &self,
    version_id: &str,
    fetch: F,
) -> Result<VersionDetails>
where
    F: FnOnce() -> Fut,
    Fut: Future<Output = Result<VersionDetails>>,

/// 预缓存版本详情（不从网络获取）
pub async fn cache_version_details(&self, version_id: &str, details: &VersionDetails) -> Result<()>

/// 失效某个缓存条目
pub fn invalidate(&self, key: &str) -> Result<()>

/// 清空所有内存缓存
pub fn clear_memory(&self) -> Result<()>

/// 清空所有磁盘缓存
pub async fn clear_disk_cache(&self) -> Result<()>

/// 获取缓存统计
pub fn stats(&self) -> Result<CacheStats>
```

**返回结构 CacheStats：**

```rust
pub struct CacheStats {
    pub memory_entries: usize,      // 当前内存缓存条目数
    pub max_memory_entries: usize,  // 最大容许条目数
}
```

**示例：**

```rust
let cache = VersionCache::new(".minecraft", 128);

// 首次访问：走网络
let manifest = cache.get_manifest(|| async {
    fetch_version_manifest().await
}).await?;

// 二次访问：命中小于 1ms
let manifest2 = cache.get_manifest(|| async {
    fetch_version_manifest().await
}).await?;

// 版本详情缓存
let details = cache.get_version_details("1.21", || async {
    let manifest = fetch_version_manifest().await?;
    let entry = manifest.versions.iter()
        .find(|v| v.id == "1.21")
        .ok_or(LauncherError::VersionNotFound("1.21".into()))?;
    fetch_version_details(&entry.url).await
}).await?;

// 查看统计
let stats = cache.stats()?;
println!("命中条目: {}/{}", stats.memory_entries, stats.max_memory_entries);
```

---

### 6.3 VersionInstaller

**导入：**

```rust
use starcloud_minecraft::version_manager::VersionInstaller;
```

**构造函数：**

```rust
pub fn new(download_engine: DownloadEngine) -> Self
```

**方法：**

```rust
/// 检查版本是否已安装（version.json 存在即认为已安装）
pub fn is_installed(&self, version_id: &str) -> bool

/// 列出所有已安装版本（按版本号排序）
pub async fn list_installed(&self) -> Result<Vec<String>>

/// 安装版本：创建目录并写出 version.json
pub async fn install_version(&self, details: &VersionDetails) -> Result<()>

/// 写出资源索引 JSON 到 assets/indexes/{id}.json
pub async fn install_asset_index(&self, asset_index: &AssetIndex, assets_id: &str) -> Result<()>

/// 读取已安装的版本详情
pub async fn read_installed_version(&self, version_id: &str) -> Result<VersionDetails>

/// 删除版本（递归删除目录）
pub async fn delete_version(&self, version_id: &str) -> Result<()>

/// 获取版本磁盘占用（字节）
pub async fn version_size(&self, version_id: &str) -> Result<u64>
```

**示例：**

```rust
let installer = VersionInstaller::new(engine);

// 检查并安装
if !installer.is_installed("1.21") {
    let manifest = fetch_version_manifest().await?;
    let entry = find_version(&manifest, "1.21")?;
    let details = fetch_version_details(&entry.url).await?;
    installer.install_version(&details).await?;
}

// 列出已安装
for id in installer.list_installed().await? {
    let size = installer.version_size(&id).await?;
    println!("{} - {} MB", id, size / 1048576);
}

// 删除旧版本
installer.delete_version("1.19.2").await?;
```

---

### 6.4 VersionValidator

**导入：**

```rust
use starcloud_minecraft::version_manager::VersionValidator;
```

**所有方法均为纯函数，无状态。**

```rust
/// 校验 LaunchConfig 所有字段合法性
/// 检查项：version_id 非空、用户名 1-32 字符字母数字下划线、
///   UUID 合法、最小内存≥256、最小≤最大、最大≤65536、窗口≥320×240
pub fn validate_launch_config(config: &LaunchConfig) -> Result<()>

/// 校验 VersionDetails 基本完整性（id/main_class 非空）
pub fn validate_version_details(details: &VersionDetails) -> Result<()>

/// 检查 version.json 文件是否存在
pub fn check_version_json(dot_minecraft: &Path, version_id: &str) -> Result<()>

/// 检查资源索引目录是否非空
pub fn check_asset_indexes(dot_minecraft: &Path) -> Result<()>

/// 校验 URL 以 http:// 或 https:// 开头
pub fn validate_url(url: &str) -> Result<()>

/// 校验路径不含非法控制字符（\0, \n, \r）
pub fn validate_path(path: &Path) -> Result<()>

/// 校验插件元信息：id 1-128字符、name 1-64字符、version 非空
pub fn validate_plugin_meta(meta: &PluginMeta) -> Result<()>
```

**使用建议：** 在 `launch()` 调用前必须执行 `validate_launch_config()`。

---

## 7. 插件系统 (plugin)

### 7.1 Plugin trait

**导入：**

```rust
use starcloud_minecraft::plugin::{
    Plugin, PluginMeta, PluginRegisterContext,
    PluginContext, PluginStage,
};
```

**完整 trait 定义：**

```rust
pub trait Plugin: Send + Sync {
    /// 返回插件元信息（必须实现）
    fn meta(&self) -> PluginMeta;

    /// 插件注册/启用时调用。可在此初始化资源、注册回调等
    fn on_enable(&self, ctx: &PluginRegisterContext) -> Result<()> { Ok(()) }

    /// 插件卸载/禁用时调用。释放资源、保存状态
    fn on_disable(&self) -> Result<()> { Ok(()) }

    /// 生命周期事件钩子
    fn on_event(&self, stage: PluginStage, ctx: &mut PluginContext) -> Result<()> { Ok(()) }

    /// 获取插件内部任意类型数据（用于插件间交互）
    fn as_any(&self) -> Option<&dyn Any> { None }
    fn as_any_mut(&mut self) -> Option<&mut dyn Any> { None }
}
```

**PluginMeta 结构：**

```rust
pub struct PluginMeta {
    pub id: String,                    // 全局唯一 ID，建议倒置域名格式
    pub name: String,                  // 显示名称（≤64 字符）
    pub version: String,               // 语义化版本号（非空）
    pub author: String,                // 作者名
    pub description: String,           // 描述
    pub dependencies: Vec<String>,     // 依赖插件的 ID 列表
    pub min_launcher_version: String,  // 最低启动器版本
}
```

---

### 7.2 PluginStage（生命周期事件）

```rust
pub enum PluginStage {
    // ---- 核心生命周期 ----
    Init,                   // 插件初始化（注册时触发）
    PreVersionManifest,     // 获取版本清单前
    PostVersionManifest,    // 版本清单下载完成          ← 新增
    PostVersionDetails,     // 获取版本详情后
    PreAssetDownload,       // 资源下载前
    PostAssetDownload,      // 资源下载后
    PreLibraryDownload,     // 库文件下载前
    PostLibraryDownload,    // 库文件下载后
    PreLaunch,              // 启动前（参数已构建，可修改 ctx）
    PostLaunch,             // 进程启动后
    OnGameExit,             // 游戏进程正常退出

    // ---- GUI 专用钩子 (v0.1+) ----
    LauncherReady,          // 核心模块就绪，GUI 可开始渲染
    LauncherShutdown,       // 启动器即将关闭，GUI 应保存状态
    DownloadProgress,       // 下载进度更新（高频，回调中勿做重操作）
    StatusChanged,          // 启动器状态变更
    GameCrashed,            // 游戏进程崩溃（ctx.crash_info 含详情）
    ConfigChanged,          // 用户修改了 LauncherConfig
    AccountChanged,         // 登录/登出/切换用户
    VersionInstalled,       // 版本安装完成
    VersionUninstalled,     // 版本已删除
    ErrorOccurred,          // 异步操作失败（GUI 集中展示错误）
}
```

---

### 7.3 PluginContext 和 PluginRegisterContext

```rust
/// 事件上下文（每次触发事件时传递）
pub struct PluginContext {
    pub dot_minecraft: PathBuf,                      // .minecraft 目录
    pub version_id: Option<String>,                  // 当前版本
    pub username: Option<String>,                    // 玩家名
    pub game_pid: Option<u32>,                       // 游戏 PID（PostLaunch 后可用）
    pub extra: HashMap<String, String>,              // 插件间共享数据

    // ---- GUI 专用字段 ----
    pub launcher_status: LauncherStatus,             // 当前启动器状态
    pub download_progress: Option<DownloadProgressInfo>, // 下载进度
    pub crash_info: Option<CrashInfo>,               // 崩溃信息（GameCrashed 时填充）
    pub error_message: Option<String>,               // 错误消息（ErrorOccurred 时填充）
}
```

**Builder 方法：**

```rust
impl PluginContext {
    pub fn new(dot_minecraft: impl Into<PathBuf>) -> Self
    pub fn with_download_progress(self, info: DownloadProgressInfo) -> Self
    pub fn with_crash_info(self, info: CrashInfo) -> Self
    pub fn with_error(self, msg: impl Into<String>) -> Self
    pub fn with_status(self, status: LauncherStatus) -> Self
}
```

**LauncherStatus 枚举：**

```rust
pub enum LauncherStatus {
    Idle,            // 空闲
    Initializing,    // 初始化中
    FetchingManifest,// 版本清单获取中
    Downloading,     // 下载中
    Launching,       // 启动中
    Running,         // 游戏运行中
    Error,           // 发生错误
}
```

**DownloadProgressInfo 结构：**

```rust
pub struct DownloadProgressInfo {
    pub stage: String,              // 当前阶段描述（如 "下载资源文件"）
    pub downloaded_bytes: u64,      // 已下载字节
    pub total_bytes: u64,           // 总字节
    pub percentage: f64,            // 百分比 0.0~100.0
    pub speed_bytes_per_sec: f64,   // 速度（字节/秒）
    pub eta_secs: Option<f64>,      // 估算剩余秒数
}
```

**CrashInfo 结构：**

```rust
pub struct CrashInfo {
    pub exit_code: i32,             // 进程退出码
    pub version_id: String,         // 崩溃的版本
    pub out_of_memory: bool,        // 是否因内存不足崩溃
    pub last_stderr: String,        // 最近几行 stderr（最多 2KB）
}
```

**GUI 插件完整使用示例：**

```rust
fn on_event(&self, stage: PluginStage, ctx: &mut PluginContext) -> Result<()> {
    match stage {
        PluginStage::LauncherReady => {
            // 核心就绪 → GUI 开始渲染主窗口
            self.open_main_window();
        }
        PluginStage::LauncherShutdown => {
            // 保存窗口大小/位置、清理纹理缓存
            self.save_window_state();
        }
        PluginStage::StatusChanged => {
            match ctx.launcher_status {
                LauncherStatus::Downloading => self.show_progress_bar(),
                LauncherStatus::Running => self.minimize_to_tray(),
                LauncherStatus::Error => self.show_error_dialog(ctx.error_message.as_deref()),
                _ => {}
            }
        }
        PluginStage::DownloadProgress => {
            if let Some(ref prog) = ctx.download_progress {
                self.update_progress_bar(prog.percentage, prog.speed_bytes_per_sec);
            }
        }
        PluginStage::GameCrashed => {
            if let Some(ref crash) = ctx.crash_info {
                self.show_crash_dialog(crash);
            }
        }
        PluginStage::ConfigChanged => {
            self.reload_settings();
        }
        PluginStage::AccountChanged => {
            self.update_user_display(&ctx.username);
        }
        PluginStage::VersionInstalled => {
            self.refresh_version_list();
            self.show_toast(&format!("版本 {} 安装完成", ctx.version_id.as_deref().unwrap_or("?")));
        }
        PluginStage::ErrorOccurred => {
            self.show_error_toast(ctx.error_message.as_deref().unwrap_or("未知错误"));
        }
        _ => {}
    }
    Ok(())
}
```

**事件触发时机一览：**

| 事件 | 触发方 | 频率 | ctx 关键字段 |
|------|--------|------|-------------|
| `LauncherReady` | 启动器主循环 | 一次性 | `launcher_status: Initializing` |
| `LauncherShutdown` | 启动器退出前 | 一次性 | — |
| `StatusChanged` | 状态机变更时 | 低 | `launcher_status` |
| `DownloadProgress` | 资源/库下载中 | 高频(≥100ms间隔) | `download_progress` |
| `GameCrashed` | 进程 wait() 返回非0 | 按需 | `crash_info` |
| `ConfigChanged` | 用户修改配置后 | 低 | `extra["config_key"]` |
| `AccountChanged` | 登录/登出/切换后 | 低 | `username` |
| `VersionInstalled` | install_version() 后 | 低 | `version_id` |
| `VersionUninstalled` | delete_version() 后 | 低 | `version_id` |
| `ErrorOccurred` | 异步操作失败 | 按需 | `error_message` |

---

### 7.4 PluginRegisterContext

```rust
/// 注册上下文（on_enable 时传递，包含目录信息）
pub struct PluginRegisterContext {
    pub launcher_version: String,   // 启动器版本
    pub data_dir: PathBuf,          // 数据目录
    pub cache_dir: PathBuf,         // 缓存目录
    pub config_dir: PathBuf,        // 配置目录
}
```

---

### 7.5 PluginManager

**导入：**

```rust
use starcloud_minecraft::plugin::PluginManager;
```

**构造函数：**

```rust
pub fn new(data_dir: impl Into<PathBuf>) -> Self
```

内部创建 `{data_dir}/plugins/`、`{data_dir}/config/`、`{data_dir}/cache/` 目录结构。

**方法：**

```rust
/// 获取插件目录路径
pub fn plugins_dir(&self) -> &Path

/// 注册编译时嵌入的静态插件，调用 on_enable
pub fn register_static(&self, plugin: Box<dyn Plugin>) -> Result<()>

/// 从动态库文件加载插件，调用 on_enable
/// # Safety: 加载并执行外部代码，请确保插件来自可信来源
pub fn load_plugin(&self, path: impl AsRef<Path>) -> Result<()>

/// 卸载插件，调用 on_disable 并释放动态库
pub fn unload_plugin(&self, plugin_id: &str) -> Result<()>

/// 列出所有已加载插件的元信息
pub fn list_plugins(&self) -> Result<Vec<PluginMeta>>

/// 向所有已启用插件广播事件（容忍单个插件失败）
pub fn fire_event(&self, stage: PluginStage, ctx: &mut PluginContext) -> Result<()>

/// 按 ID 获取插件实例（用于插件间通信）
pub fn get_plugin(&self, plugin_id: &str) -> Option<Arc<Box<dyn Plugin>>>

/// 获取注册上下文
pub fn register_context(&self) -> &PluginRegisterContext
```

**示例：**

```rust
let mut pm = PluginManager::new("./launcher_data");

// 注册静态插件
pm.register_static(Box::new(MyPlugin))?;

// 加载动态库插件
pm.load_plugin("./plugins/advanced_plugin.dll")?;

// 触发事件
let mut ctx = PluginContext::new(".minecraft");
ctx.version_id = Some("1.21".into());
pm.fire_event(PluginStage::PreLaunch, &mut ctx)?;

// 列出插件
for meta in pm.list_plugins()? {
    println!("[{}] {} v{} by {}", meta.id, meta.name, meta.version, meta.author);
}

// 卸载
pm.unload_plugin("com.example.myplugin")?;
```

---

### 7.5 PluginInstaller

**导入：**

```rust
use starcloud_minecraft::plugin::{PluginInstaller, PluginSource};
```

**构造函数：**

```rust
pub fn new(plugins_dir: impl Into<PathBuf>) -> Self
```

**PluginSource 枚举（安装源）：**

```rust
pub enum PluginSource {
    Local(PathBuf),                     // 本地文件路径
    Remote(String),                     // 远程下载 URL
    Registry { plugin_id: String, registry_url: String }, // 从注册表查找
}
```

**方法：**

```rust
/// 根据来源类型执行安装，返回安装后的文件路径
pub async fn install(&self, source: &PluginSource) -> Result<PathBuf>

/// 列出已安装的插件文件（仅 .dll/.so/.dylib）
pub async fn list_installed(&self) -> Result<Vec<PathBuf>>

/// 按路径删除插件文件
pub async fn uninstall(&self, path: &Path) -> Result<()>

/// 按插件 ID 卸载（匹配文件名片段）
pub async fn uninstall_by_id(&self, plugin_id: &str) -> Result<()>
```

**示例：**

```rust
let installer = PluginInstaller::new("./plugins");

// 本地文件
let path = installer.install(&PluginSource::Local(
    PathBuf::from("downloads/my_plugin.dll")
)).await?;

// 远程下载
let path = installer.install(&PluginSource::Remote(
    "https://example.com/plugins/optifine.dll".into()
)).await?;

// 注册表
let path = installer.install(&PluginSource::Registry {
    plugin_id: "com.example.hello".into(),
    registry_url: "https://plugins.example.com/api".into(),
}).await?;

// 管理
let files = installer.list_installed().await?;
installer.uninstall_by_id("com.example.hello").await?;
```

---

### 7.7 动态库插件开发

**Cargo.toml：**

```toml
[lib]
crate-type = ["cdylib"]

[dependencies]
starcloud-minecraft = "0.1"
```

**插件代码：**

```rust
use starcloud_minecraft::plugin::{
    Plugin, PluginMeta, PluginRegisterContext,
    PluginContext, PluginStage,
};

struct HelloPlugin;

impl Plugin for HelloPlugin {
    fn meta(&self) -> PluginMeta {
        PluginMeta {
            id: "com.example.hello".into(),
            name: "Hello World".into(),
            version: "1.0.0".into(),
            author: "Developer".into(),
            description: "A demo plugin".into(),
            dependencies: vec![],
            min_launcher_version: "0.1.0".into(),
        }
    }

    fn on_enable(&self, ctx: &PluginRegisterContext) -> starcloud_minecraft::Result<()> {
        log::info!("Hello Plugin v1.0.0 已启用");
        Ok(())
    }

    fn on_event(&self, stage: PluginStage, ctx: &mut PluginContext) -> starcloud_minecraft::Result<()> {
        match stage {
            PluginStage::PreLaunch => {
                ctx.extra.insert("plugin_hello".into(), "invoked".into());
            }
            _ => {}
        }
        Ok(())
    }
}

// 导出入口（名称必须为 _plugin_create）
#[no_mangle]
pub unsafe extern "C" fn _plugin_create() -> *mut dyn Plugin {
    let plugin = Box::new(HelloPlugin);
    Box::into_raw(plugin)
}
```

---

## 8. 配置管理 (config)

### 8.1 LauncherConfig

**导入：**

```rust
use starcloud_minecraft::config::LauncherConfig;
```

**完整字段：**

```rust
pub struct LauncherConfig {
    pub config_version: u32,              // 配置版本号（用于迁移）
    pub dot_minecraft: PathBuf,           // .minecraft 目录
    pub language: String,                 // 语言代码（如 "zh_CN"）
    pub default_min_memory_mb: u32,       // 默认最小内存
    pub default_max_memory_mb: u32,       // 默认最大内存
    pub default_window_width: u32,        // 默认窗口宽度
    pub default_window_height: u32,       // 默认窗口高度
    pub java_executable: Option<PathBuf>, // 手动指定 Java
    pub extra_jvm_args: Vec<String>,      // 全局 JVM 参数
    pub download_concurrency: usize,      // 下载并发数（默认 8）
    pub download_max_retries: u32,        // 下载最大重试次数（默认 3）
    pub max_cache_entries: usize,         // 内存缓存条目数（默认 128）
    pub auto_update: bool,                // 是否自动更新
    pub login_method: String,             // 登录方式 "offline" / "mojang" / "microsoft"
    pub last_username: Option<String>,    // 上次登录用户名
    pub plugins_dir: PathBuf,             // 插件安装目录
    pub experimental: bool,               // 是否启用实验性功能
}
```

**方法：**

```rust
/// 从 TOML 文件加载（文件不存在时返回默认配置）
pub async fn load(path: impl Into<PathBuf>) -> Result<Self>

/// 保存到 TOML 文件（自动创建父目录）
pub async fn save(&self, path: impl Into<PathBuf>) -> Result<()>

/// 重置为默认值
pub fn reset(&mut self)

/// 迁移旧版本配置到当前版本
pub fn migrate(&mut self) -> Result<()>
```

**示例：**

```rust
let mut config = LauncherConfig::load("launcher.toml").await?;
config.download_concurrency = 16;
config.auto_update = true;
config.save("launcher.toml").await?;
```

---

### 8.2 LaunchProfile & ProfileManager

**LaunchProfile 结构：**

```rust
pub struct LaunchProfile {
    pub name: String,                        // 配置名称
    pub version_id: String,                  // MC 版本
    pub username: String,                    // 玩家名
    pub uuid: String,                        // UUID
    pub min_memory_mb: u32,                  // 最小内存
    pub max_memory_mb: u32,                  // 最大内存
    pub window_width: u32,                   // 窗口宽度
    pub window_height: u32,                  // 窗口高度
    pub extra_jvm_args: Vec<String>,         // 自定义 JVM 参数
    pub isolated_mods: bool,                 // 是否隔离模组目录
    pub custom_game_dir: Option<PathBuf>,    // 自定义游戏目录
}
```

**方法：**

```rust
pub fn new(name: impl Into<String>, version_id: impl Into<String>, username: impl Into<String>) -> Self
pub fn to_launch_config(&self, dot_minecraft: &PathBuf) -> LaunchConfig
```

**ProfileManager 方法：**

```rust
pub fn new(config_dir: impl Into<PathBuf>) -> Self
pub async fn load(&mut self) -> Result<()>
pub async fn save(&self) -> Result<()>
pub fn add(&mut self, profile: LaunchProfile)
pub fn remove(&mut self, name: &str) -> bool
pub fn list(&self) -> &[LaunchProfile]
pub fn find(&self, name: &str) -> Option<&LaunchProfile>
pub fn find_mut(&mut self, name: &str) -> Option<&mut LaunchProfile>
```

**示例：**

```rust
let mut mgr = ProfileManager::new("./config");
mgr.add(LaunchProfile::new("Survival", "1.20.1", "Player"));

if let Some(profile) = mgr.find("Survival") {
    let launch_cfg = profile.to_launch_config(&dot_minecraft);
}
mgr.save().await?;
```

---

## 9. 账户管理 (account)

### 9.1 OfflineAccount

**导入：**

```rust
use starcloud_minecraft::account::OfflineAccount;
```

**构造函数：**

```rust
/// 创建离线账户
/// 用户名规则：1-32 字符，仅字母、数字、下划线
/// UUID 基于 "OfflinePlayer:{username}" 的 MD5 确定性生成（Mojang 格式 v3 UUID）
pub fn new(username: impl Into<String>) -> Result<Self>
```

**方法：**

```rust
/// 获取启动凭据
pub fn credentials(&self) -> AccountCredentials
```

**返回 AccountCredentials：**

```rust
pub struct AccountCredentials {
    pub uuid: String,            // 离线 UUID
    pub username: String,        // 用户名
    pub access_token: String,    // 离线固定为 "0"
    pub user_type: String,       // "mojang"
    pub xuid: Option<String>,    // 离线为 None
    pub client_id: Option<String>, // 离线为 None
}
```

**示例：**

```rust
let account = OfflineAccount::new("Player")?;
let cred = account.credentials();

let config = LaunchConfig::basic("1.21", ".minecraft", &cred.username)
    .with_account(cred.uuid, cred.access_token, cred.user_type);
```

---

### 9.2 MicrosoftAuth

**导入：**

```rust
use starcloud_minecraft::account::MicrosoftAuth;
```

微软 OAuth 2.0 设备码流程，完整 5 步认证。

**构造函数：**

```rust
pub fn new() -> Self                         // 使用默认 Minecraft Launcher Client ID
pub fn with_client_id(self, id: impl Into<String>) -> Self  // 使用自定义 Azure Client ID
```

**分步认证方法：**

```rust
/// 步骤 1：获取设备码（返回 user_code、verification_uri 等）
pub async fn get_device_code(&self) -> Result<DeviceCodeResponse>

/// 步骤 2：轮询获取 Token（每 interval 秒调用一次）
pub async fn poll_token(&self, device_code: &str) -> Result<TokenResponse>

/// 步骤 3：用微软 Token 换 Xbox Live Token
pub async fn auth_xbox_live(&self, access_token: &str) -> Result<XblAuthResponse>

/// 步骤 4：用 Xbox Live Token 换 XSTS Token
pub async fn auth_xsts(&self, xbl_token: &str) -> Result<XstsAuthResponse>

/// 步骤 5：用 XSTS Token 获取 Minecraft Token
pub async fn auth_minecraft(&self, uhs: &str, xsts_token: &str) -> Result<MinecraftAuthResponse>

/// 获取玩家档案（需要 Minecraft Token）
pub async fn get_profile(&self, mc_token: &str) -> Result<MinecraftProfile>
```

**一步完成方法：**

```rust
/// 自动轮询完成完整登录
/// poll_interval_secs: 轮询间隔
/// max_polls: 最大轮询次数（超时退出）
pub async fn full_login(
    &self,
    device_code: &str,
    poll_interval_secs: u64,
    max_polls: u32,
) -> Result<MicrosoftLoginResult>
```

**返回 MicrosoftLoginResult：**

```rust
pub struct MicrosoftLoginResult {
    pub access_token: String,    // Minecraft Bearer Token
    pub refresh_token: Option<String>,
    pub uuid: String,            // 玩家 UUID
    pub username: String,        // 玩家名
    pub xuid: String,            // Xbox User ID
    pub uhs: String,             // User Hash
}
```

**完整登录示例：**

```rust
let auth = MicrosoftAuth::new();

// 步骤 1-2：获取设备码
let device = auth.get_device_code().await?;
println!("请在浏览器中打开: {}", device.verification_uri);
println!("输入代码: {}", device.user_code);
println!("然后按回车继续...");
std::io::stdin().read_line(&mut String::new())?;

// 步骤 3-6：自动完成
match auth.full_login(&device.device_code, device.interval, 120).await {
    Ok(result) => {
        println!("登录成功: {} ({})", result.username, result.uuid);
        let config = LaunchConfig::basic("1.21", ".minecraft", &result.username)
            .with_account(result.uuid, result.access_token, "msa");
    }
    Err(e) => eprintln!("登录失败: {}", e),
}
```

> **注意**：`MicrosoftAuth` 无 `Clone` 派生（TCP 连接非 Clone），每次使用需保持同一实例或创建新的。

---

### 9.3 MojangAuth

**导入：**

```rust
use starcloud_minecraft::account::MojangAuth;
```

旧版 Mojang 账户认证 API（已逐步被微软账户取代，但仍可用）。

**构造函数：**

```rust
pub fn new() -> Self
```

**方法：**

```rust
/// 密码登录
pub async fn login(&self, email: &str, password: &str, client_token: Option<&str>) -> Result<MojangAuthResponse>

/// 刷新令牌
pub async fn refresh(&self, access_token: &str, client_token: &str) -> Result<MojangAuthResponse>

/// 验证令牌有效性（true=有效）
pub async fn validate(&self, access_token: &str, client_token: &str) -> Result<bool>

/// 注销令牌
pub async fn signout(&self, access_token: &str, client_token: &str) -> Result<()>
```

**返回 MojangAuthResponse：**

```rust
pub struct MojangAuthResponse {
    pub access_token: String,
    pub client_token: String,
    pub selected_profile: SelectedProfile,
    pub available_profiles: Option<Vec<SelectedProfile>>,
}

pub struct SelectedProfile {
    pub id: String,    // UUID
    pub name: String,  // 玩家名
}
```

---

## 10. ModLoader 支持 (modloader)

### 10.1 ModLoaderType

```rust
pub enum ModLoaderType {
    Forge,
    Fabric,
    Quilt,
    NeoForge,
    Custom(String),
}

impl Display for ModLoaderType {
    // "forge" / "fabric" / "quilt" / "neoforge" / custom string
}
```

### 10.2 Forge

```rust
use starcloud_minecraft::modloader::forge;

/// 获取某 MC 版本的推荐 Forge 版本
pub async fn get_recommended_version(mc_version: &str) -> Result<String>

/// 构建 Forge 安装目录路径
pub fn install_dir(dot_minecraft: &PathBuf, mc_version: &str, forge_version: &str) -> PathBuf
```

### 10.3 Fabric

```rust
use starcloud_minecraft::modloader::fabric;

/// 获取最新稳定版 Fabric Loader（无稳定版则取最新）
pub async fn get_latest_loader(mc_version: &str) -> Result<String>

/// 获取 Fabric 安装 profile JSON（含所有 libraries 定义）
pub async fn get_profile_json(mc_version: &str, loader_version: &str) -> Result<serde_json::Value>

/// 构建 Fabric 安装目录路径
pub fn install_dir(dot_minecraft: &PathBuf, mc_version: &str, loader_version: &str) -> PathBuf
```

### 10.4 Quilt

```rust
use starcloud_minecraft::modloader::quilt;

/// 获取最新 Quilt Loader
pub async fn get_latest_loader(mc_version: &str) -> Result<String>

/// 构建 Quilt 安装目录路径
pub fn install_dir(dot_minecraft: &PathBuf, mc_version: &str, loader_version: &str) -> PathBuf
```

---

## 11. 服务层 (core)

服务层将底层模块组织为高层异步服务，供 CLI 与 GUI 共用（`starcloud_minecraft::core`）。

### 11.1 InstallService — 版本安装服务

```rust
use starcloud_minecraft::core::InstallService;

let svc = InstallService::new(config); // config: LauncherConfig

/// 关联插件管理器（版本安装事件广播），可选
pub fn with_plugin_manager(self, plugin_manager: Arc<PluginManager>) -> Self

/// 完整安装版本：清单 → 详情 → 客户端 jar → 库 → 资源 → 索引 → version.json
pub async fn install_version(
    &self,
    version_id: &str,
    progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
) -> Result<()>

/// 安装 Fabric 加载器（内联合并 inheritsFrom 父版本），返回 fabric 版本 ID
/// 产物 ID 形如 "fabric-loader-{loader}-{mc}"
pub async fn install_fabric(
    &self,
    mc_version: &str,
    progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
) -> Result<String>

/// 仅下载客户端 jar（启动前 EnsureInstalled 使用）
pub async fn ensure_client_jar(&self, details: &VersionDetails) -> Result<()>

/// 确保 log4j2 日志配置就绪
pub async fn ensure_logging_config(&self, details: &VersionDetails) -> Result<()>
```

`progress_cb` 接收节流后的 `ProgressInfo` 快照（终态强制触发），可转交给 `ProgressTracker` 显示。

### 11.2 LaunchService — 启动服务

```rust
use starcloud_minecraft::core::LaunchService;

pub async fn launch(
    &self,
    version_id: &str,
    username: &str,
    launch_options: Option<...>,
    dry_run: bool,
    progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
    log_cb: Option<impl Fn(&str, bool)>,   // 输出行回调 (line, is_stderr)
    pid_cb: Option<impl Fn(u32)>,          // 子进程 PID 回调
) -> Result<LaunchOutcome>

pub struct LaunchOutcome {
    pub exit_code: Option<i32>,
    pub crashed: bool,     // 退出码非 0 或异常退出视为崩溃
}
```

流程：读取版本详情 → 确保已安装 → 账户凭据解析（微软 refresh 续期，否则离线）→ Java 检测 → 参数构建 → 启动 → 输出流转发 → 退出判定。

### 11.3 AccountService — 账户服务

```rust
use starcloud_minecraft::core::{AccountService, MicrosoftLoginPoll};

pub async fn list() -> Result<Vec<StoredAccount>>
pub async fn add_offline(&self, username: &str) -> Result<StoredAccount>
pub async fn remove(&self, uuid: &str) -> Result<bool>
pub async fn set_default(&self, uuid: &str) -> Result<()>

/// 发起微软设备码登录，返回 DeviceCodeResponse（user_code / verification_uri / interval …）
pub async fn start_microsoft_login(&self) -> Result<DeviceCodeResponse>

/// 单次轮询设备码登录状态（前端按 interval 循环调用直至终态）
pub async fn poll_microsoft_login(&self, device_code: &str) -> Result<MicrosoftLoginPoll>

pub enum MicrosoftLoginPoll {
    Pending(String), // 用户未完成授权，应稍后继续轮询
    Success(StoredAccount),
}
```

登录成功后凭证持久化到 `accounts.json`，`access_token` 过期时由 `resolve_credentials` 自动续期。

### 11.4 使用示例

```rust
use starcloud_minecraft::core::InstallService;

let svc = InstallService::new(config);
let fabric_id = svc
    .install_fabric("1.21.4", |p| println!("{} {:.1}%", p.stage, p.percentage))
    .await?;
println!("安装完成: {}", fabric_id);
```

---

## 12. 开发规范与最佳实践

### 12.1 错误处理规范

```rust
// ✅ 正确：使用 ? 传播错误，在顶层处理
async fn launch_flow() -> Result<()> {
    let manifest = fetch_version_manifest().await?; // ? 自动传播
    Ok(())
}

// ✅ 正确：在 UI 层做类型化匹配
fn handle_error(e: &LauncherError) -> String {
    match e {
        LauncherError::JavaNotFound(v) => format!("请安装 Java {} 或更高版本", v),
        LauncherError::DownloadError(m) => format!("下载失败: {}", m),
        _ => e.to_string(),
    }
}

// ❌ 错误：bare unwrap()
let manifest = fetch_version_manifest().await.unwrap();
```

### 12.2 异步运行时

所有网络和文件 I/O 操作基于 `tokio`，必须在 `#[tokio::main]` 或 `tokio::runtime::Runtime` 上下文中调用。

```rust
#[tokio::main]
async fn main() { /* ... */ }
```

### 12.3 启动前必须校验

```rust
// ✅ 启动前执行配置校验
VersionValidator::validate_launch_config(&config)?;
VersionValidator::validate_version_details(&details)?;
let child = launcher.launch(&config, &details, &libs)?;
```

### 12.4 缓存策略

| 场景 | 策略 |
|------|------|
| 版本清单 | `VersionCache::get_manifest`（LRU + 磁盘） |
| 版本详情 | `VersionCache::get_version_details`（LRU + 磁盘） |
| 资源文件 | 存在跳过，不重复下载 |
| 库文件 | SHA1 校验 + 存在跳过 |

### 12.5 下载优化参数推荐

```rust
let engine = DownloadEngine::new(".minecraft");

// 资源下载：IO 密集型，高并发
let assets = engine.asset_downloader().concurrency(16);

// 库文件下载：文件较大，带宽敏感，中等并发
let libs = engine.library_downloader().concurrency(6);

// HTTP 客户端：启用所有优化
let client = HttpClient::new()?;
```

### 12.6 并发安全

- `PluginManager` 内部使用 `RwLock<HashMap>`，可安全在多线程间共享
- `ProgressTracker` 基于 `AtomicU64`，完全无锁
- `VersionCache` 内部 `Arc<Mutex<LruCache>>`
- `LaunchEngine`、`DownloadEngine` 无内部状态锁，可 `Clone` 安全共享

### 12.7 文件路径

所有路径接受 `impl Into<PathBuf>`，支持跨平台：

```rust
DownloadEngine::new("/home/user/.minecraft")     // Unix
DownloadEngine::new(r"C:\Users\user\.minecraft") // Windows
DownloadEngine::new(PathBuf::from("."))           // 相对路径
```

### 12.8 日志

库内部使用 `log` crate（`info!`/`debug!`/`warn!`），上层需配置日志后端：

```rust
env_logger::init(); // 或 tracing-subscriber / simple_logger
```

### 12.9 版本号命名

- 正式版：`"1.20.1"` `"1.21"`
- 快照版：`"24w21b"` `"1.21-pre2"`
- Forge 版：`"forge-1.20.1-47.2.0"`
- Fabric 版：`"fabric-loader-0.15.11-1.20.1"`

`VersionComparator` 对以上格式均可比较。

### 12.10 线程模型建议

```
main thread:
  ├── tokio runtime (async I/O)
  │   ├── fetch_version_manifest()
  │   ├── AssetDownloader::download_assets()  ── spawn N tasks
  │   └── LibraryDownloader::download_libraries() ── spawn N tasks
  │
  └── std::process::Child (minecraft process)
      └── 独立 OS 进程，不占用 tokio 线程
```
