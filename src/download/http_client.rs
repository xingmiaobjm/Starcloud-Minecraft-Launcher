use crate::error::{LauncherError, Result};
use crate::download::sources::{self, SourceKind};
use std::time::Duration;

/// 下载重试配置
#[derive(Debug, Clone)]
pub struct RetryConfig {
    /// 最大重试次数
    pub max_retries: u32,
    /// 初始重试间隔
    pub initial_delay: Duration,
    /// 最大重试间隔
    pub max_delay: Duration,
    /// 退避因子（指数退避）
    pub backoff_factor: f64,
    /// 可重试的 HTTP 状态码
    pub retryable_statuses: Vec<u16>,
}

impl Default for RetryConfig {
    fn default() -> Self {
        Self {
            max_retries: 3,
            initial_delay: Duration::from_secs(1),
            max_delay: Duration::from_secs(60),
            backoff_factor: 2.0,
            retryable_statuses: vec![408, 429, 500, 502, 503, 504],
        }
    }
}

impl RetryConfig {
    /// 执行重试逻辑
    pub async fn retry<F, Fut, T>(&self, op: F) -> Result<T>
    where
        F: Fn() -> Fut,
        Fut: std::future::Future<Output = Result<T>>,
    {
        let mut attempt = 0u32;
        let mut delay = self.initial_delay;

        loop {
            attempt += 1;
            match op().await {
                Ok(result) => return Ok(result),
                Err(e) => {
                    if attempt >= self.max_retries {
                        return Err(LauncherError::DownloadError(format!(
                            "下载失败，已重试 {} 次: {}",
                            self.max_retries, e
                        )));
                    }

                    log::warn!(
                        "下载失败 (第 {} 次), 将在 {:?} 后重试: {}",
                        attempt,
                        delay,
                        e
                    );
                    tokio::time::sleep(delay).await;

                    // 指数退避
                    delay = Duration::from_secs_f64(
                        (delay.as_secs_f64() * self.backoff_factor)
                            .min(self.max_delay.as_secs_f64()),
                    );
                }
            }
        }
    }
}

/// HTTP 请求封装（带压缩、重试、超时）
pub struct HttpClient {
    inner: reqwest::Client,
    retry_config: RetryConfig,
}

impl HttpClient {
    /// 创建带优化的 HTTP 客户端
    pub fn new() -> Result<Self> {
        let client = reqwest::Client::builder()
            .gzip(true)
            .brotli(true)
            .deflate(true)
            .connect_timeout(Duration::from_secs(15))
            .timeout(Duration::from_secs(300))
            .tcp_keepalive(Some(Duration::from_secs(60)))
            .tcp_nodelay(true)
            .pool_max_idle_per_host(10)
            .pool_idle_timeout(Some(Duration::from_secs(90)))
            .build()?;

        Ok(Self {
            inner: client,
            retry_config: RetryConfig::default(),
        })
    }

    /// 自定义重试配置
    pub fn with_retry(mut self, config: RetryConfig) -> Self {
        self.retry_config = config;
        self
    }

    /// 获取内部客户端
    pub fn inner(&self) -> &reqwest::Client {
        &self.inner
    }

    /// 带重试的 GET 请求（自动走活动镜像源，镜像失败时回退官方地址）
    pub async fn get_with_retry(&self, url: &str) -> Result<reqwest::Response> {
        let sources = sources::healthy_sources();
        self.get_with_fallback(url, &sources).await
    }

    /// 按优先级遍历所有源下载（每个源内部带重试），失败时自动尝试下一个源
    pub async fn get_with_fallback(
        &self,
        url: &str,
        priority_sources: &[SourceKind],
    ) -> Result<reqwest::Response> {
        let mut last_err = None;
        for &kind in priority_sources {
            let rewritten = sources::rewrite_url_for(kind, url);
            match self.get_with_retry_single(&rewritten).await {
                Ok(resp) => {
                    sources::record_source_success(kind);
                    return Ok(resp);
                }
                Err(e) => {
                    log::warn!("源 {} 下载失败: {} (url={})", kind.name(), e, rewritten);
                    sources::record_source_failure(kind);
                    last_err = Some(e);
                }
            }
        }
        Err(last_err.unwrap_or_else(|| {
            LauncherError::DownloadError("所有下载源均失败".to_string())
        }))
    }

    /// 单个地址的重试逻辑（内部实现）
    async fn get_with_retry_single(&self, url: &str) -> Result<reqwest::Response> {
        let url = url.to_string();
        let retryable = self.retry_config.retryable_statuses.clone();
        let client = self.inner.clone();
        self.retry_config
            .retry(move || {
                let url = url.clone();
                let client = client.clone();
                let retryable = retryable.clone();
                async move {
                    let response = client.get(&url).send().await?;
                    let status = response.status().as_u16();
                    if retryable.contains(&status) {
                        return Err(LauncherError::DownloadError(format!(
                            "HTTP {}: {}",
                            status, url
                        )));
                    }
                    if !response.status().is_success() {
                        return Err(LauncherError::DownloadError(format!(
                            "HTTP {}: {}",
                            status, url
                        )));
                    }
                    Ok(response)
                }
            })
            .await
    }

    /// 带重试的 GET 请求并返回 bytes（自动走健康源列表，逐源回退）
    pub async fn get_bytes_with_retry(&self, url: &str) -> Result<Vec<u8>> {
        let sources = sources::healthy_sources();
        self.get_bytes_with_fallback(url, &sources).await
    }

    /// 按优先级遍历所有源下载 bytes（每个源内部带重试），失败时自动尝试下一个源
    pub async fn get_bytes_with_fallback(
        &self,
        url: &str,
        priority_sources: &[SourceKind],
    ) -> Result<Vec<u8>> {
        let mut last_err = None;
        for &kind in priority_sources {
            let rewritten = sources::rewrite_url_for(kind, url);
            match self.get_bytes_with_retry_single(&rewritten).await {
                Ok(bytes) => {
                    sources::record_source_success(kind);
                    return Ok(bytes);
                }
                Err(e) => {
                    log::warn!("源 {} 下载失败: {} (url={})", kind.name(), e, rewritten);
                    sources::record_source_failure(kind);
                    last_err = Some(e);
                }
            }
        }
        Err(last_err.unwrap_or_else(|| {
            LauncherError::DownloadError("所有下载源均失败".to_string())
        }))
    }

    /// 单个地址的重试逻辑（内部实现）
    async fn get_bytes_with_retry_single(&self, url: &str) -> Result<Vec<u8>> {
        let url = url.to_string();
        let retryable = self.retry_config.retryable_statuses.clone();
        let client = self.inner.clone();
        self.retry_config
            .retry(move || {
                let url = url.clone();
                let client = client.clone();
                let retryable = retryable.clone();
                async move {
                    let response = client.get(&url).send().await?;
                    let status = response.status().as_u16();
                    if retryable.contains(&status) {
                        return Err(LauncherError::DownloadError(format!(
                            "HTTP {}: {}",
                            status, url
                        )));
                    }
                    if !response.status().is_success() {
                        return Err(LauncherError::DownloadError(format!(
                            "HTTP {}: {}",
                            status, url
                        )));
                    }
                    Ok(response.bytes().await?.to_vec())
                }
            })
            .await
    }

    /// 带重试的 GET 请求并返回 JSON 反序列化结果
    pub async fn get_json_with_retry<T: serde::de::DeserializeOwned>(
        &self,
        url: &str,
    ) -> Result<T> {
        let response = self.get_with_retry(url).await?;
        let result: T = response.json().await?;
        Ok(result)
    }
}

impl Clone for HttpClient {
    fn clone(&self) -> Self {
        Self {
            inner: self.inner.clone(),
            retry_config: self.retry_config.clone(),
        }
    }
}

impl std::fmt::Debug for HttpClient {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HttpClient")
            .field("retry_config", &self.retry_config)
            .finish()
    }
}
