/**
 * 前端错误码映射表
 *
 * 将 Rust 后端（anyhow、std::io、reqwest 等）的原始错误消息转换为用户可理解的中文提示。
 * 使用方式:
 *   import { friendlyError } from "../errorMessages";
 *   catch (e) { setMsg({ text: friendlyError(e), err: true }); }
 *
 * 匹配规则从通用到具体排列，优先级高的在前。
 */

interface ErrorMapping {
  /** 用于匹配原始错误消息的正则（不区分大小写） */
  pattern: RegExp;
  /** 用户友好的中文消息 */
  message: string;
  /** 是否为严重错误（影响后续操作） */
  critical?: boolean;
}

const MAPPINGS: ErrorMapping[] = [
  // ---- 网络 ----
  {
    pattern: /connection\s*(refused|reset|timed?\s*out)/i,
    message: "无法连接到服务器，请检查网络或切换下载源",
    critical: false,
  },
  {
    pattern: /dns\s*(error|resolve|not found)/i,
    message: "域名解析失败，请检查网络连接或 DNS 设置",
    critical: false,
  },
  {
    pattern: /tls|ssl|certificate/i,
    message: "安全连接失败，可能因网络代理或防火墙拦截",
    critical: false,
  },
  {
    pattern: /request.*timeout/i,
    message: "请求超时，服务器响应过慢，请稍后重试或切换下载源",
    critical: false,
  },
  {
    pattern: /too many (requests|redirect)/i,
    message: "请求过于频繁，请稍等片刻再试",
    critical: false,
  },
  {
    pattern: /503|service\s*unavailable|gateway/i,
    message: "下载源暂时不可用（服务器繁忙），请稍后重试或切换其他源",
    critical: false,
  },
  {
    pattern: /404|not\s*found/i,
    message: "请求的文件不存在，可能是版本已下架或资源链接失效",
    critical: false,
  },
  {
    pattern: /403|forbidden/i,
    message: "下载被拒绝，可能是下载源限制了访问，请尝试切换下载源",
    critical: false,
  },

  // ---- 文件系统 ----
  {
    pattern: /permission\s*denied|access\s*is\s*denied|os error 5/i,
    message: "文件权限不足，请以管理员身份运行或检查目录权限",
    critical: true,
  },
  {
    pattern: /disk\s*full|no\s*space|os error 112|os error 28/i,
    message: "磁盘空间不足，请清理磁盘后重试",
    critical: true,
  },
  {
    pattern: /file\s*not\s*found|no\s*such\s*file|os error 2/i,
    message: "找不到所需文件，请重新安装或检查文件完整性",
    critical: true,
  },
  {
    pattern: /already\s*exists|os error 80/i,
    message: "文件已存在，如需覆盖请先删除旧文件",
    critical: false,
  },
  {
    pattern: /path\s*too\s*long|os error 100/i,
    message: "文件路径过长，请将游戏目录移动到更短的路径",
    critical: true,
  },
  {
    pattern: /read-only|os error 30/i,
    message: "文件为只读状态，无法写入，请检查目录属性",
    critical: true,
  },

  // ---- 配置 ----
  {
    pattern: /config.*(parse|invalid|malformed|deserialize|corrupt)/i,
    message: "配置文件损坏，将自动重置为默认设置",
    critical: false,
  },
  {
    pattern: /config.*(write|save|serialize)/i,
    message: "无法保存配置文件，请检查磁盘空间和目录权限",
    critical: true,
  },

  // ---- Java ----
  {
    pattern: /java.*(not\s*found|not\s*installed|missing)/i,
    message: "找不到 Java 运行环境，请前往设置页检测并安装 Java",
    critical: true,
  },
  {
    pattern: /java.*version.*(mismatch|unsupported|too\s*old)/i,
    message: "Java 版本不满足游戏要求，请安装更高版本的 Java",
    critical: true,
  },
  {
    pattern: /java.*(memory|heap|out\s*of\s*memory|gc\s*overhead)/i,
    message: "游戏内存不足，请在设置中增加最大内存分配",
    critical: false,
  },

  // ---- 下载 / 安装 ----
  {
    pattern: /checksum|hash.*mismatch|integrity.*check/i,
    message: "文件校验失败，下载数据可能损坏，正在自动重试",
    critical: false,
  },
  {
    pattern: /download.*(failed|error|interrupted)/i,
    message: "下载失败，请检查网络连接或切换下载源后重试",
    critical: false,
  },
  {
    pattern: /install.*(failed|error)/i,
    message: "安装失败，请检查磁盘空间和目录权限后重试",
    critical: true,
  },

  // ---- 账户 ----
  {
    pattern: /auth.*(failed|error|invalid|expired)/i,
    message: "账户验证失败，请重新登录或刷新令牌",
    critical: false,
  },
  {
    pattern: /device.*code.*(expired|invalid)/i,
    message: "登录验证码已过期，请重新发起登录",
    critical: false,
  },

  // ---- 进程 ----
  {
    pattern: /process.*(exit|killed|terminated|crash)/i,
    message: "游戏进程异常退出，可能是游戏崩溃或内存不足",
    critical: false,
  },
  {
    pattern: /process.*(spawn|launch|start).*(error|failed)/i,
    message: "无法启动游戏进程，请检查 Java 路径和版本设置",
    critical: true,
  },

  // ---- 通用 ----
  {
    pattern: /timeout/i,
    message: "操作超时，请检查网络连接后重试",
    critical: false,
  },
  {
    pattern: /cancelled|canceled/i,
    message: "操作已取消",
    critical: false,
  },
];

/**
 * 将原始错误转换为人性化消息。
 * 如果没匹配到任何模式，返回带中文上下文的原始错误（截断过长内容）。
 * 原始错误全文始终可复制——错误提示容器应使用 .selectable。
 */
export function friendlyError(error: unknown): string {
  const raw = String(error);
  for (const { pattern, message } of MAPPINGS) {
    if (pattern.test(raw)) {
      return message;
    }
  }
  const clean = raw.replace(/^Error:\s*/i, "").trim();
  if (!clean) return "操作失败，请稍后重试";
  const truncated = clean.length > 160 ? clean.slice(0, 160) + "…" : clean;
  return `操作失败：${truncated}`;
}

/**
 * 判断错误是否为严重级别（阻断后续操作）。
 */
export function isCriticalError(error: unknown): boolean {
  const raw = String(error);
  for (const { pattern, critical } of MAPPINGS) {
    if (critical && pattern.test(raw)) {
      return true;
    }
  }
  return false;
}