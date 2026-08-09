//! 服务层 - CLI 与 GUI 共用的高层业务封装
//!
//! 将下载/启动/账户等底层模块组织为可复用的异步服务：
//! - [`InstallService`]：完整版本安装流程（清单→详情→jar→库→资源→索引→version.json）
//! - [`LaunchService`]：确保安装 → 校验 → 启动 → 输出流转发 → 退出码/崩溃判定
//! - [`AccountService`]：账户持久化（accounts.json）与微软登录/续期

pub mod account;
pub mod install;
pub mod launch;

pub use account::{AccountService, MicrosoftLoginPoll};
pub use install::InstallService;
pub use launch::{LaunchOutcome, LaunchService};
