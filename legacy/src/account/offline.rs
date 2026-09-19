use crate::error::{LauncherError, Result};
use md_5::{Digest, Md5};

/// 离线账户
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct OfflineAccount {
    /// 用户名
    pub username: String,
    /// UUID（离线下固定生成）
    pub uuid: String,
}

impl OfflineAccount {
    /// 创建离线账户
    /// UUID 由用户名确定性生成（基于用户名 MD5）
    pub fn new(username: impl Into<String>) -> Result<Self> {
        let username = username.into();
        if username.is_empty() || username.len() > 32 {
            return Err(LauncherError::ArgumentError(
                "用户名长度必须在 1-32 字符之间".to_string(),
            ));
        }
        if !username.chars().all(|c| c.is_alphanumeric() || c == '_') {
            return Err(LauncherError::ArgumentError(
                "用户名只能包含字母、数字和下划线".to_string(),
            ));
        }

        // 基于用户名确定性生成 UUID
        let uuid = offline_uuid(&username);
        Ok(Self { username, uuid })
    }

    /// 获取启动配置需要的凭据
    pub fn credentials(&self) -> AccountCredentials {
        AccountCredentials {
            uuid: self.uuid.clone(),
            username: self.username.clone(),
            access_token: "0".to_string(),
            user_type: "mojang".to_string(),
            xuid: None,
            client_id: None,
        }
    }
}

/// 账户凭据（用于构建 LaunchConfig）
#[derive(Debug, Clone)]
pub struct AccountCredentials {
    pub uuid: String,
    pub username: String,
    pub access_token: String,
    pub user_type: String,
    pub xuid: Option<String>,
    pub client_id: Option<String>,
}

/// 根据用户名生成离线 UUID
fn offline_uuid(username: &str) -> String {
    // 使用 "OfflinePlayer:" + username 的 MD5 生成 UUID（Mojang 方式）
    let input = format!("OfflinePlayer:{}", username);
    let digest = Md5::digest(input.as_bytes());
    let mut uuid_bytes: [u8; 16] = digest.into();
    uuid_bytes[6] = (uuid_bytes[6] & 0x0f) | 0x30;
    uuid_bytes[8] = (uuid_bytes[8] & 0x3f) | 0x80;

    format!(
        "{:02x}{:02x}{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
        uuid_bytes[0], uuid_bytes[1], uuid_bytes[2], uuid_bytes[3],
        uuid_bytes[4], uuid_bytes[5], uuid_bytes[6], uuid_bytes[7],
        uuid_bytes[8], uuid_bytes[9], uuid_bytes[10], uuid_bytes[11],
        uuid_bytes[12], uuid_bytes[13], uuid_bytes[14], uuid_bytes[15],
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_offline_account_creation() {
        let account = OfflineAccount::new("Player").unwrap();
        assert_eq!(account.username, "Player");
        assert!(!account.uuid.is_empty());
        assert!(uuid::Uuid::parse_str(&account.uuid).is_ok());
    }

    #[test]
    fn test_offline_uuid_deterministic() {
        let a = OfflineAccount::new("TestPlayer").unwrap();
        let b = OfflineAccount::new("TestPlayer").unwrap();
        assert_eq!(a.uuid, b.uuid);
    }

    #[test]
    fn test_invalid_username() {
        assert!(OfflineAccount::new("").is_err());
        assert!(OfflineAccount::new("name with spaces").is_err());
    }
}
