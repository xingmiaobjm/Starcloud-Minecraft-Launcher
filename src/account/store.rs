//! 账户持久化存储（accounts.json）
//!
//! 以 JSON 文件形式保存已登录账户（含微软 refresh_token），
//! 支持 CRUD、默认账户与最后使用时间维护。写入采用原子方式
//! （临时文件 + rename），避免进程中断导致文件损坏。

use crate::error::{LauncherError, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// 账户类型
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AccountType {
    /// 离线账户
    Offline,
    /// 微软账户
    Microsoft,
    /// Mojang 账户（遗留，仅兼容读取）
    Mojang,
}

impl AccountType {
    /// 人类可读名称
    pub fn display_name(self) -> &'static str {
        match self {
            AccountType::Offline => "离线",
            AccountType::Microsoft => "微软",
            AccountType::Mojang => "Mojang",
        }
    }
}

/// 持久化的账户记录
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StoredAccount {
    /// 账户唯一标识（UUID）
    pub uuid: String,
    /// 显示名称
    pub username: String,
    /// 账户类型
    #[serde(rename = "type")]
    pub account_type: AccountType,
    /// 访问令牌（离线账户为空）
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub access_token: Option<String>,
    /// 刷新令牌（微软账户，用于续期）
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub refresh_token: Option<String>,
    /// 是否默认账户
    #[serde(default)]
    pub is_default: bool,
    /// 最后使用时间（RFC3339）
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_used: Option<String>,
}

impl StoredAccount {
    /// 创建离线账户记录
    pub fn offline(uuid: impl Into<String>, username: impl Into<String>) -> Self {
        Self {
            uuid: uuid.into(),
            username: username.into(),
            account_type: AccountType::Offline,
            access_token: None,
            refresh_token: None,
            is_default: false,
            last_used: None,
        }
    }

    /// 创建微软账户记录
    pub fn microsoft(
        uuid: impl Into<String>,
        username: impl Into<String>,
        access_token: impl Into<String>,
        refresh_token: Option<String>,
    ) -> Self {
        Self {
            uuid: uuid.into(),
            username: username.into(),
            account_type: AccountType::Microsoft,
            access_token: Some(access_token.into()),
            refresh_token,
            is_default: false,
            last_used: None,
        }
    }
}

/// 账户存储（accounts.json 持久化，每次操作全量读写）
pub struct AccountStore {
    path: PathBuf,
}

impl AccountStore {
    /// 创建账户存储，指向指定文件
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    /// 默认账户存储路径（数据目录下 starcloud-launcher/accounts.json）
    pub fn default_path() -> PathBuf {
        dirs::data_dir()
            .unwrap_or_else(|| PathBuf::from("."))
            .join("starcloud-launcher")
            .join("accounts.json")
    }

    /// 存储文件路径
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// 读取全部账户（文件不存在返回空列表）
    pub async fn load(&self) -> Result<Vec<StoredAccount>> {
        if !self.path.exists() {
            return Ok(Vec::new());
        }
        let content = tokio::fs::read_to_string(&self.path).await?;
        let accounts: Vec<StoredAccount> = serde_json::from_str(&content)
            .map_err(|e| LauncherError::ConfigError(format!("解析账户文件失败: {}", e)))?;
        Ok(accounts)
    }

    /// 全量写入（原子：临时文件 + rename）
    pub async fn save(&self, accounts: &[StoredAccount]) -> Result<()> {
        if let Some(parent) = self.path.parent() {
            tokio::fs::create_dir_all(parent).await?;
        }
        let content = serde_json::to_string_pretty(accounts)
            .map_err(|e| LauncherError::ConfigError(format!("序列化账户文件失败: {}", e)))?;
        let tmp = self.path.with_extension("json.tmp");
        tokio::fs::write(&tmp, content).await?;
        if cfg!(windows) {
            // Windows 上 rename 目标存在会失败，先删除
            let _ = tokio::fs::remove_file(&self.path).await;
        }
        tokio::fs::rename(&tmp, &self.path).await?;
        Ok(())
    }

    /// 添加或更新账户（同 uuid 覆盖）
    pub async fn upsert(&self, account: StoredAccount) -> Result<()> {
        let mut accounts = self.load().await?;
        if let Some(existing) = accounts.iter_mut().find(|a| a.uuid == account.uuid) {
            *existing = account;
        } else {
            accounts.push(account);
        }
        self.save(&accounts).await
    }

    /// 删除账户，返回是否确实删除
    pub async fn remove(&self, uuid: &str) -> Result<bool> {
        let mut accounts = self.load().await?;
        let before = accounts.len();
        accounts.retain(|a| a.uuid != uuid);
        if accounts.len() == before {
            return Ok(false);
        }
        self.save(&accounts).await?;
        Ok(true)
    }

    /// 设为默认账户（其余取消默认）
    pub async fn set_default(&self, uuid: &str) -> Result<()> {
        let mut accounts = self.load().await?;
        let mut found = false;
        for a in &mut accounts {
            if a.uuid == uuid {
                a.is_default = true;
                found = true;
            } else {
                a.is_default = false;
            }
        }
        if !found {
            return Err(LauncherError::AccountError(format!("账户 {} 不存在", uuid)));
        }
        self.save(&accounts).await
    }

    /// 获取默认账户（无默认时取第一个，仍无则 None）
    pub async fn default_account(&self) -> Result<Option<StoredAccount>> {
        let accounts = self.load().await?;
        Ok(accounts
            .iter()
            .find(|a| a.is_default)
            .cloned()
            .or_else(|| accounts.first().cloned()))
    }

    /// 更新账户的最后使用时间（账户不存在时静默忽略）
    pub async fn touch(&self, uuid: &str) -> Result<()> {
        let mut accounts = self.load().await?;
        let now = chrono::Utc::now().to_rfc3339();
        let mut found = false;
        for a in &mut accounts {
            if a.uuid == uuid {
                a.last_used = Some(now.clone());
                found = true;
            }
        }
        if found {
            self.save(&accounts).await?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn test_account_crud() {
        let dir = tempfile::tempdir().unwrap();
        let store = AccountStore::new(dir.path().join("accounts.json"));

        let account = StoredAccount::offline("00000000-0000-0000-0000-000000000001", "Player");
        store.upsert(account.clone()).await.unwrap();

        // 读取验证
        let loaded = store.load().await.unwrap();
        assert_eq!(loaded.len(), 1);
        assert_eq!(loaded[0].username, "Player");
        assert_eq!(loaded[0].account_type, AccountType::Offline);

        // 同 uuid 覆盖更新
        let updated = StoredAccount::microsoft(
            &account.uuid,
            "Player",
            "token",
            Some("refresh".to_string()),
        );
        store.upsert(updated).await.unwrap();
        let loaded = store.load().await.unwrap();
        assert_eq!(loaded.len(), 1);
        assert_eq!(loaded[0].account_type, AccountType::Microsoft);

        // 默认账户（新文件无默认时取第一个）
        let def = store.default_account().await.unwrap().unwrap();
        assert_eq!(def.uuid, account.uuid);

        // 删除
        assert!(store.remove(&account.uuid).await.unwrap());
        assert!(!store.remove(&account.uuid).await.unwrap());
        assert!(store.load().await.unwrap().is_empty());
    }

    #[tokio::test]
    async fn test_set_default_and_touch() {
        let dir = tempfile::tempdir().unwrap();
        let store = AccountStore::new(dir.path().join("accounts.json"));

        store
            .upsert(StoredAccount::offline("uuid-a", "A"))
            .await
            .unwrap();
        store
            .upsert(StoredAccount::offline("uuid-b", "B"))
            .await
            .unwrap();
        store.set_default("uuid-b").await.unwrap();

        let def = store.default_account().await.unwrap().unwrap();
        assert_eq!(def.uuid, "uuid-b");
        assert!(def.is_default);

        // 设置不存在的账户报错
        assert!(store.set_default("nonexistent").await.is_err());

        // touch 记录时间
        store.touch("uuid-a").await.unwrap();
        let accounts = store.load().await.unwrap();
        let a = accounts.iter().find(|a| a.uuid == "uuid-a").unwrap();
        assert!(a.last_used.is_some());
    }
}
