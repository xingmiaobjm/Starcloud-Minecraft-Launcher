use crate::error::{LauncherError, Result};

/// 微软 OAuth 2.0 认证客户端（当前官方登录方式）
#[derive(Debug, Clone)]
pub struct MicrosoftAuth {
    client: reqwest::Client,
    /// Azure 应用 client_id
    client_id: String,
}

/// 微软 OAuth 设备码响应
#[derive(Debug, Clone, serde::Deserialize)]
pub struct DeviceCodeResponse {
    pub user_code: String,
    pub device_code: String,
    pub verification_uri: String,
    pub message: String,
    pub interval: u64,
    pub expires_in: u64,
}

/// 微软 OAuth Token 响应（成功时）
#[derive(Debug, Clone, serde::Deserialize)]
pub struct TokenResponse {
    pub access_token: String,
    pub refresh_token: Option<String>,
    pub expires_in: u64,
    pub token_type: String,
}

/// 微软 OAuth Token 错误响应（失败时）
#[derive(Debug, Clone, serde::Deserialize)]
pub struct TokenErrorResponse {
    pub error: String,
    #[serde(default)]
    pub error_description: Option<String>,
}

/// Token 轮询状态（区分可重试与终止错误）
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TokenPollError {
    /// authorization_pending：用户尚未完成授权，继续轮询
    Pending,
    /// slow_down：请求过于频繁，需延长轮询间隔
    SlowDown,
    /// expired_token：设备码已过期，需重新获取
    Expired,
    /// authorization_declined：用户拒绝授权
    Declined,
    /// 其他错误（HTTP/网络/未知）
    Other(String),
}

impl std::fmt::Display for TokenPollError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            TokenPollError::Pending => write!(f, "授权尚未完成，请继续等待"),
            TokenPollError::SlowDown => write!(f, "轮询过于频繁，请稍后重试"),
            TokenPollError::Expired => write!(f, "设备码已过期，请重新发起登录"),
            TokenPollError::Declined => write!(f, "用户拒绝授权"),
            TokenPollError::Other(msg) => write!(f, "{}", msg),
        }
    }
}

/// Xbox Live 认证响应
#[derive(Debug, Clone, serde::Deserialize)]
pub struct XblAuthResponse {
    #[serde(rename = "Token")]
    pub token: String,
    #[serde(rename = "DisplayClaims")]
    pub display_claims: serde_json::Value,
}

/// XSTS 认证响应
#[derive(Debug, Clone, serde::Deserialize)]
pub struct XstsAuthResponse {
    #[serde(rename = "Token")]
    pub token: String,
    #[serde(rename = "DisplayClaims")]
    pub display_claims: XstsDisplayClaims,
}

#[derive(Debug, Clone, serde::Deserialize)]
pub struct XstsDisplayClaims {
    pub xui: Vec<XuiClaim>,
}

#[derive(Debug, Clone, serde::Deserialize)]
pub struct XuiClaim {
    pub uhs: String,
}

/// Minecraft 认证响应
#[derive(Debug, Clone, serde::Deserialize)]
pub struct MinecraftAuthResponse {
    pub access_token: String,
    pub expires_in: u64,
    pub token_type: String,
}

/// Minecraft 档案
#[derive(Debug, Clone, serde::Deserialize)]
pub struct MinecraftProfile {
    pub id: String,
    pub name: String,
    pub skins: Option<Vec<serde_json::Value>>,
}

/// 完整微软登录结果
#[derive(Debug, Clone)]
pub struct MicrosoftLoginResult {
    pub access_token: String,
    pub refresh_token: Option<String>,
    pub uuid: String,
    pub username: String,
    /// Xbox XUID。注意：XSTS 响应不直接提供该字段，官方启动器
    /// 使用 `auth_xuid` 占位；此处保持空串（在线模式不依赖它）。
    pub xuid: String,
    pub uhs: String,
}

impl MicrosoftAuth {
    /// 默认 Azure client_id (Minecraft Launcher 官方)
    const DEFAULT_CLIENT_ID: &'static str = "00000000402b5328";

    /// 创建微软认证客户端
    pub fn new() -> Self {
        Self {
            client: reqwest::Client::new(),
            client_id: Self::DEFAULT_CLIENT_ID.to_string(),
        }
    }

    /// 使用自定义 client_id
    pub fn with_client_id(mut self, client_id: impl Into<String>) -> Self {
        self.client_id = client_id.into();
        self
    }

    /// 第一步：获取设备码（需要在浏览器中打开 verification_uri 并输入 user_code）
    pub async fn get_device_code(&self) -> Result<DeviceCodeResponse> {
        let params = [
            ("client_id", self.client_id.as_str()),
            ("scope", "XboxLive.signin offline_access"),
        ];

        let response = self
            .client
            .post("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode")
            .form(&params)
            .timeout(std::time::Duration::from_secs(15))
            .send()
            .await?;

        if !response.status().is_success() {
            return Err(LauncherError::AccountError(format!(
                "获取设备码失败: HTTP {}",
                response.status()
            )));
        }

        let result: DeviceCodeResponse = response.json().await?;
        Ok(result)
    }

    /// 第二步：轮询获取 Token（解析 error 字段并映射为明确的轮询状态）
    pub async fn poll_token(
        &self,
        device_code: &str,
    ) -> std::result::Result<TokenResponse, TokenPollError> {
        let params = [
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ("client_id", self.client_id.as_str()),
            ("device_code", device_code),
        ];

        let response = self
            .client
            .post("https://login.microsoftonline.com/consumers/oauth2/v2.0/token")
            .form(&params)
            .timeout(std::time::Duration::from_secs(15))
            .send()
            .await
            .map_err(|e| TokenPollError::Other(format!("网络错误: {}", e)))?;

        let status = response.status();
        let body = response.text().await.unwrap_or_default();

        if status.is_success() {
            let token: TokenResponse = serde_json::from_str(&body)
                .map_err(|e| TokenPollError::Other(format!("解析 Token 响应失败: {}", e)))?;
            return Ok(token);
        }

        // 失败：解析 error 字段
        let err = serde_json::from_str::<TokenErrorResponse>(&body).map_err(|_| {
            TokenPollError::Other(format!("获取 Token 失败: HTTP {} {}", status, body))
        })?;

        match err.error.as_str() {
            "authorization_pending" => Err(TokenPollError::Pending),
            "slow_down" => Err(TokenPollError::SlowDown),
            "expired_token" => Err(TokenPollError::Expired),
            "authorization_declined" => Err(TokenPollError::Declined),
            other => Err(TokenPollError::Other(format!(
                "{}: {}",
                other,
                err.error_description.unwrap_or_default()
            ))),
        }
    }

    /// 第三步：Xbox Live 认证
    pub async fn auth_xbox_live(&self, access_token: &str) -> Result<XblAuthResponse> {
        #[derive(serde::Serialize)]
        struct XblRequest<'a> {
            #[serde(rename = "Properties")]
            properties: XblProperties<'a>,
            #[serde(rename = "RelyingParty")]
            relying_party: &'a str,
            #[serde(rename = "TokenType")]
            token_type: &'a str,
        }

        #[derive(serde::Serialize)]
        struct XblProperties<'a> {
            #[serde(rename = "AuthMethod")]
            auth_method: &'a str,
            #[serde(rename = "SiteName")]
            site_name: &'a str,
            #[serde(rename = "RpsTicket")]
            rps_ticket: String,
        }

        let req = XblRequest {
            properties: XblProperties {
                auth_method: "RPS",
                site_name: "user.auth.xboxlive.com",
                rps_ticket: format!("d={}", access_token),
            },
            relying_party: "http://auth.xboxlive.com",
            token_type: "JWT",
        };

        let response = self
            .client
            .post("https://user.auth.xboxlive.com/user/authenticate")
            .json(&req)
            .timeout(std::time::Duration::from_secs(15))
            .send()
            .await?;

        if !response.status().is_success() {
            return Err(LauncherError::AccountError(
                "Xbox Live 认证失败".to_string(),
            ));
        }

        let xbl: XblAuthResponse = response.json().await?;
        Ok(xbl)
    }

    /// 第四步：XSTS 认证
    pub async fn auth_xsts(&self, xbl_token: &str) -> Result<XstsAuthResponse> {
        #[derive(serde::Serialize)]
        struct XstsRequest<'a> {
            #[serde(rename = "Properties")]
            properties: XstsProperties<'a>,
            #[serde(rename = "RelyingParty")]
            relying_party: &'a str,
            #[serde(rename = "TokenType")]
            token_type: &'a str,
        }

        #[derive(serde::Serialize)]
        struct XstsProperties<'a> {
            #[serde(rename = "SandboxId")]
            sandbox_id: &'a str,
            #[serde(rename = "UserTokens")]
            user_tokens: Vec<&'a str>,
        }

        let req = XstsRequest {
            properties: XstsProperties {
                sandbox_id: "RETAIL",
                user_tokens: vec![xbl_token],
            },
            relying_party: "rp://api.minecraftservices.com/",
            token_type: "JWT",
        };

        let response = self
            .client
            .post("https://xsts.auth.xboxlive.com/xsts/authorize")
            .json(&req)
            .timeout(std::time::Duration::from_secs(15))
            .send()
            .await?;

        if !response.status().is_success() {
            return Err(LauncherError::AccountError("XSTS 认证失败".to_string()));
        }

        let xsts: XstsAuthResponse = response.json().await?;
        Ok(xsts)
    }

    /// 第五步：获取 Minecraft Token
    pub async fn auth_minecraft(
        &self,
        uhs: &str,
        xsts_token: &str,
    ) -> Result<MinecraftAuthResponse> {
        #[derive(serde::Serialize)]
        struct McRequest {
            #[serde(rename = "identityToken")]
            identity_token: String,
        }

        let xbl_identity = format!("XBL3.0 x={};{}", uhs, xsts_token);
        let req = McRequest {
            identity_token: xbl_identity,
        };

        let response = self
            .client
            .post("https://api.minecraftservices.com/authentication/login_with_xbox")
            .json(&req)
            .timeout(std::time::Duration::from_secs(15))
            .send()
            .await?;

        if !response.status().is_success() {
            return Err(LauncherError::AccountError(
                "Minecraft 认证失败".to_string(),
            ));
        }

        let mc: MinecraftAuthResponse = response.json().await?;
        Ok(mc)
    }

    /// 获取玩家档案（需要 Minecraft Bearer Token）
    pub async fn get_profile(&self, mc_token: &str) -> Result<MinecraftProfile> {
        let response = self
            .client
            .get("https://api.minecraftservices.com/minecraft/profile")
            .bearer_auth(mc_token)
            .timeout(std::time::Duration::from_secs(10))
            .send()
            .await?;

        if !response.status().is_success() {
            return Err(LauncherError::AccountError(format!(
                "获取玩家档案失败: HTTP {}",
                response.status()
            )));
        }

        let profile: MinecraftProfile = response.json().await?;
        Ok(profile)
    }

    /// 完整登录流程（设备码轮询）
    ///
    /// 首轮立即发起请求；`authorization_pending`/`slow_down` 按轮询间隔重试，
    /// 其余错误立即返回。
    pub async fn full_login(
        &self,
        device_code: &str,
        poll_interval_secs: u64,
        max_polls: u32,
    ) -> Result<MicrosoftLoginResult> {
        let mut polls = 0u32;
        loop {
            if polls >= max_polls {
                return Err(LauncherError::AccountError(format!(
                    "登录超时：已轮询 {} 次",
                    max_polls
                )));
            }
            polls += 1;

            match self.poll_token(device_code).await {
                Ok(token) => {
                    return self
                        .complete_auth_chain(token.access_token, token.refresh_token)
                        .await;
                }
                Err(TokenPollError::Pending) => {
                    tokio::time::sleep(std::time::Duration::from_secs(poll_interval_secs)).await;
                }
                Err(TokenPollError::SlowDown) => {
                    // 服务端要求放慢，双倍间隔
                    tokio::time::sleep(std::time::Duration::from_secs(
                        poll_interval_secs.max(5) * 2,
                    ))
                    .await;
                }
                Err(e) => {
                    return Err(LauncherError::AccountError(e.to_string()));
                }
            }
        }
    }

    /// 使用 refresh_token 续期（微软 OAuth refresh → Xbox/XSTS/MC 全链路）
    pub async fn refresh_login(&self, refresh_token: &str) -> Result<MicrosoftLoginResult> {
        let params = [
            ("grant_type", "refresh_token"),
            ("client_id", self.client_id.as_str()),
            ("refresh_token", refresh_token),
            ("scope", "XboxLive.signin offline_access"),
        ];

        let response = self
            .client
            .post("https://login.microsoftonline.com/consumers/oauth2/v2.0/token")
            .form(&params)
            .timeout(std::time::Duration::from_secs(15))
            .send()
            .await?;

        if !response.status().is_success() {
            let body = response.text().await.unwrap_or_default();
            return Err(LauncherError::AccountError(format!(
                "刷新 Token 失败: {}",
                body
            )));
        }

        let token: TokenResponse = response.json().await?;
        self.complete_auth_chain(token.access_token, token.refresh_token)
            .await
    }

    /// 完成 access_token → Xbox → XSTS → Minecraft → Profile 全链路
    async fn complete_auth_chain(
        &self,
        access_token: String,
        refresh_token: Option<String>,
    ) -> Result<MicrosoftLoginResult> {
        // Xbox Live 认证
        let xbl = self.auth_xbox_live(&access_token).await?;
        // XSTS 认证
        let xsts = self.auth_xsts(&xbl.token).await?;
        let uhs = xsts
            .display_claims
            .xui
            .first()
            .ok_or_else(|| LauncherError::AccountError("XSTS 返回空的 UHS".to_string()))?
            .uhs
            .clone();
        // Minecraft 认证
        let mc = self.auth_minecraft(&uhs, &xsts.token).await?;
        // 获取档案
        let profile = self.get_profile(&mc.access_token).await?;

        Ok(MicrosoftLoginResult {
            access_token: mc.access_token,
            refresh_token,
            uuid: profile.id,
            username: profile.name,
            // XSTS 不提供 xuid 字段，官方启动器此处占位为空
            xuid: String::new(),
            uhs,
        })
    }
}

impl Default for MicrosoftAuth {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_token_error_mapping() {
        assert_eq!(
            map_error_str("authorization_pending"),
            TokenPollError::Pending
        );
        assert_eq!(map_error_str("slow_down"), TokenPollError::SlowDown);
        assert_eq!(map_error_str("expired_token"), TokenPollError::Expired);
        assert_eq!(
            map_error_str("authorization_declined"),
            TokenPollError::Declined
        );
    }

    fn map_error_str(error: &str) -> TokenPollError {
        match error {
            "authorization_pending" => TokenPollError::Pending,
            "slow_down" => TokenPollError::SlowDown,
            "expired_token" => TokenPollError::Expired,
            "authorization_declined" => TokenPollError::Declined,
            other => TokenPollError::Other(other.to_string()),
        }
    }
}
