//! 账户服务 - 账户持久化与登录/续期的高层封装

use crate::account::microsoft::TokenPollError;
use crate::account::store::{AccountStore, StoredAccount};
use crate::account::{MicrosoftAuth, MicrosoftLoginResult, OfflineAccount};
use crate::error::{LauncherError, Result};
use crate::plugin::{PluginContext, PluginManager, PluginStage};
use std::path::PathBuf;
use std::sync::Arc;

/// 微软设备码登录的轮询状态（供 GUI 展示与继续轮询判断）
#[derive(Debug, Clone)]
pub enum MicrosoftLoginPoll {
    /// 等待用户授权，message 为给用户的提示，应稍后继续轮询
    Pending(String),
    /// 登录成功，账户已保存
    Success(StoredAccount),
}

/// 账户服务 - 登录/注销/切换 + accounts.json 持久化 + 插件事件广播
pub struct AccountService {
    store: AccountStore,
    plugin_manager: Option<Arc<PluginManager>>,
}

impl AccountService {
    /// 创建账户服务（默认使用数据目录下的 accounts.json）
    pub fn new() -> Self {
        Self {
            store: AccountStore::new(AccountStore::default_path()),
            plugin_manager: None,
        }
    }

    /// 指定账户存储路径（测试/自定义数据目录用）
    pub fn with_store_path(store_path: impl Into<PathBuf>) -> Self {
        Self {
            store: AccountStore::new(store_path),
            plugin_manager: None,
        }
    }

    /// 关联插件管理器（事件广播）
    pub fn with_plugin_manager(mut self, plugin_manager: Arc<PluginManager>) -> Self {
        self.plugin_manager = Some(plugin_manager);
        self
    }

    /// 访问底层存储（供高级调用）
    pub fn store(&self) -> &AccountStore {
        &self.store
    }

    /// 广播账户变更事件（失败仅记录警告）
    fn fire_account_changed(&self) {
        if let Some(pm) = &self.plugin_manager {
            let mut ctx = PluginContext::new(PathBuf::from("."));
            if let Err(e) = pm.fire_event(PluginStage::AccountChanged, &mut ctx) {
                log::warn!("账户变更事件广播失败: {}", e);
            }
        }
    }

    /// 创建离线账户并保存（若尚无任何账户则设为默认）
    pub async fn add_offline(&self, username: &str) -> Result<StoredAccount> {
        let account = OfflineAccount::new(username)?;
        let mut stored = StoredAccount::offline(&account.uuid, &account.username);
        stored.is_default = self.store.load().await?.is_empty();
        self.store.upsert(stored.clone()).await?;
        self.fire_account_changed();
        log::info!("已保存离线账户: {} ({})", stored.username, stored.uuid);
        Ok(stored)
    }

    /// 保存微软登录结果（若尚无任何账户则设为默认）
    pub async fn add_microsoft(&self, result: MicrosoftLoginResult) -> Result<StoredAccount> {
        let mut stored = StoredAccount::microsoft(
            result.uuid,
            result.username,
            result.access_token,
            result.refresh_token,
        );
        stored.is_default = self.store.load().await?.is_empty();
        self.store.upsert(stored.clone()).await?;
        self.fire_account_changed();
        log::info!("已保存微软账户: {} ({})", stored.username, stored.uuid);
        Ok(stored)
    }

    /// 删除账户
    pub async fn remove(&self, uuid: &str) -> Result<bool> {
        let removed = self.store.remove(uuid).await?;
        if removed {
            self.fire_account_changed();
            log::info!("已删除账户: {}", uuid);
        }
        Ok(removed)
    }

    /// 设置默认账户
    pub async fn set_default(&self, uuid: &str) -> Result<()> {
        self.store.set_default(uuid).await?;
        self.fire_account_changed();
        Ok(())
    }

    /// 列出全部账户
    pub async fn list(&self) -> Result<Vec<StoredAccount>> {
        self.store.load().await
    }

    /// 获取默认账户（无默认时取第一个）
    pub async fn default(&self) -> Result<Option<StoredAccount>> {
        self.store.default_account().await
    }

    /// 微软账户 refresh_token 全链路续期并更新存储
    pub async fn refresh_microsoft(&self, uuid: &str) -> Result<StoredAccount> {
        let accounts = self.store.load().await?;
        let account = accounts
            .iter()
            .find(|a| a.uuid == uuid)
            .ok_or_else(|| LauncherError::AccountError(format!("账户不存在: {}", uuid)))?;
        let refresh_token = account
            .refresh_token
            .clone()
            .ok_or_else(|| LauncherError::AccountError("该账户没有可用的刷新令牌".to_string()))?;

        let auth = MicrosoftAuth::new();
        let result = auth.refresh_login(&refresh_token).await?;
        log::info!("微软账户续期成功: {}", result.username);
        self.add_microsoft(result).await
    }

    /// 发起微软设备码登录（返回设备码，供 GUI/CLI 展示给用户）
    pub async fn start_microsoft_login(
        &self,
    ) -> Result<crate::account::microsoft::DeviceCodeResponse> {
        MicrosoftAuth::new().get_device_code().await
    }

    /// 轮询并完成微软设备码登录（阻塞直到授权完成或超时）
    pub async fn complete_microsoft_login(&self, device_code: &str) -> Result<StoredAccount> {
        let auth = MicrosoftAuth::new();
        let result = auth.full_login(device_code, 5, 240).await?;
        self.add_microsoft(result).await
    }

    /// 单次轮询微软设备码登录（供 GUI 按 interval 间隔循环调用）
    ///
    /// - `Pending`：用户尚未完成授权，按返回提示稍后继续轮询
    /// - `Success`：授权完成，账户已持久化
    /// - `Err`：授权失败/过期/拒绝等终止性错误
    pub async fn poll_microsoft_login(&self, device_code: &str) -> Result<MicrosoftLoginPoll> {
        let auth = MicrosoftAuth::new();
        match auth.poll_token(device_code).await {
            Ok(token) => {
                let result = auth
                    .complete_auth_chain(token.access_token, token.refresh_token)
                    .await?;
                let stored = self.add_microsoft(result).await?;
                Ok(MicrosoftLoginPoll::Success(stored))
            }
            Err(TokenPollError::Pending) => {
                Ok(MicrosoftLoginPoll::Pending("等待用户完成授权…".into()))
            }
            Err(TokenPollError::SlowDown) => Ok(MicrosoftLoginPoll::Pending(
                "轮询过于频繁，请稍后再试".into(),
            )),
            Err(e) => Err(LauncherError::AccountError(e.to_string())),
        }
    }
}

impl Default for AccountService {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn test_account_service_crud() {
        let dir = tempfile::tempdir().unwrap();
        let svc = AccountService::with_store_path(dir.path().join("accounts.json"));

        // 添加离线账户
        let acc = svc.add_offline("Player").await.unwrap();
        assert!(acc.is_default, "首个账户应设为默认");

        // 第二个账户不是默认
        let acc2 = svc.add_offline("Player2").await.unwrap();
        assert!(!acc2.is_default);

        // 切换默认
        svc.set_default(&acc2.uuid).await.unwrap();
        let def = svc.default().await.unwrap().unwrap();
        assert_eq!(def.uuid, acc2.uuid);

        // 删除
        assert!(svc.remove(&acc.uuid).await.unwrap());
        assert_eq!(svc.list().await.unwrap().len(), 1);

        // 删除不存在的账户
        assert!(!svc.remove("nonexistent").await.unwrap());
    }
}
