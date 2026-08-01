//! # Furry Minecraft Launcher Core
//!
//! Minecraft 启动器核心模块，提供下载引擎、启动引擎和插件系统。
//!
//! ## 模块概览
//!
//! - **[download]** - 下载引擎：版本清单获取、资源文件下载、库文件下载、进度追踪、连接池优化
//! - **[launch]** - 启动引擎：Java 检测、JVM 参数构建、进程管理
//! - **[plugin]** - 插件系统：插件 API、动态加载、插件管理、安装
//! - **[version]** - 版本数据结构：Mojang 版本清单 JSON 的 Rust 类型定义
//! - **[version_manager]** - 版本管理：缓存、比对、安装/删除、校验
//! - **[config]** - 配置管理：启动器全局配置、启动配置集
//! - **[account]** - 账户管理：离线、Mojang、微软 OAuth2 认证
//! - **[modloader]** - ModLoader 支持：Forge、Fabric、Quilt

pub mod account;
pub mod config;
pub mod download;
pub mod error;
pub mod launch;
pub mod logging;
pub mod modloader;
pub mod plugin;
pub mod version;
pub mod version_manager;

// 重新导出常用类型
pub use account::{MicrosoftAuth, MojangAuth, OfflineAccount};
pub use config::{LaunchProfile, LauncherConfig, ProfileManager};
pub use download::DownloadEngine;
pub use error::{LauncherError, Result};
pub use launch::{LaunchConfig, LaunchEngine};
pub use modloader::ModLoaderInfo;
pub use plugin::{PluginInstaller, PluginManager};
pub use version_manager::{VersionCache, VersionComparator, VersionInstaller, VersionValidator};
