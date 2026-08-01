use crate::version::manifest::{
    ArgumentValue, ArgumentValueInner, Rule, RuleAction, VersionDetails,
};
use std::collections::HashMap;

/// 操作系统类型
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum OsKind {
    Windows,
    Linux,
    MacOS,
}

impl OsKind {
    /// 获取 Mojang 格式的 os name
    pub fn as_mojang(&self) -> &str {
        match self {
            OsKind::Windows => "windows",
            OsKind::Linux => "linux",
            OsKind::MacOS => "osx",
        }
    }

    /// 自动检测当前系统
    pub fn detect() -> Self {
        if cfg!(target_os = "windows") {
            OsKind::Windows
        } else if cfg!(target_os = "macos") {
            OsKind::MacOS
        } else {
            OsKind::Linux
        }
    }

    /// 获取系统架构
    pub fn arch() -> &'static str {
        std::env::consts::ARCH
    }
}

/// JVM 参数构建器
pub struct ArgumentsBuilder {
    /// 版本详情
    version: VersionDetails,
    /// 操作系统
    os: OsKind,
    /// 特征映射（如 is_demo_user, has_custom_resolution）
    features: HashMap<String, bool>,
    /// 替换变量
    replacements: HashMap<String, String>,
}

impl ArgumentsBuilder {
    /// 创建参数构建器
    pub fn new(version: VersionDetails, os: OsKind) -> Self {
        let mut features = HashMap::new();
        features.insert("is_demo_user".to_string(), false);
        features.insert("has_custom_resolution".to_string(), false);

        Self {
            version,
            os,
            features,
            replacements: HashMap::new(),
        }
    }

    /// 设置自定义分辨率特性
    pub fn with_custom_resolution(mut self) -> Self {
        self.features
            .insert("has_custom_resolution".to_string(), true);
        self
    }

    /// 设置 Demo 模式
    pub fn with_demo(mut self) -> Self {
        self.features.insert("is_demo_user".to_string(), true);
        self
    }

    /// 添加替换变量
    pub fn with_replacement(mut self, key: impl Into<String>, value: impl Into<String>) -> Self {
        self.replacements.insert(key.into(), value.into());
        self
    }

    /// 构建 JVM 参数列表
    pub fn build_jvm_args(&self) -> Vec<String> {
        let mut args: Vec<String> = Vec::new();

        if let Some(arguments) = &self.version.arguments {
            for arg in &arguments.jvm {
                match arg {
                    ArgumentValue::Simple(s) => {
                        args.push(apply_replacements(s, &self.replacements));
                    }
                    ArgumentValue::Conditional(cond) => {
                        if check_rules(&cond.rules, self.os, &self.features) {
                            match &cond.value {
                                ArgumentValueInner::Single(s) => {
                                    args.push(apply_replacements(s, &self.replacements));
                                }
                                ArgumentValueInner::Multiple(list) => {
                                    for s in list {
                                        args.push(apply_replacements(s, &self.replacements));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        } else if let Some(legacy_args) = &self.version.minecraft_arguments {
            // 旧版本使用 minecraftArguments 格式
            let parts: Vec<&str> = legacy_args.split_whitespace().collect();
            for part in parts {
                args.push(part.to_string());
            }
        }

        // 添加日志参数
        if let Some(result) = self
            .version
            .logging
            .client
            .argument
            .strip_prefix("-D")
            .map(|s| s.to_string())
        {
            let log_arg = format!("-D{}", apply_replacements(&result, &self.replacements));
            args.push(log_arg);
        }

        args
    }

    /// 构建游戏参数列表
    pub fn build_game_args(&self) -> Vec<String> {
        let mut args: Vec<String> = Vec::new();

        if let Some(arguments) = &self.version.arguments {
            for arg in &arguments.game {
                match arg {
                    ArgumentValue::Simple(s) => {
                        args.push(apply_replacements(s, &self.replacements));
                    }
                    ArgumentValue::Conditional(cond) => {
                        if check_rules(&cond.rules, self.os, &self.features) {
                            match &cond.value {
                                ArgumentValueInner::Single(s) => {
                                    args.push(apply_replacements(s, &self.replacements));
                                }
                                ArgumentValueInner::Multiple(list) => {
                                    for s in list {
                                        args.push(apply_replacements(s, &self.replacements));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        args
    }

    /// 获取主类名
    pub fn main_class(&self) -> &str {
        &self.version.main_class
    }

    /// 获取资源目录名
    pub fn assets_id(&self) -> String {
        self.version
            .assets
            .clone()
            .unwrap_or_else(|| self.version.id.clone())
    }
}

/// 检查规则是否通过
fn check_rules(rules: &[Rule], os: OsKind, features: &HashMap<String, bool>) -> bool {
    if rules.is_empty() {
        return true;
    }

    let mut allowed = false;
    for rule in rules {
        let os_match = rule
            .os
            .as_ref()
            .is_none_or(|os_rule| os_rule.name.as_deref().is_none_or(|n| n == os.as_mojang()));

        let feature_match = rule.features.as_ref().is_none_or(|feats| {
            feats
                .iter()
                .all(|(key, expected)| features.get(key).copied().unwrap_or(false) == *expected)
        });

        if os_match && feature_match {
            allowed = match rule.action {
                RuleAction::Allow => true,
                RuleAction::Disallow => false,
            };
        }
    }

    allowed
}

/// 应用替换变量 (如 ${launcher_name} -> "FurryLauncher")
fn apply_replacements(input: &str, replacements: &HashMap<String, String>) -> String {
    let mut result = input.to_string();
    for (key, value) in replacements {
        let placeholder = format!("${{{}}}", key);
        result = result.replace(&placeholder, value);
    }
    result
}
