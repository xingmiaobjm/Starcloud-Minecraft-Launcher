//! 加载器注册表（可扩展）
//!
//! 下载版本时必须在「原版 / Fabric / Forge / Quilt / NeoForge」中选择一种，
//! 选择结果写入版本 ID（如 `1.20.1-forge-47.2.0`），安装后不可再添加或更改加载器。
//!
//! # 扩展指南（防止出现新的加载器时无从下手）
//! 1. `LoaderKind` 增加一个变体；
//! 2. `label()` / `id()` 补充显示名与 ID；
//! 3. `latest_version()` 补充版本查询逻辑（或在 `InstallService::install_loader` 中处理）；
//! 4. `InstallService::install_loader` 的 match 增加安装分支。
//! 前端通过 `list_loaders` 命令自动读取本注册表，无需改动前端。

use crate::error::{LauncherError, Result};
use serde::Serialize;

/// 加载器类型
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LoaderKind {
    /// 原版（无加载器）
    Vanilla,
    Fabric,
    Forge,
    Quilt,
    NeoForge,
}

/// 前端可展示的加载器信息
#[derive(Debug, Clone, Serialize)]
pub struct LoaderInfo {
    pub id: String,
    pub label: String,
    pub description: String,
}

impl LoaderKind {
    /// 注册表中的全部加载器（顺序即前端展示顺序）
    pub fn all() -> &'static [LoaderKind] {
        &[
            LoaderKind::Vanilla,
            LoaderKind::Fabric,
            LoaderKind::Forge,
            LoaderKind::Quilt,
            LoaderKind::NeoForge,
        ]
    }

    /// 唯一 ID（用于命令参数 / 任务键）
    pub fn id(self) -> &'static str {
        match self {
            LoaderKind::Vanilla => "vanilla",
            LoaderKind::Fabric => "fabric",
            LoaderKind::Forge => "forge",
            LoaderKind::Quilt => "quilt",
            LoaderKind::NeoForge => "neoforge",
        }
    }

    /// 显示名
    pub fn label(self) -> &'static str {
        match self {
            LoaderKind::Vanilla => "原版",
            LoaderKind::Fabric => "Fabric",
            LoaderKind::Forge => "Forge",
            LoaderKind::Quilt => "Quilt",
            LoaderKind::NeoForge => "NeoForge",
        }
    }

    /// 一句话说明（前端弹窗展示）
    pub fn description(self) -> &'static str {
        match self {
            LoaderKind::Vanilla => "Mojang 官方原版，无任何加载器",
            LoaderKind::Fabric => "轻量、更新快，现代 Mod 生态首选",
            LoaderKind::Forge => "最老牌的加载器，经典大型 Mod 生态",
            LoaderKind::Quilt => "Fabric 的分叉，追求社区治理与扩展性",
            LoaderKind::NeoForge => "Forge 的继任者，面向新版本 Minecraft",
        }
    }

    /// 解析字符串 ID → LoaderKind
    pub fn from_id(id: &str) -> Result<LoaderKind> {
        Self::all()
            .iter()
            .copied()
            .find(|k| k.id() == id)
            .ok_or_else(|| {
                LauncherError::Other(format!("未知的加载器类型: {}", id))
            })
    }

    /// 查询该加载器对指定 MC 版本可用的最新版本号
    ///
    /// - 原版返回 MC 版本号本身；
    /// - Fabric / Quilt 返回最新 Loader；
    /// - Forge / NeoForge 返回推荐（最新）构建号。
    pub async fn latest_version(self, mc_version: &str) -> Result<String> {
        match self {
            LoaderKind::Vanilla => Ok(mc_version.to_string()),
            LoaderKind::Fabric => super::loader::fabric::get_latest_loader(mc_version).await,
            LoaderKind::Quilt => super::loader::quilt::get_latest_loader(mc_version).await,
            LoaderKind::Forge => super::loader::forge::get_recommended_version(mc_version).await,
            LoaderKind::NeoForge => super::loader::neoforge::get_latest_version(mc_version).await,
        }
    }

    /// 前端展示信息
    pub fn info(self) -> LoaderInfo {
        LoaderInfo {
            id: self.id().to_string(),
            label: self.label().to_string(),
            description: self.description().to_string(),
        }
    }
}
