use clap::{Parser, Subcommand};
use colored::*;
use furry_minecraft::core::{AccountService, InstallService, LaunchService};
use furry_minecraft::{
    config::{LaunchProfile, LauncherConfig},
    download::{progress::ProgressInfo, DownloadEngine, ProgressStatus},
    plugin::PluginInstaller,
    version_manager::{VersionComparator, VersionInstaller},
    Result,
};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};

// ============================================================
// CLI 定义
// ============================================================

/// Minecraft 启动器核心
#[derive(Parser)]
#[command(name = "furry-minecraft", version, about = "Minecraft 启动器核心", long_about = None)]
struct Cli {
    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand)]
enum Commands {
    /// 安装 Minecraft 版本（jar+库+资源）
    Install {
        /// 目标版本 ID（如 "1.21"）
        version: String,

        /// .minecraft 目录路径
        #[arg(short, long, default_value = ".minecraft")]
        dir: String,
    },
    /// 启动 Minecraft
    Launch {
        /// 版本 ID
        version: String,

        /// 玩家用户名
        #[arg(short, long, default_value = "Player")]
        username: String,

        /// 最小内存 (MB)
        #[arg(long, default_value = "2048")]
        min_memory: u32,

        /// 最大内存 (MB)
        #[arg(long, default_value = "4096")]
        max_memory: u32,

        /// 窗口宽度
        #[arg(long, default_value = "854")]
        width: u32,

        /// 窗口高度
        #[arg(long, default_value = "480")]
        height: u32,

        /// .minecraft 目录路径
        #[arg(short, long, default_value = ".minecraft")]
        dir: String,

        /// 仅输出启动命令，不实际启动
        #[arg(short, long)]
        dry_run: bool,

        /// Java 路径（留空自动检测）
        #[arg(long)]
        java: Option<String>,
    },
    /// 列出所有已安装版本
    List {
        /// .minecraft 目录路径
        #[arg(short, long, default_value = ".minecraft")]
        dir: String,
    },
    /// 删除已安装版本
    Remove {
        /// 版本 ID
        version: String,

        /// .minecraft 目录路径
        #[arg(short, long, default_value = ".minecraft")]
        dir: String,
    },
    /// 显示远程可用版本
    Versions {
        /// 最多显示 N 条
        #[arg(short, long, default_value = "20")]
        count: usize,

        /// 包含快照版
        #[arg(short, long)]
        snapshots: bool,
    },
    /// 创建离线账户并保存
    Account {
        /// 玩家用户名
        username: String,
    },
    /// 启动器配置管理
    Config {
        #[arg(short, long, default_value = "launcher.toml")]
        path: String,

        /// 显示当前配置
        #[arg(short, long)]
        show: bool,

        /// 重置为默认
        #[arg(long)]
        reset: bool,
    },
    /// 插件管理
    Plugins {
        /// 插件目录路径
        #[arg(short, long, default_value = "plugins")]
        dir: String,
    },
    /// 检查 Java 环境
    Check,
}

// ============================================================
// 主函数
// ============================================================

#[tokio::main]
async fn main() {
    let cli = Cli::parse();

    // 初始化日志（失败不阻断 CLI，仅打印警告）
    if let Err(e) =
        furry_minecraft::logging::init_logging(furry_minecraft::logging::default_log_dir(), "info")
    {
        eprintln!("警告: 日志初始化失败: {}", e);
    }

    let result = match cli.command {
        Commands::Install { version, dir } => cmd_install(&version, &dir).await,
        Commands::Launch {
            version,
            username,
            min_memory,
            max_memory,
            width,
            height,
            dir,
            dry_run,
            java,
        } => {
            cmd_launch(
                &version,
                &username,
                min_memory,
                max_memory,
                width,
                height,
                &dir,
                dry_run,
                java.as_deref(),
            )
            .await
        }
        Commands::List { dir } => cmd_list(&dir).await,
        Commands::Remove { version, dir } => cmd_remove(&version, &dir).await,
        Commands::Versions { count, snapshots } => cmd_versions(count, snapshots).await,
        Commands::Account { username } => cmd_account(&username).await,
        Commands::Config { path, show, reset } => cmd_config(&path, show, reset).await,
        Commands::Plugins { dir } => cmd_plugins(&dir).await,
        Commands::Check => cmd_check().await,
    };

    if let Err(e) = result {
        eprintln!("{} {}", "错误:".red().bold(), e);
        std::process::exit(1);
    }
}

// ============================================================
// 公共辅助
// ============================================================

/// 加载配置（默认 launcher.toml，缺失用默认值），迁移到最新版本后覆盖游戏目录
async fn load_config(dir: &str) -> Result<LauncherConfig> {
    let mut config = LauncherConfig::load("launcher.toml").await?;
    config.migrate()?;
    config.dot_minecraft = PathBuf::from(dir);
    Ok(config)
}

// ============================================================
// 命令实现
// ============================================================

/// install 命令 - 完整安装（清单→详情→jar→库→资源→索引→version.json）
async fn cmd_install(version: &str, dir: &str) -> Result<()> {
    println!(
        "{} 开始安装 Minecraft {}",
        "▶".green().bold(),
        version.cyan()
    );

    let config = load_config(dir).await?;
    let svc = InstallService::new(config);

    // 进度显示：阶段切换时打印一行，完成/失败打印状态
    let last_stage = Arc::new(Mutex::new(String::new()));
    let last_stage_cb = last_stage.clone();
    let version_owned = version.to_string();
    svc.install_version(version, move |info: &ProgressInfo| {
        let mut last = last_stage_cb.lock().unwrap();
        if *last != info.stage {
            *last = info.stage.clone();
            println!("  · {}", info.stage);
        }
        match &info.status {
            ProgressStatus::Completed => println!("    ✓ 完成"),
            ProgressStatus::Failed(e) => eprintln!("    ✗ {}", e),
            _ => {}
        }
    })
    .await?;

    println!(
        "{} Minecraft {} 安装完成！",
        "✓".green().bold(),
        version_owned.cyan().bold()
    );
    Ok(())
}

/// launch 命令 - 走服务层（确保安装→配置→校验→启动→输出流转发→退出判定）
async fn cmd_launch(
    version: &str,
    username: &str,
    min_mem: u32,
    max_mem: u32,
    width: u32,
    height: u32,
    dir: &str,
    dry_run: bool,
    java_path: Option<&str>,
) -> Result<()> {
    let mut config = load_config(dir).await?;
    if let Some(java) = java_path {
        config.java_executable = Some(PathBuf::from(java));
    }
    let svc = LaunchService::new(config);

    let mut profile = LaunchProfile::new("cli", version, username);
    profile.min_memory_mb = min_mem;
    profile.max_memory_mb = max_mem;
    profile.window_width = width;
    profile.window_height = height;

    if dry_run {
        let cmd = svc
            .dry_run_command(version, username, Some(&profile), |_| {})
            .await?;
        println!("\n{}", cmd);
        return Ok(());
    }

    println!(
        "{} 启动 Minecraft {} ...",
        "▶".green().bold(),
        version.cyan().bold()
    );
    let outcome = svc
        .launch(
            version,
            username,
            Some(&profile),
            false,
            |_| {},
            Some(|line: &str, is_err| {
                if is_err {
                    eprintln!("{}", line.dimmed());
                } else {
                    println!("{}", line);
                }
            }),
        )
        .await?;

    match outcome.exit_code {
        Some(0) => println!("\n{} Minecraft 已正常退出", "✓".green()),
        Some(code) => eprintln!("\n{} 游戏异常退出 (退出码: {})", "✗".red(), code),
        None => eprintln!("\n{} 进程被信号终止", "✗".red()),
    }
    Ok(())
}

/// list 命令
async fn cmd_list(dir: &str) -> Result<()> {
    let installer = VersionInstaller::new(DownloadEngine::new(dir));

    let versions = installer.list_installed().await?;
    if versions.is_empty() {
        println!(
            "{} 没有已安装的版本。使用 {} 安装。",
            "⊙".yellow(),
            "furry-minecraft install <版本号>".cyan()
        );
        return Ok(());
    }

    println!("已安装版本 ({} 个):", versions.len().to_string().bold());
    for id in &versions {
        let size = installer.version_size(id).await?;
        let size_str = if size > 1_073_741_824 {
            format!("{:.1} GB", size as f64 / 1_073_741_824.0)
        } else {
            format!("{:.1} MB", size as f64 / 1_048_576.0)
        };
        let tag = if VersionComparator::is_snapshot(id) {
            " [快照]"
        } else {
            ""
        };
        println!("    {}  {}{}", id.green(), size_str.dimmed(), tag.dimmed());
    }
    Ok(())
}

/// remove 命令
async fn cmd_remove(version: &str, dir: &str) -> Result<()> {
    let installer = VersionInstaller::new(DownloadEngine::new(dir));

    if !installer.is_installed(version) {
        println!("{} 版本 {} 未安装", "⊙".yellow(), version.cyan());
        return Ok(());
    }

    installer.delete_version(version).await?;
    println!("{} 已删除版本 {}", "✓".green(), version.cyan());
    Ok(())
}

/// versions 命令
async fn cmd_versions(count: usize, snapshots: bool) -> Result<()> {
    println!("获取版本清单...");
    let manifest = furry_minecraft::download::fetch_version_manifest().await?;

    let filtered: Vec<_> = manifest
        .versions
        .iter()
        .filter(|v| snapshots || VersionComparator::is_release(&v.id))
        .take(count)
        .collect();

    println!("\n最近 {} 个版本:\n", filtered.len());
    for v in &filtered {
        let icon = match v.version_type {
            furry_minecraft::version::manifest::VersionType::Release => "R",
            _ => "S",
        };
        let time = &v.release_time[..10.min(v.release_time.len())];
        println!("  [{}]  {}  {}", icon, v.id.green(), time.dimmed());
    }

    if !snapshots && !manifest.versions.is_empty() {
        println!(
            "\n提示: 使用 {} 查看包含快照版的完整列表",
            "furry-minecraft versions -s".dimmed()
        );
    }
    Ok(())
}

/// account 命令 - 创建离线账户并持久化（首个账户自动设为默认）
async fn cmd_account(username: &str) -> Result<()> {
    let svc = AccountService::new();
    let acc = svc.add_offline(username).await?;
    println!("{} 离线账户已创建:", "✓".green());
    println!("  用户名: {}", acc.username.green().bold());
    println!("  UUID:   {}", acc.uuid.dimmed());
    println!("  类型:   {}", "mojang (离线)".dimmed());
    Ok(())
}

/// config 命令
async fn cmd_config(path: &str, show: bool, reset: bool) -> Result<()> {
    if reset {
        let config = LauncherConfig::default();
        config.save(path).await?;
        println!("{} 配置已重置为默认值", "✓".green());
        return Ok(());
    }

    let mut config = LauncherConfig::load(path).await?;
    config.migrate()?;

    if show {
        println!("当前配置 (v{}) ({})\n", config.config_version, path.cyan());
        println!("  .minecraft:        {}", config.dot_minecraft.display());
        println!("  语言:             {}", config.language.cyan());
        println!(
            "  默认内存:         {} ~ {} MB",
            config.default_min_memory_mb, config.default_max_memory_mb
        );
        println!(
            "  默认窗口:         {}x{}",
            config.default_window_width, config.default_window_height
        );
        println!("  下载并发:         {}", config.download_concurrency);
        println!("  下载重试:         {} 次", config.download_max_retries);
        println!("  最大缓存:         {} 条目", config.max_cache_entries);
        println!("  登录方式:         {}", config.login_method.cyan());
        println!("  插件目录:         {}", config.plugins_dir.display());
        if let Some(ref java) = config.java_executable {
            println!("  Java 路径:        {}", java.display());
        } else {
            println!("  Java 路径:        {} (自动检测)", "自动".dimmed());
        }
        if config.extra_jvm_args.is_empty() {
            println!("  额外 JVM 参数:    (无)");
        } else {
            println!("  额外 JVM 参数:    {}", config.extra_jvm_args.join(" "));
        }
        println!(
            "  自动更新:         {}",
            if config.auto_update {
                "是".green()
            } else {
                "否".dimmed()
            }
        );
        if let Some(ref user) = config.last_username {
            println!("  上次用户:         {}", user.green());
        }
        println!(
            "  实验性功能:       {}",
            if config.experimental {
                "是".green()
            } else {
                "否".dimmed()
            }
        );
        return Ok(());
    }

    config.migrate()?;
    config.save(path).await?;
    println!("{} 配置已保存到 {}", "✓".green(), path.cyan());
    Ok(())
}

/// plugins 命令 - 展示安装清单（插件 ID/版本），兼容无清单的旧插件
async fn cmd_plugins(dir: &str) -> Result<()> {
    let installer = PluginInstaller::new(dir);

    let manifest = installer.manifest().await?;
    if manifest.is_empty() {
        let files = installer.list_installed().await?;
        if files.is_empty() {
            println!("{} 插件目录没有插件。", "⊙".yellow());
            println!(
                "  目录: {}",
                PathBuf::from(dir)
                    .canonicalize()
                    .unwrap_or_else(|_| PathBuf::from(dir))
                    .display()
            );
            println!("\n  安装方式:");
            println!("    1. 将 .dll/.so/.dylib 插件文件放入上述目录");
            println!("    2. 使用 install 命令从本地文件或远程 URL 安装（后续版本提供）");
            return Ok(());
        }
        // 无清单的旧插件：按文件展示
        println!("已安装插件 ({} 个，无清单):\n", files.len());
        for path in &files {
            let name = path
                .file_stem()
                .map(|s| s.to_string_lossy())
                .unwrap_or_default();
            let meta = path.metadata().ok();
            let size = meta.map(|m| m.len()).unwrap_or(0);
            println!("  • {}  ({:.1} KB)", name.cyan(), size as f64 / 1024.0);
        }
        return Ok(());
    }

    println!("已安装插件 ({} 个):\n", manifest.len());
    for entry in &manifest {
        let id = entry.plugin_id.as_deref().unwrap_or("未知 ID");
        let ver = entry.version.as_deref().unwrap_or("?");
        println!(
            "  • {}  v{}  {}",
            id.cyan(),
            ver,
            format!("文件: {}", entry.file_name).dimmed()
        );
        println!("      安装时间: {}", entry.installed_at.dimmed());
    }
    Ok(())
}

/// check 命令
async fn cmd_check() -> Result<()> {
    use furry_minecraft::launch::JavaDetector;

    println!("检测 Java 环境...\n");

    let detector = JavaDetector::new().with_min_version(8);
    match detector.detect() {
        Ok(java) => {
            println!("  {} Java 已找到:", "✓".green());
            println!(
                "    路径:      {}",
                java.executable.display().to_string().green()
            );
            println!(
                "    版本:      {} ({})",
                java.major_version.to_string().bold(),
                java.version_string.dimmed()
            );
            println!(
                "    64 位:     {}",
                if java.is_64bit {
                    "是".green()
                } else {
                    "否".yellow()
                }
            );
            println!(
                "    JAVA_HOME: {}",
                java.java_home.display().to_string().dimmed()
            );
        }
        Err(_) => {
            println!("  {} Java 未找到！请安装 Java 8 或更高版本。", "✗".red());
            println!("    下载: https://adoptium.net/");
        }
    }

    Ok(())
}
