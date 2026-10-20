package net.kdt.pojavlaunch.utils;

/**
 * 最小 JREUtils：仅承担 libpojavexec 的加载。
 * so 不打进 APK，位于组件部署目录，由 C# 宿主注入 pojav.exec.path 属性后加载。
 */
public final class JREUtils {

    static {
        try {
            ensureLoaded(null);
        } catch (Throwable ignored) {
            // so 缺席时允许类初始化成功：宿主会在启动前显式加载
        }
    }

    private JREUtils() {
    }

    /**
     * 加载 libpojavexec：优先使用参数路径，其次 pojav.exec.path 属性，最后按库名加载。
     */
    public static void ensureLoaded(String libPath) {
        String path = libPath;
        if (path == null || path.isEmpty()) {
            path = System.getProperty("pojav.exec.path");
        }
        if (path != null && !path.isEmpty()) {
            System.load(path);
        } else {
            System.loadLibrary("pojavexec");
        }
    }
}
