//! 基础启动器示例
//!
//! 演示如何使用 starcloud-minecraft 核心库下载并启动 Minecraft。

use starcloud_minecraft::{
    download::{fetch_version_details, fetch_version_manifest, DownloadEngine},
    launch::LaunchEngine,
    LaunchConfig,
};
use std::path::PathBuf;

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    // 初始化日志
    env_logger::init();

    // 确定 .minecraft 目录
    let dot_minecraft = get_minecraft_dir();

    println!("== Starcloud Minecraft Launcher ==");
    println!(".minecraft 目录: {}", dot_minecraft.display());

    // 1. 创建下载引擎
    let download_engine = DownloadEngine::new(&dot_minecraft);

    // 2. 获取版本清单
    println!("获取版本清单...");
    let manifest = fetch_version_manifest().await?;
    println!("最新发布版: {}", manifest.latest.release);
    println!("最新快照版: {}", manifest.latest.snapshot);
    println!("可用版本总数: {}", manifest.versions.len());

    // 3. 选择一个版本（这里用最新发布版）
    let version_entry = manifest
        .versions
        .iter()
        .find(|v| &v.id == "1.20.1")
        .or_else(|| manifest.versions.first())
        .ok_or("没有可用的版本")?;

    println!("目标版本: {}", version_entry.id);

    // 4. 获取版本详情
    println!("获取版本详情...");
    let version_details = fetch_version_details(&version_entry.url).await?;

    // 5. 下载库文件
    println!("下载库文件 ({} 个)...", version_details.libraries.len());
    let library_downloader = download_engine.library_downloader();
    let library_paths = library_downloader
        .download_libraries(&version_details.libraries)
        .await?;
    println!("库文件准备完毕，classpath 条目: {}", library_paths.len());

    // 6. 下载资源文件
    if let Some(asset_index) = &version_details.asset_index {
        println!("下载资源文件...");
        let asset_downloader = download_engine.asset_downloader();
        asset_downloader.download_from_url(&asset_index.url).await?;
        println!("资源文件下载完成");
    }

    // 7. 创建启动引擎
    println!("检测 Java 运行时...");
    let launch_engine = LaunchEngine::new(download_engine)?;

    // 8. 创建启动配置
    let config = LaunchConfig::basic(&version_entry.id, &dot_minecraft, "Player")
        .with_memory(2048, 4096)
        .with_window(1280, 720);

    // 9. 输出启动命令（调试用）
    let command_line = launch_engine.print_command(&config, &version_details, &library_paths)?;
    println!("\n--- 启动命令 ---");
    println!("{}", command_line);
    println!("--- 命令结束 ---\n");

    println!("按 Enter 启动 Minecraft，或 Ctrl+C 退出...");
    let mut input = String::new();
    std::io::stdin().read_line(&mut input)?;

    // 10. 启动 Minecraft
    println!("启动 Minecraft {} ...", version_entry.id);
    let mut child = launch_engine.launch(&config, &version_details, &library_paths)?;
    println!("Minecraft 进程已启动 (PID: {})", child.id());

    let status = child.wait()?;
    println!("Minecraft 已退出，状态码: {:?}", status.code());

    Ok(())
}

fn get_minecraft_dir() -> PathBuf {
    // 默认 .minecraft 目录
    dirs::data_dir()
        .map(|p| p.join(".minecraft"))
        .unwrap_or_else(|| PathBuf::from(".minecraft"))
}
