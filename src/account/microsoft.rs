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

/// 微软 OAuth Token 响应
#[derive(Debug, Clone, serde::Deserialize)]
pub struct TokenResponse {
    pub access_token: String,
    pub refresh_token: Option<String>,
    pub expires_in: u64,
    pub token_type: String,
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
            return Err(LauncherError::Other(format!(
                "获取设备码失败: HTTP {}",
                response.status()
            )));
        }

        let result: DeviceCodeResponse = response.json().await?;
        Ok(result)
    }

    /// 第二步：轮询获取 Token
    pub async fn poll_token(&self, device_code: &str) -> Result<TokenResponse> {
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
            .await?;

        if !response.status().is_success() {
            let body = response.text().await.unwrap_or_default();
            return Err(LauncherError::Other(format!("获取 Token 失败: {}", body)));
        }

        let token: TokenResponse = response.json().await?;
        Ok(token)
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
            return Err(LauncherError::Other("Xbox Live 认证失败".to_string()));
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
            return Err(LauncherError::Other("XSTS 认证失败".to_string()));
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
            return Err(LauncherError::Other("Minecraft 认证失败".to_string()));
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
            return Err(LauncherError::Other("获取玩家档案失败".to_string()));
        }

        let profile: MinecraftProfile = response.json().await?;
        Ok(profile)
    }

    /// 完整登录流程
    pub async fn full_login(
        &self,
        device_code: &str,
        poll_interval_secs: u64,
        max_polls: u32,
    ) -> Result<MicrosoftLoginResult> {
        let mut polls = 0;
        loop {
            if polls >= max_polls {
                return Err(LauncherError::Other("登录超时".to_string()));
            }

            tokio::time::sleep(std::time::Duration::from_secs(poll_interval_secs)).await;

            match self.poll_token(device_code).await {
                Ok(token) => {
                    // Xbox Live 认证
                    let xbl = self.auth_xbox_live(&token.access_token).await?;
                    // XSTS 认证
                    let xsts = self.auth_xsts(&xbl.token).await?;
                    let uhs = xsts
                        .display_claims
                        .xui
                        .first()
                        .ok_or_else(|| LauncherError::Other("XSTS 返回空的 UHS".to_string()))?
                        .uhs
                        .clone();
                    let xuid = xsts
                        .display_claims
                        .xui
                        .first()
                        .map(|x| x.uhs.clone())
                        .unwrap_or_default();
                    // Minecraft 认证
                    let mc = self.auth_minecraft(&uhs, &xsts.token).await?;
                    // 获取档案
                    let profile = self.get_profile(&mc.access_token).await?;

                    return Ok(MicrosoftLoginResult {
                        access_token: mc.access_token,
                        refresh_token: token.refresh_token,
                        uuid: profile.id,
                        username: profile.name,
                        xuid,
                        uhs,
                    });
                }
                Err(e) => {
                    let msg = e.to_string();
                    if msg.contains("authorization_pending") {
                        polls += 1;
                        continue;
                    }
                    return Err(e);
                }
            }
        }
    }
}

impl Default for MicrosoftAuth {
    fn default() -> Self {
        Self::new()
    }
}
