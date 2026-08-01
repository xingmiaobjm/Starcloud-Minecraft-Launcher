use crate::error::{LauncherError, Result};

/// Mojang Auth API 客户端（旧版 Mojang 账户系统，已被微软迁移）
#[derive(Debug, Clone)]
pub struct MojangAuth {
    client: reqwest::Client,
}

/// Mojang 认证响应
#[derive(Debug, Clone, serde::Deserialize)]
pub struct MojangAuthResponse {
    #[serde(rename = "accessToken")]
    pub access_token: String,
    #[serde(rename = "clientToken")]
    pub client_token: String,
    #[serde(rename = "selectedProfile")]
    pub selected_profile: SelectedProfile,
    #[serde(rename = "availableProfiles")]
    pub available_profiles: Option<Vec<SelectedProfile>>,
}

/// 用户档案
#[derive(Debug, Clone, serde::Deserialize)]
pub struct SelectedProfile {
    pub id: String,
    pub name: String,
}

impl MojangAuth {
    /// 创建 Mojang 认证客户端
    pub fn new() -> Self {
        Self {
            client: reqwest::Client::new(),
        }
    }

    /// 使用密码登录（已弃用，仅支持仍使用旧账户的用户）
    pub async fn login(
        &self,
        email: &str,
        password: &str,
        client_token: Option<&str>,
    ) -> Result<MojangAuthResponse> {
        #[derive(serde::Serialize)]
        struct Request<'a> {
            agent: AgentInfo,
            username: &'a str,
            password: &'a str,
            #[serde(rename = "clientToken")]
            client_token: Option<String>,
            #[serde(rename = "requestUser")]
            request_user: bool,
        }

        #[derive(serde::Serialize)]
        struct AgentInfo {
            name: &'static str,
            version: u32,
        }

        let req = Request {
            agent: AgentInfo {
                name: "Minecraft",
                version: 1,
            },
            username: email,
            password,
            client_token: client_token.map(|s| s.to_string()),
            request_user: true,
        };

        let response = self
            .client
            .post("https://authserver.mojang.com/authenticate")
            .json(&req)
            .timeout(std::time::Duration::from_secs(15))
            .send()
            .await?;

        if !response.status().is_success() {
            return Err(LauncherError::Other(format!(
                "Mojang 认证失败: HTTP {}",
                response.status()
            )));
        }

        let auth: MojangAuthResponse = response.json().await?;
        Ok(auth)
    }

    /// 刷新令牌
    pub async fn refresh(
        &self,
        access_token: &str,
        client_token: &str,
    ) -> Result<MojangAuthResponse> {
        #[derive(serde::Serialize)]
        struct RefreshRequest<'a> {
            #[serde(rename = "accessToken")]
            access_token: &'a str,
            #[serde(rename = "clientToken")]
            client_token: &'a str,
            #[serde(rename = "requestUser")]
            request_user: bool,
        }

        let req = RefreshRequest {
            access_token,
            client_token,
            request_user: true,
        };

        let response = self
            .client
            .post("https://authserver.mojang.com/refresh")
            .json(&req)
            .timeout(std::time::Duration::from_secs(15))
            .send()
            .await?;

        if !response.status().is_success() {
            return Err(LauncherError::Other("令牌刷新失败".to_string()));
        }

        let auth: MojangAuthResponse = response.json().await?;
        Ok(auth)
    }

    /// 验证令牌是否有效
    pub async fn validate(&self, access_token: &str, client_token: &str) -> Result<bool> {
        #[derive(serde::Serialize)]
        struct ValidateRequest<'a> {
            #[serde(rename = "accessToken")]
            access_token: &'a str,
            #[serde(rename = "clientToken")]
            client_token: &'a str,
        }

        let req = ValidateRequest {
            access_token,
            client_token,
        };

        let response = self
            .client
            .post("https://authserver.mojang.com/validate")
            .json(&req)
            .timeout(std::time::Duration::from_secs(10))
            .send()
            .await?;

        Ok(response.status().is_success() || response.status().as_u16() == 204)
    }

    /// 注销令牌
    pub async fn signout(&self, access_token: &str, client_token: &str) -> Result<()> {
        #[derive(serde::Serialize)]
        struct SignoutRequest<'a> {
            #[serde(rename = "accessToken")]
            access_token: &'a str,
            #[serde(rename = "clientToken")]
            client_token: &'a str,
        }

        let req = SignoutRequest {
            access_token,
            client_token,
        };

        self.client
            .post("https://authserver.mojang.com/signout")
            .json(&req)
            .timeout(std::time::Duration::from_secs(10))
            .send()
            .await?;

        Ok(())
    }
}

impl Default for MojangAuth {
    fn default() -> Self {
        Self::new()
    }
}
