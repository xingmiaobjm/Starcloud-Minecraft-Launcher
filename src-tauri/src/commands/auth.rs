// 认证相关命令
// StarCloud Minecraft Launcher - LGPL-3.0-only

use serde::{Deserialize, Serialize};
use std::sync::Mutex;
use tauri::State;

const MICROSOFT_CLIENT_ID: &str = "00000000402b5328"; // Minecraft 官方客户端 ID
const MICROSOFT_REDIRECT_URI: &str = "http://localhost:59125/callback";
const SCOPE: &str = "XboxLive.signin offline_access";

pub struct AuthState {
    pub current_profile: Mutex<Option<UserProfileInfo>>,
    pub pkce_verifier: Mutex<Option<String>>,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct UserProfileInfo {
    pub name: String,
    pub uuid: String,
    pub skin_url: Option<String>,
    pub access_token: String,
    pub refresh_token: Option<String>,
    pub login_type: String,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct UserProfile {
    pub name: String,
    pub uuid: String,
    pub skin_url: Option<String>,
}

#[derive(Serialize, Deserialize)]
pub struct AuthUrl {
    pub url: String,
}

#[derive(Serialize, Deserialize)]
pub struct AuthResult {
    pub profile: UserProfile,
    pub login_type: String,
}

// ========= Microsoft OAuth =========

/// 获取微软登录 URL（含 PKCE）
#[tauri::command]
pub fn auth_get_ms_login_url(auth_state: State<'_, AuthState>) -> Result<AuthUrl, String> {
    let (verifier, challenge) = generate_pkce();
    // 存储 verifier 供后续 token 交换使用
    *auth_state.pkce_verifier.lock().unwrap() = Some(verifier);
    tracing::info!("MS 登录 URL 已生成");
    let url = format!(
        "https://login.live.com/oauth20_authorize.srf\
         ?client_id={}\
         &response_type=code\
         &redirect_uri={}\
         &scope={}\
         &code_challenge={}\
         &code_challenge_method=S256",
        MICROSOFT_CLIENT_ID,
        urlencoding(MICROSOFT_REDIRECT_URI),
        urlencoding(SCOPE),
        challenge
    );
    Ok(AuthUrl { url })
}

/// 用授权码完成微软登录
#[tauri::command]
pub async fn auth_microsoft_complete(
    code: String,
    auth_state: State<'_, AuthState>,
) -> Result<AuthResult, String> {
    // 获取 PKCE verifier
    let verifier = auth_state.pkce_verifier.lock().unwrap()
        .clone()
        .ok_or("PKCE verifier 未找到，请先获取登录 URL")?;

    // 1. 用授权码换取 access_token
    let token = exchange_code_for_token(&code, &verifier).await?;

    // 2. 用 access_token 认证 Xbox Live
    let xbl_token = authenticate_xbl(&token.access_token).await?;

    // 3. 获取 XSTS token
    let xsts_token = authenticate_xsts(&xbl_token).await?;

    // 4. 用 XSTS token 获取 Minecraft token
    let mc_token = authenticate_minecraft(&xsts_token).await?;

    // 5. 获取 Minecraft 用户信息
    let profile = get_minecraft_profile(&mc_token).await?;

    // 保存用户信息
    *auth_state.current_profile.lock().unwrap() = Some(UserProfileInfo {
        name: profile.name.clone(),
        uuid: profile.uuid.clone(),
        skin_url: profile.skin_url.clone(),
        access_token: mc_token.access_token.clone(),
        refresh_token: token.refresh_token.clone(),
        login_type: "microsoft".to_string(),
    });

    Ok(AuthResult {
        profile,
        login_type: "microsoft".to_string(),
    })
}

/// 用刷新令牌刷新 access_token
#[tauri::command]
pub async fn auth_refresh_token(refresh_token: String) -> Result<AuthResult, String> {
    let token = refresh_ms_token(&refresh_token).await?;
    let xbl_token = authenticate_xbl(&token.access_token).await?;
    let xsts_token = authenticate_xsts(&xbl_token).await?;
    let mc_token = authenticate_minecraft(&xsts_token).await?;
    let profile = get_minecraft_profile(&mc_token).await?;
    Ok(AuthResult {
        profile,
        login_type: "microsoft".to_string(),
    })
}

// ========= 离线登录 =========

/// 离线登录
#[tauri::command]
pub fn auth_offline_login(
    username: String,
    auth_state: State<'_, AuthState>,
) -> Result<AuthResult, String> {
    let uuid = offline_uuid(&username);
    let profile = UserProfile {
        name: username.clone(),
        uuid: uuid.clone(),
        skin_url: None,
    };
    *auth_state.current_profile.lock().unwrap() = Some(UserProfileInfo {
        name: username,
        uuid,
        skin_url: None,
        access_token: String::new(),
        refresh_token: None,
        login_type: "offline".to_string(),
    });
    Ok(AuthResult {
        profile,
        login_type: "offline".to_string(),
    })
}

// ========= 状态管理 =========

/// 获取当前用户信息
#[tauri::command]
pub fn auth_get_profile(auth_state: State<'_, AuthState>) -> Result<Option<UserProfile>, String> {
    let profile = auth_state.current_profile.lock().unwrap();
    Ok(profile.as_ref().map(|p| UserProfile {
        name: p.name.clone(),
        uuid: p.uuid.clone(),
        skin_url: p.skin_url.clone(),
    }))
}

/// 退出登录
#[tauri::command]
pub fn auth_logout(auth_state: State<'_, AuthState>) -> Result<bool, String> {
    *auth_state.current_profile.lock().unwrap() = None;
    *auth_state.pkce_verifier.lock().unwrap() = None;
    Ok(true)
}

// ========= 辅助函数 =========

#[derive(Serialize, Deserialize)]
struct TokenResponse {
    access_token: String,
    refresh_token: Option<String>,
    expires_in: Option<u64>,
    token_type: Option<String>,
}

#[derive(Serialize, Deserialize)]
struct XblAuthResponse {
    #[serde(rename = "Token")]
    token: String,
    #[serde(rename = "DisplayClaims")]
    display_claims: serde_json::Value,
}

#[derive(Serialize, Deserialize)]
struct McAuthResponse {
    access_token: String,
    expires_in: u64,
    token_type: String,
}

#[derive(Serialize, Deserialize)]
struct McProfileResponse {
    id: String,
    name: String,
    skins: Vec<McSkin>,
}

#[derive(Serialize, Deserialize)]
struct McSkin {
    id: String,
    state: String,
    url: String,
    variant: String,
}

#[derive(Serialize, Deserialize)]
struct McOwnershipResponse {
    items: Vec<McItem>,
}

#[derive(Serialize, Deserialize)]
struct McItem {
    name: String,
}

/// 用授权码换取 token
async fn exchange_code_for_token(code: &str, verifier: &str) -> Result<TokenResponse, String> {
    let client = reqwest::Client::new();
    let resp: TokenResponse = client
        .post("https://login.live.com/oauth20_token.srf")
        .form(&[
            ("client_id", MICROSOFT_CLIENT_ID),
            ("code", code),
            ("code_verifier", verifier),
            ("grant_type", "authorization_code"),
            ("redirect_uri", MICROSOFT_REDIRECT_URI),
        ])
        .send()
        .await
        .map_err(|e| format!("网络错误: {}", e))?
        .json()
        .await
        .map_err(|e| format!("解析 Token 失败: {}", e))?;
    Ok(resp)
}

/// 刷新 token
async fn refresh_ms_token(refresh_token: &str) -> Result<TokenResponse, String> {
    let client = reqwest::Client::new();
    let resp: TokenResponse = client
        .post("https://login.live.com/oauth20_token.srf")
        .form(&[
            ("client_id", MICROSOFT_CLIENT_ID),
            ("refresh_token", refresh_token),
            ("grant_type", "refresh_token"),
            ("redirect_uri", MICROSOFT_REDIRECT_URI),
        ])
        .send()
        .await
        .map_err(|e| format!("刷新 Token 网络错误: {}", e))?
        .json()
        .await
        .map_err(|e| format!("解析 Token 失败: {}", e))?;
    Ok(resp)
}

/// Xbox Live 认证
async fn authenticate_xbl(access_token: &str) -> Result<XblAuthResponse, String> {
    let client = reqwest::Client::new();
    let payload = serde_json::json!({
        "Properties": {
            "AuthMethod": "RPS",
            "SiteName": "user.auth.xboxlive.com",
            "RpsTicket": &format!("d={}", access_token)
        },
        "RelyingParty": "http://auth.xboxlive.com",
        "TokenType": "JWT"
    });
    let resp: XblAuthResponse = client
        .post("https://user.auth.xboxlive.com/user/authenticate")
        .json(&payload)
        .send()
        .await
        .map_err(|e| format!("XBL 认证失败: {}", e))?
        .json()
        .await
        .map_err(|e| format!("XBL 解析失败: {}", e))?;
    Ok(resp)
}

/// XSTS 令牌获取
async fn authenticate_xsts(xbl_token: &XblAuthResponse) -> Result<XblAuthResponse, String> {
    let client = reqwest::Client::new();
    let payload = serde_json::json!({
        "Properties": {
            "SandboxId": "RETAIL",
            "UserTokens": [&xbl_token.token]
        },
        "RelyingParty": "rp://api.minecraftservices.com/",
        "TokenType": "JWT"
    });
    let resp: XblAuthResponse = client
        .post("https://xsts.auth.xboxlive.com/xsts/authorize")
        .json(&payload)
        .send()
        .await
        .map_err(|e| format!("XSTS 认证失败: {}", e))?
        .json()
        .await
        .map_err(|e| format!("XSTS 解析失败: {}", e))?;
    Ok(resp)
}

/// 用 XSTS token 获取 Minecraft token
async fn authenticate_minecraft(xsts_token: &XblAuthResponse) -> Result<McAuthResponse, String> {
    let client = reqwest::Client::new();
    let payload = serde_json::json!({
        "identityToken": &format!("XBL3.0 x={};{}",
            xsts_token.display_claims["xui"][0]["uhs"].as_str().unwrap_or(""),
            xsts_token.token
        )
    });
    let resp: McAuthResponse = client
        .post("https://api.minecraftservices.com/authentication/login_with_xbox")
        .json(&payload)
        .send()
        .await
        .map_err(|e| format!("Minecraft 认证失败: {}", e))?
        .json()
        .await
        .map_err(|e| format!("Minecraft Token 解析失败: {}", e))?;
    Ok(resp)
}

/// 获取 Minecraft 用户资料
async fn get_minecraft_profile(mc_token: &McAuthResponse) -> Result<UserProfile, String> {
    let client = reqwest::Client::new();

    // 获取玩家资料
    let profile_resp: McProfileResponse = client
        .get("https://api.minecraftservices.com/minecraft/profile")
        .bearer_auth(&mc_token.access_token)
        .send()
        .await
        .map_err(|e| format!("获取资料失败: {}", e))?
        .json()
        .await
        .map_err(|e| format!("解析资料失败: {}", e))?;

    let skin_url = profile_resp
        .skins
        .iter()
        .find(|s| s.state == "ACTIVE")
        .map(|s| s.url.clone());

    Ok(UserProfile {
        name: profile_resp.name,
        uuid: profile_resp.id,
        skin_url,
    })
}

// ========= PKCE =========

fn generate_pkce() -> (String, String) {
    use rand::Rng;
    use sha2::{Digest, Sha256};

    let mut rng = rand::thread_rng();
    let mut bytes = [0u8; 32];
    rng.fill(&mut bytes);
    let verifier = base64_url_no_pad(&bytes);

    let mut hasher = Sha256::new();
    hasher.update(verifier.as_bytes());
    let hash = hasher.finalize();
    let challenge = base64_url_no_pad(&hash);

    (verifier, challenge)
}

fn base64_url_no_pad(data: &[u8]) -> String {
    use base64::Engine;
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(data)
}

fn urlencoding(s: &str) -> String {
    let mut result = String::new();
    for byte in s.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => result.push(byte as char),
            _ => result.push_str(&format!("%{:02X}", byte)),
        }
    }
    result
}

/// 生成离线模式 UUID（基于用户名）
fn offline_uuid(username: &str) -> String {
    use std::hash::{Hash, Hasher};
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    format!("OfflinePlayer:{}", username).hash(&mut hasher);
    let hash = hasher.finish();
    format!("{:032x}", hash)
}
