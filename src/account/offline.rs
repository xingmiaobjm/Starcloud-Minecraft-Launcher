use crate::error::{LauncherError, Result};

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
                "用户名长度必须在 1-32 字符之间".to_string()
            ));
        }
        if !username.chars().all(|c| c.is_alphanumeric() || c == '_') {
            return Err(LauncherError::ArgumentError(
                "用户名只能包含字母、数字和下划线".to_string()
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
    let digest = md5::compute(input.as_bytes());
    let bytes = digest.0;

    // 设置为 version 3 UUID
    let mut uuid_bytes = bytes;
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

/// MD5 计算用简单实现
mod md5 {
    pub struct Digest(pub [u8; 16]);

    pub fn compute(data: &[u8]) -> Digest {
        // 标准 MD5 实现
        let mut state = [0x67452301u32, 0xEFCDAB89, 0x98BADCFE, 0x10325476];

        // 填充
        let mut msg = data.to_vec();
        let original_len_bits = (msg.len() as u64) * 8;
        msg.push(0x80);
        while (msg.len() % 64) != 56 {
            msg.push(0);
        }
        msg.extend_from_slice(&original_len_bits.to_le_bytes());

        let s = [7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
                 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
                 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
                 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21];

        let k = [
            0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee,
            0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
            0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
            0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
            0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa,
            0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
            0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed,
            0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
            0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
            0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
            0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
            0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
            0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
            0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
            0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
            0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
        ];

        for chunk in msg.chunks(64) {
            let mut m = [0u32; 16];
            for (i, word) in chunk.chunks(4).enumerate() {
                m[i] = u32::from_le_bytes([word[0], word[1], word[2], word[3]]);
            }

            let (mut a, mut b, mut c, mut d) = (state[0], state[1], state[2], state[3]);

            for i in 0..64 {
                let (f, g): (u32, usize) = if i < 16 {
                    ((b & c) | (!b & d), i)
                } else if i < 32 {
                    ((d & b) | (!d & c), (5 * i + 1) % 16)
                } else if i < 48 {
                    (b ^ c ^ d, (3 * i + 5) % 16)
                } else {
                    (c ^ (b | !d), (7 * i) % 16)
                };

                let temp = d;
                d = c;
                c = b;
                b = b.wrapping_add(
                    (a.wrapping_add(f).wrapping_add(k[i]).wrapping_add(m[g])).rotate_left(s[i])
                );
                a = temp;
            }

            state[0] = state[0].wrapping_add(a);
            state[1] = state[1].wrapping_add(b);
            state[2] = state[2].wrapping_add(c);
            state[3] = state[3].wrapping_add(d);
        }

        let mut bytes = [0u8; 16];
        for (i, &word) in state.iter().enumerate() {
            bytes[i * 4..(i + 1) * 4].copy_from_slice(&word.to_le_bytes());
        }
        Digest(bytes)
    }
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
