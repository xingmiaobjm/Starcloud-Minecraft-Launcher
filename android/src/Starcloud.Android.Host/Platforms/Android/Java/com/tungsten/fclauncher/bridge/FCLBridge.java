package com.tungsten.fclauncher.bridge;

/**
 * FCLBridge 壳类：仅声明宿主 libpojavexec.so / libfcl.so 中已导出的 JNI 静态方法。
 *
 * 类名与方法签名必须与 native 导出符号严格对应
 * （Java_com_tungsten_fclauncher_bridge_FCLBridge_*），调用经 ART 的
 * dlsym(RTLD_DEFAULT) 回退解析到 C# 侧以 RTLD_GLOBAL 加载的 libpojavexec。
 *
 * initializeHooks 是 FCL 的钩子总装入口：内部完成 pojav_environ 填充
 * （dalvikJavaVMPtr / bridgeClazz / method_notifyLauncher）+ bytehook 初始化 +
 * create_sdl_hooks / create_sdl_dlopen_hooks / create_chmod_hooks，
 * 是 26.x SDL 路径图形后端修复的核心（强制 ES profile / 单窗口复用 /
 * EGL 属性归一化 / Vulkan 加载器一致性）。
 */
public class FCLBridge {
    /** 安装 FCL 全套 bytehook 钩子并初始化 pojav_environ（Dalvik 侧调用一次） */
    public static native void initializeHooks();

    /** dlopen 绝对路径库；plain dlopen 失败时 native 内部回退 escape 命名空间，返回句柄（0 = 失败） */
    public static native long dlopen(String name);

    /** 经 android_update_LD_LIBRARY_PATH 更新链接器搜索路径（供 DT_NEEDED 解析） */
    public static native void setLdLibraryPath(String path);

    /** 当前 Dalvik JavaVM 指针（诊断用） */
    public static native long getJavaVMPointer();

    /**
     * 注册退出陷阱回调（native 侧存本对象全局引用 + onExit 方法 ID）。
     * 必须先于 initializeHooks 调用（与 FCLauncher.launch 同序）：若钩子接管了
     * exit()，游戏退出会回调 onExit 而不是让桥指针悬空。
     */
    public native void setupExitTrap(FCLBridge bridge);

    /** 游戏退出回调（native 经 exit 陷阱调用）：仅记日志，进程收尾仍走原有路径 */
    public void onExit(int code) {
        android.util.Log.i("StarcloudSDL", "FCLBridge.onExit code=" + code);
    }

    /**
     * 组合入口：setupExitTrap → initializeHooks（与 FCLauncher.launch 的调用顺序一致）。
     * 壳类构造私有，集中在此完成，调用方（SdlGameActivity.loadLibraries）一步接入。
     */
    public static void installExitTrapAndHooks() {
        FCLBridge self = new FCLBridge();
        self.setupExitTrap(self);
        initializeHooks();
    }

    private FCLBridge() {
    }
}
