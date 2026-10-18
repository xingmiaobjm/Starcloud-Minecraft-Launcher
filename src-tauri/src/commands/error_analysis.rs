// 错误分析命令 - Crash Report 和日志解析
// StarCloud Minecraft Launcher - LGPL-3.0-only

use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;

#[derive(Serialize, Deserialize, Clone)]
pub struct CrashAnalysis {
    pub time: String,
    pub crash_type: String,
    pub description: String,
    pub java_version: String,
    pub errors: Vec<ErrorEntry>,
    pub suggestions: Vec<String>,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct ErrorEntry {
    pub pattern: String,
    pub message: String,
    pub line: u32,
    pub severity: String,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct LogAnalysis {
    pub errors: Vec<ErrorEntry>,
    pub warnings: Vec<ErrorEntry>,
    pub summary: String,
}

#[derive(Serialize, Deserialize, Clone)]
pub struct ErrorStat {
    pub error_type: String,
    pub count: u32,
    pub last_occurrence: String,
}

/// 已知错误模式及修复建议
const ERROR_PATTERNS: &[(&str, &str, &str)] = &[
    ("Unsupported class file major version", "error", "Java 版本不匹配，请安装正确的 Java 版本"),
    ("OutOfMemoryError", "error", "内存不足，建议增加 -Xmx 启动参数"),
    ("ModLoadingException", "error", "Mod 加载失败，可能存在 Mod 冲突"),
    ("Mixin apply failed", "error", "Mixin 注入失败，检查 Mod 兼容性"),
    ("NoClassDefFoundError", "error", "缺少依赖类，检查 Mod/插件版本"),
    ("LinkageError", "error", "类链接错误，可能存在重复或冲突的依赖"),
    ("Corrupted chunk data", "error", "区块数据损坏，建议修复或删除损坏区块"),
    ("NBTException", "error", "NBT 数据异常，可能是世界文件损坏"),
    ("OpenGL", "error", "OpenGL 错误，请更新显卡驱动"),
    ("Pixel Format Not Accelerated", "error", "显卡加速不可用，请更新显卡驱动"),
    ("Connection refused", "warning", "连接被拒绝，检查服务器状态"),
    ("Can't keep up", "warning", "服务器负载过高，考虑优化或升级硬件"),
    ("moved too quickly", "warning", "玩家移动异常，可能是延迟或作弊"),
    ("Can't resolve hostname", "error", "DNS 解析失败，检查网络连接"),
];

fn get_minecraft_dir() -> PathBuf {
    let base = dirs::data_dir().unwrap_or_else(|| PathBuf::from("."));
    base.join(".starcloud_minecraft")
}

// ========= 命令 =========

/// 分析最新的崩溃报告
#[tauri::command]
pub fn analyze_crash_report() -> Result<Option<CrashAnalysis>, String> {
    let mc_dir = get_minecraft_dir();
    let crash_dir = mc_dir.join("crash-reports");

    if !crash_dir.exists() {
        return Ok(None);
    }

    // 找到最新的崩溃报告
    let mut latest_path: Option<PathBuf> = None;
    let mut latest_time = std::time::SystemTime::UNIX_EPOCH;

    if let Ok(entries) = fs::read_dir(&crash_dir) {
        for entry in entries.flatten() {
            if let Ok(meta) = entry.metadata() {
                if let Ok(modified) = meta.modified() {
                    if modified > latest_time
                        && entry.path().extension().map_or(false, |e| e == "txt")
                    {
                        latest_time = modified;
                        latest_path = Some(entry.path());
                    }
                }
            }
        }
    }

    let path = match latest_path {
        Some(p) => p,
        None => return Ok(None),
    };

    let content = fs::read_to_string(&path)
        .map_err(|e| format!("读取崩溃报告失败: {}", e))?;

    let analysis = parse_crash_content(&content);
    Ok(Some(analysis))
}

/// 分析最新的游戏日志
#[tauri::command]
pub fn analyze_game_log() -> Result<Option<LogAnalysis>, String> {
    let mc_dir = get_minecraft_dir();
    let log_path = mc_dir.join("logs").join("latest.log");

    if !log_path.exists() {
        return Ok(None);
    }

    let content = fs::read_to_string(&log_path)
        .map_err(|e| format!("读取日志失败: {}", e))?;

    let analysis = parse_log_content(&content);
    Ok(Some(analysis))
}

/// 获取错误统计
#[tauri::command]
pub fn get_error_stats() -> Result<Vec<ErrorStat>, String> {
    let mc_dir = get_minecraft_dir();
    let crash_dir = mc_dir.join("crash-reports");

    if !crash_dir.exists() {
        return Ok(vec![]);
    }

    let mut stats: std::collections::HashMap<String, (u32, String)> =
        std::collections::HashMap::new();

    if let Ok(entries) = fs::read_dir(&crash_dir) {
        for dir_entry in entries.flatten() {
            if dir_entry.path().extension().map_or(false, |e| e == "txt") {
                if let Ok(content) = fs::read_to_string(dir_entry.path()) {
                    let error_type = extract_error_type(&content);
                    let stat = stats.entry(error_type.clone()).or_insert((0, String::new()));
                    stat.0 += 1;
                    if let Ok(meta) = dir_entry.path().metadata() {
                        if let Ok(time) = meta.modified() {
                            stat.1 = format!("{:?}", time);
                        }
                    }
                }
            }
        }
    }

    let result: Vec<ErrorStat> = stats
        .into_iter()
        .map(|(error_type, (count, last))| ErrorStat {
            error_type,
            count,
            last_occurrence: last,
        })
        .collect();

    Ok(result)
}

// ========= 内部辅助函数 =========

fn parse_crash_content(content: &str) -> CrashAnalysis {
    let mut crash_type = "未知".to_string();
    let mut description = String::new();
    let mut java_version = String::new();
    let mut errors = Vec::new();
    let mut suggestions: Vec<String> = Vec::new();
    let mut line_num = 0u32;

    for line in content.lines() {
        line_num += 1;

        // 提取崩溃类型
        if line.starts_with("Description:") {
            description = line
                .strip_prefix("Description:")
                .unwrap_or("")
                .trim()
                .to_string();
        }

        // 提取 Java 版本
        if line.starts_with("Java VM Version:") {
            java_version = line
                .strip_prefix("Java VM Version:")
                .unwrap_or("")
                .trim()
                .to_string();
        }

        // 匹配错误模式
        for (pattern, severity, suggestion) in ERROR_PATTERNS {
            if line.contains(pattern) {
                errors.push(ErrorEntry {
                    pattern: pattern.to_string(),
                    message: line.trim().to_string(),
                    line: line_num,
                    severity: severity.to_string(),
                });
                let sug_str = suggestion.to_string();
                if !suggestions.contains(&sug_str) {
                    suggestions.push(sug_str);
                }
            }
        }

        // 提取通用的 Exception
        if line.contains("Exception") && !crash_type.contains("Exception") {
            if let Some(ex_type) = line.split(':').next() {
                crash_type = ex_type.trim().to_string();
            }
        }
    }

    CrashAnalysis {
        time: chrono_now(),
        crash_type,
        description,
        java_version,
        errors,
        suggestions,
    }
}

fn parse_log_content(content: &str) -> LogAnalysis {
    let mut errors = Vec::new();
    let mut warnings = Vec::new();
    let mut line_num = 0u32;
    let mut error_count = 0u32;
    let mut warn_count = 0u32;
    let mut fatal_count = 0u32;

    for line in content.lines() {
        line_num += 1;

        for (pattern, severity, _) in ERROR_PATTERNS {
            if line.contains(pattern) {
                let entry = ErrorEntry {
                    pattern: pattern.to_string(),
                    message: line.trim().to_string(),
                    line: line_num,
                    severity: severity.to_string(),
                };
                if *severity == "error" {
                    error_count += 1;
                    errors.push(entry);
                } else {
                    warn_count += 1;
                    warnings.push(entry);
                }
            }
        }

        // 检测 FATAL 级别
        if line.contains("FATAL") || line.contains("fatal") {
            fatal_count += 1;
        }
    }

    let summary = format!(
        "发现 {} 个错误, {} 个警告, {} 个严重问题",
        error_count, warn_count, fatal_count
    );

    LogAnalysis {
        errors,
        warnings,
        summary,
    }
}

fn extract_error_type(content: &str) -> String {
    for line in content.lines() {
        for (pattern, _, _) in ERROR_PATTERNS {
            if line.contains(pattern) {
                return pattern.to_string();
            }
        }
    }
    "未分类错误".to_string()
}

fn chrono_now() -> String {
    use std::time::SystemTime;
    if let Ok(dur) = SystemTime::now().duration_since(SystemTime::UNIX_EPOCH) {
        let secs = dur.as_secs();
        let days = secs / 86400;
        let hours = (secs % 86400) / 3600 + 8; // UTC+8
        let mins = (secs % 3600) / 60;
        let secs = secs % 60;
        format!(
            "{:04}-{:02}-{:02} {:02}:{:02}:{:02}",
            1970 + days / 365,
            (days % 365) / 30 + 1,
            (days % 365) % 30 + 1,
            hours % 24,
            mins,
            secs
        )
    } else {
        "未知时间".to_string()
    }
}
