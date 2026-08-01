use crate::version::manifest::{VersionEntry, VersionType};

/// 版本比较器 - 支持语义化版本比较
pub struct VersionComparator;

/// 版本比较结果
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum VersionDiff {
    /// 版本相同
    Same,
    /// 有更新版本
    Newer { id: String },
    /// 当前版本较新
    Older,
    /// 无法比较
    Incomparable,
}

impl VersionComparator {
    /// 比较两个版本 ID（如 "1.20.1" 和 "1.21"）
    pub fn compare(a: &str, b: &str) -> std::cmp::Ordering {
        let va = parse_version(a);
        let vb = parse_version(b);
        va.cmp(&vb)
    }

    /// 获取最新版本
    pub fn latest<'a>(versions: &'a [VersionEntry]) -> Option<&'a VersionEntry> {
        versions
            .iter()
            .filter(|v| v.version_type == VersionType::Release)
            .max_by(|a, b| Self::compare(&a.id, &b.id).then_with(|| a.release_time.cmp(&b.release_time)))
    }

    /// 获取最旧版本
    pub fn oldest<'a>(versions: &'a [VersionEntry]) -> Option<&'a VersionEntry> {
        versions
            .iter()
            .filter(|v| v.version_type == VersionType::Release)
            .min_by(|a, b| Self::compare(&a.id, &b.id).then_with(|| a.release_time.cmp(&b.release_time)))
    }

    /// 检测版本是否为快照版
    pub fn is_snapshot(version_id: &str) -> bool {
        version_id.contains('w') || version_id.contains("Pre") || version_id.contains("pre")
    }

    /// 检测版本是否为正式发布版
    pub fn is_release(version_id: &str) -> bool {
        !Self::is_snapshot(version_id)
    }

    /// 获取两个版本之间的差异方向
    pub fn diff(current: &str, target: &str) -> VersionDiff {
        let cmp = Self::compare(current, target);
        match cmp {
            std::cmp::Ordering::Less => VersionDiff::Newer { id: target.to_string() },
            std::cmp::Ordering::Equal => VersionDiff::Same,
            std::cmp::Ordering::Greater => VersionDiff::Older,
        }
    }

    /// 按版本范围筛选
    pub fn filter_range<'a>(
        versions: &'a [VersionEntry],
        min: Option<&str>,
        max: Option<&str>,
        include_snapshots: bool,
    ) -> Vec<&'a VersionEntry> {
        versions
            .iter()
            .filter(|v| {
                if !include_snapshots && Self::is_snapshot(&v.id) {
                    return false;
                }
                if let Some(min_ver) = min {
                    if Self::compare(&v.id, min_ver) == std::cmp::Ordering::Less {
                        return false;
                    }
                }
                if let Some(max_ver) = max {
                    if Self::compare(&v.id, max_ver) == std::cmp::Ordering::Greater {
                        return false;
                    }
                }
                true
            })
            .collect()
    }

    /// 检测版本 ID 是否合法（基本格式检查）
    pub fn is_valid_id(id: &str) -> bool {
        if id.is_empty() || id.len() > 64 {
            return false;
        }
        // 版本 ID 通常为 "数字.数字.数字" 格式，快照包含字母
        id.chars().all(|c| c.is_alphanumeric() || c == '.' || c == '-' || c == '_')
    }
}

/// 解析版本字符串为元组 (主版本, 次版本, 修订版)
fn parse_version(version: &str) -> (u32, u32, u32) {
    let re = regex::Regex::new(r"^(\d+)\.(\d+)(?:\.(\d+))?").unwrap();
    if let Some(caps) = re.captures(version) {
        let major = caps.get(1).and_then(|m| m.as_str().parse().ok()).unwrap_or(0);
        let minor = caps.get(2).and_then(|m| m.as_str().parse().ok()).unwrap_or(0);
        let patch = caps.get(3).and_then(|m| m.as_str().parse().ok()).unwrap_or(0);
        (major, minor, patch)
    } else {
        (0, 0, 0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_version_comparison() {
        assert_eq!(VersionComparator::compare("1.20.1", "1.21"), std::cmp::Ordering::Less);
        assert_eq!(VersionComparator::compare("1.21", "1.21"), std::cmp::Ordering::Equal);
        assert_eq!(VersionComparator::compare("1.21.1", "1.21"), std::cmp::Ordering::Greater);
        assert_eq!(VersionComparator::compare("1.8", "1.20"), std::cmp::Ordering::Less);
    }

    #[test]
    fn test_is_snapshot() {
        assert!(VersionComparator::is_snapshot("24w01a"));
        assert!(VersionComparator::is_snapshot("1.21-pre1"));
        assert!(!VersionComparator::is_snapshot("1.20.1"));
    }

    #[test]
    fn test_is_valid_id() {
        assert!(VersionComparator::is_valid_id("1.20.1"));
        assert!(VersionComparator::is_valid_id("24w01a"));
        assert!(!VersionComparator::is_valid_id(""));
        assert!(!VersionComparator::is_valid_id("<>invalid"));
    }

    #[test]
    fn test_diff() {
        assert_eq!(
            VersionComparator::diff("1.20", "1.21"),
            VersionDiff::Newer { id: "1.21".to_string() }
        );
        assert_eq!(VersionComparator::diff("1.21", "1.21"), VersionDiff::Same);
        assert_eq!(VersionComparator::diff("1.21", "1.20"), VersionDiff::Older);
    }
}
