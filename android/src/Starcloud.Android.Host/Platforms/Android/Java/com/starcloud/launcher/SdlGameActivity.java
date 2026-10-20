package com.starcloud.launcher;

import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.view.View;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;

import com.tungsten.fclauncher.bridge.FCLBridge;

import org.libsdl.app.SDLActivity;

import java.util.concurrent.CountDownLatch;

/**
 * MC 26.x SDL 后端游戏活动（LWJGL ≥3.4 的 SDL 窗口/输入链）。
 *
 * 与 SDLActivity 默认形态的差异：
 * 1. libSDL3.so 不在 APK lib 目录（组件部署目录），override loadLibraries 按
 *    Intent extra 传入的完整路径 System.load（onCreate 流程 loadLibraries 先于
 *    setupJNI，加载顺序正好卡对点：nativeSetupJNI 的 native 实现在 libSDL3.so 内）；
 * 2. 加载后执行 FCL 同款图形后端修复链（intent extras 提供路径）：
 *    setLdLibraryPath（DT_NEEDED 搜索路径）→ DLOPEN 循环（mesa 库按依赖序预载，
 *    native 内部 plain dlopen 失败回退 escape 命名空间）→ setupExitTrap +
 *    initializeHooks（pojav_environ 填充 + bytehook 全套钩子：SDL_InitSubSystem
 *    launcher 集成、强制 ES profile、单窗口复用、EGL 属性归一化、Vulkan 句柄一致）；
 * 3. JVM main 由 C# 宿主线程运行，SDLThread 的 main() 不跑 nativeRunMain，
 *    只阻塞到游戏退出——若立即返回，SDLMain.run 会在 main 返回后 finish 整个活动；
 * 4. onCreate 结束置 sReady 标志：C# 侧轮询该标志确认 SDL_SetMainReady
 *    （nativeSetupJNI 内自动调用）完成后再拉起 JVM，消除启动竞态。
 */
public class SdlGameActivity extends SDLActivity {
    private static final String TAG = "StarcloudSDL";

    /** C# 侧传入 libSDL3.so 完整路径的 Intent extra 键 */
    public static final String EXTRA_SDL_LIB = "sdl_lib_path";

    /** C# 侧传入链接器搜索路径（spec.Env[LD_LIBRARY_PATH] + 系统目录）的 Intent extra 键 */
    public static final String EXTRA_LD_PATH = "ld_library_path";

    /** C# 侧传入渲染器组件目录（mesa-zink）的 Intent extra 键 */
    public static final String EXTRA_GL_DIR = "gl_dir";

    /**
     * DLOPEN 预载顺序（依赖先行）：libEGL_mesa 依赖 libglapi（组件目录）与
     * libcutils 等系统私有库；预载后 SDL 按 SDL_EGL_LIBRARY/SDL_OPENGL_LIBRARY
     * 再加载时 DT_NEEDED 按 soname 命中已加载表，规避同目录字母序问题
     */
    private static final String[] GL_DLOPEN_ORDER = {
            "libglapi.so",
            "libEGL_mesa.so",
            "libOSMesa_81.so",
            "libzink_dri.so",
            "libvulkan_freedreno.so",
            "libVkLayer_khronos_timeline_semaphore.so",
    };

    /** onCreate 完成标志（loadLibraries + setupJNI 均已就绪），C# 侧经 JNI 轮询 */
    public static volatile boolean sReady = false;

    private static CountDownLatch sExitLatch = new CountDownLatch(1);

    /** libstarcloudwindow.so 实现：以 RTLD_GLOBAL 重开自身，使符号插入生效 */
    private static native void promoteSelfGlobal();

    /** libstarcloudwindow.so 实现：改写 libSDL3.so 的 GOT，插入窗口 buffer 处理 */
    private static native void installHooks();

    @Override
    public void loadLibraries() {
        // 最先加载窗口变换修正库：靠加载顺序优先于 libnativewindow.so，插入
        // ANativeWindow_setBuffersGeometry，补写 SDL 不设置的 buffer 变换标记
        // （横屏预旋转链路的修复，详见 NativeLibs/src/starcloud_windowhook.c）
        System.loadLibrary("starcloudwindow");
        // 提升为 RTLD_GLOBAL：否则本库仅局部可见，符号插入不会被 libSDL3 的调用解析到
        promoteSelfGlobal();
        Log.i(TAG, "libstarcloudwindow.so 已加载并提升全局可见");

        Intent intent = getIntent();
        String lib = intent != null ? intent.getStringExtra(EXTRA_SDL_LIB) : null;
        if (lib == null || lib.isEmpty()) {
            throw new UnsatisfiedLinkError("缺少 Intent extra: " + EXTRA_SDL_LIB);
        }
        Log.i(TAG, "System.load " + lib);
        System.load(lib);
        // libSDL3 已载入，改写其 GOT 条目（插在符号解析层之下，免受查找顺序影响）
        installHooks();

        // ---- FCL 同款图形后端修复链（顺序不可调换）----
        String ldPath = intent.getStringExtra(EXTRA_LD_PATH);
        if (ldPath != null && !ldPath.isEmpty()) {
            FCLBridge.setLdLibraryPath(ldPath);
            Log.i(TAG, "android_update_LD_LIBRARY_PATH 已更新");
        }
        String glDir = intent.getStringExtra(EXTRA_GL_DIR);
        if (glDir != null && !glDir.isEmpty()) {
            for (String name : GL_DLOPEN_ORDER) {
                long handle = FCLBridge.dlopen(glDir + "/" + name);
                Log.i(TAG, "dlopen " + name + " handle=0x" + Long.toHexString(handle));
            }
        }
        FCLBridge.installExitTrapAndHooks();
        Log.i(TAG, "FCL 钩子已安装（pojav_environ + SDL/EGL/dlsym 代理）");
    }

    // getLibraries 仅供默认 loadLibraries 使用，已按完整路径加载，返回空
    @Override
    protected String[] getLibraries() {
        return new String[0];
    }

    /**
     * 覆盖 SDL 的方向决策，固定横屏（与 manifest 一致）。
     *
     * SDLActivity.setOrientationBis 在窗口 resizable 且无 SDL_ORIENTATIONS hint
     * （MC 未设置）时申请 SCREEN_ORIENTATION_FULL_USER，允许系统随传感器在横竖屏
     * 间自由切换——曾实测游戏日志在 2200x1380 与 1440x2140 之间反复振荡，故需覆盖。
     *
     * 横屏后本机（面板原生方向为竖屏）显示旋转 90°；为消除随之而来的 buffer 预旋转，
     * 见 applyGameWindowSetup 里对 surface buffer 尺寸的固定
     */
    @Override
    public void setOrientationBis(int w, int h, boolean resizable, String hint) {
        Log.i(TAG, "锁定横屏（忽略 SDL 方向请求 w=" + w + " h=" + h
                + " resizable=" + resizable + " hint=" + hint + "）");
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE);
    }

    @Override
    protected void main() {
        try {
            Log.i(TAG, "SDLThread 等待游戏退出");
            sExitLatch.await();
        } catch (InterruptedException ignored) {
        }
        Log.i(TAG, "SDLThread 释放，活动结束");
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        // 让游戏窗口与显示区等大，并把 surface buffer 固定为横屏尺寸（详见方法注释）
        applyGameWindowSetup();
        // super.onCreate 完成 loadLibraries + SDL.setupJNI（内部 SDL_SetMainReady）。
        // 注意：不要改写 SDL 的自然方向 / 当前旋转——已实测无效（buffer 几何不变）
        sReady = !mBrokenLibraries;
        Log.i(TAG, "onCreate 完成，sReady=" + sReady);
    }

    /**
     * 游戏窗口初始化：边到边全屏 + 隐藏系统栏。
     *
     * 注意：不要在此处调用 SurfaceHolder.setFixedSize，也不要改写 SDL 的自然方向/旋转。
     * 实测 setFixedSize(2200,1440) 会把图层的 contentCrop 声明成横屏，而实际 buffer 是
     * 面板原生竖屏（framework 分配），导致合成器按错误裁剪非等比拉伸（画面被压成竖屏）；
     * 改写 SDL 的 naturalOrientation/rotation 则会让 SDL 按竖屏上报窗口尺寸，MC 布局随之
     * 变成竖屏。二者均使画面更差，故移除，保留 framework + SDL 的默认行为
     */
    private void applyGameWindowSetup() {
        Window win = getWindow();
        win.clearFlags(WindowManager.LayoutParams.FLAG_FORCE_NOT_FULLSCREEN);
        win.addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            win.setDecorFitsSystemWindows(false);
            WindowInsetsController c = win.getInsetsController();
            if (c != null) {
                c.hide(WindowInsets.Type.statusBars() | WindowInsets.Type.navigationBars());
                c.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            }
        } else {
            win.getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                            | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                            | View.SYSTEM_UI_FLAG_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
        }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            applyGameWindowSetup();
        }
    }

    /**
     * 同进程二次启动前由 C# 侧调用：复位 SDLActivity 的会话静态位。
     * SDLActivity.initialize 不覆盖 mActivityCreated / mSDLMainFinished /
     * mBrokenLibraries，不复位则二次 onCreate 走 System.exit(0) 分支杀掉整个进程；
     * native 侧 run_count 检查由 manifest 的 SDL_ENV.SDL_ANDROID_ALLOW_RECREATE_ACTIVITY=1 放行。
     */
    public static void prepareNewSession() {
        mActivityCreated = false;
        mSDLMainFinished = false;
        mBrokenLibraries = true;
        mSDLThread = null;
        SDLActivity.initialize();
        sExitLatch = new CountDownLatch(1);
        sReady = false;
    }

    /** 游戏退出（C# JvmHost.Exited）后由 C# 侧调用：放行 SDLThread，让其 finish 活动 */
    public static void notifyExit() {
        sExitLatch.countDown();
    }
}
