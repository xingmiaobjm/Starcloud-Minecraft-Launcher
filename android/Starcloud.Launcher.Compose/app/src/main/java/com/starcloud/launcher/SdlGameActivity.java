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
 * MC 26.x SDL        LWJGL  3.4   SDL   /     
 *
 *   SDLActivity         
 * 1. libSDL3.so    APK lib            override loadLibraries  
 *    Intent extra         System.load onCreate    loadLibraries   
 *    setupJNI           nativeSetupJNI   native     libSDL3.so    
 * 2.       FCL           intent extras       
 *    setLdLibraryPath DT_NEEDED        DLOPEN    mesa         
 *    native    plain dlopen      escape        setupExitTrap +
 *    initializeHooks pojav_environ    + bytehook      SDL_InitSubSystem
 *    launcher       ES profile       EGL       Vulkan       
 * 3. JVM main   C#        SDLThread   main()    nativeRunMain 
 *                    SDLMain.run    main     finish      
 * 4. onCreate     sReady    C#          SDL_SetMainReady
 *     nativeSetupJNI              JVM        
 */
public class SdlGameActivity extends SDLActivity {
    private static final String TAG = "StarcloudSDL";

    /** C#     libSDL3.so       Intent extra   */
    public static final String EXTRA_SDL_LIB = "sdl_lib_path";

    /** C#            spec.Env[LD_LIBRARY_PATH] +        Intent extra   */
    public static final String EXTRA_LD_PATH = "ld_library_path";

    /** C#            mesa-zink   Intent extra   */
    public static final String EXTRA_GL_DIR = "gl_dir";

    /**
     * DLOPEN            libEGL_mesa    libglapi       
     * libcutils            SDL   SDL_EGL_LIBRARY/SDL_OPENGL_LIBRARY
     *      DT_NEEDED   soname                  
     */
    private static final String[] GL_DLOPEN_ORDER = {
            "libglapi.so",
            "libEGL_mesa.so",
            "libOSMesa_81.so",
            "libzink_dri.so",
            "libvulkan_freedreno.so",
            "libVkLayer_khronos_timeline_semaphore.so",
    };

    /** onCreate      loadLibraries + setupJNI       C#    JNI    */
    public static volatile boolean sReady = false;

    private static CountDownLatch sExitLatch = new CountDownLatch(1);

    /** libstarcloudwindow.so      RTLD_GLOBAL              */
    private static native void promoteSelfGlobal();

    /** libstarcloudwindow.so       libSDL3.so   GOT      buffer    */
    private static native void installHooks();

    @Override
    public void loadLibraries() {
        //                      libnativewindow.so   
        // ANativeWindow_setBuffersGeometry    SDL      buffer     
        //                NativeLibs/src/starcloud_windowhook.c 
        System.loadLibrary("starcloudwindow");
        //     RTLD_GLOBAL                   libSDL3       
        promoteSelfGlobal();
        Log.i(TAG, "libstarcloudwindow.so 已加载并提升全局可见");

        Intent intent = getIntent();
        String lib = intent != null ? intent.getStringExtra(EXTRA_SDL_LIB) : null;
        if (lib == null || lib.isEmpty()) {
            throw new UnsatisfiedLinkError("缺少 Intent extra: " + EXTRA_SDL_LIB);
        }
        Log.i(TAG, "System.load " + lib);
        System.load(lib);
        // libSDL3         GOT                       
        installHooks();

        // ---- FCL                  ----
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

    // getLibraries      loadLibraries                
    @Override
    protected String[] getLibraries() {
        return new String[0];
    }

    /**
     *    SDL              manifest     
     *
     * SDLActivity.setOrientationBis     resizable    SDL_ORIENTATIONS hint
     *  MC         SCREEN_ORIENTATION_FULL_USER             
     *                 2200x1380   1440x2140             
     *
     *                      90           buffer     
     *   applyGameWindowSetup    surface buffer      
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
        //                surface buffer                
        applyGameWindowSetup();
        // super.onCreate    loadLibraries + SDL.setupJNI    SDL_SetMainReady  
        //         SDL       /             buffer      
        sReady = !mBrokenLibraries;
        // 触摸控件层（FCL 风格）：盖在 SDLSurface 之上，全屏接管触摸并注入 LWJGL 输入
        mountTouchControls();
        Log.i(TAG, "onCreate 完成，sReady=" + sReady);
    }

    private void mountTouchControls() {
        try {
            if (!cn.starcloud.launcher.compose.data.LauncherPrefs.INSTANCE.touchControlsEnabled(this)) {
                return;
            }
            final cn.starcloud.launcher.compose.game.TouchControlsView touch =
                    new cn.starcloud.launcher.compose.game.TouchControlsView(this);
            touch.setLayout(cn.starcloud.launcher.compose.data.TouchLayout.INSTANCE.current(this));
            touch.setOrbClickAction(new Runnable() {
                @Override
                public void run() {
                    runOnUiThread(new Runnable() {
                        @Override
                        public void run() {
                            showTouchPanel(touch);
                        }
                    });
                }
            });
            addContentView(touch, new android.view.ViewGroup.LayoutParams(
                    android.view.ViewGroup.LayoutParams.MATCH_PARENT,
                    android.view.ViewGroup.LayoutParams.MATCH_PARENT));
        } catch (Throwable t) {
            Log.w(TAG, "触摸控件层挂载失败：" + t);
        }
    }

    /** 游戏内控制面板：编辑键位 / 保存 / 切换布局 / 关闭控件层 */
    private void showTouchPanel(final cn.starcloud.launcher.compose.game.TouchControlsView touch) {
        String[] items = {
                touch.getEditMode() ? "退出编辑（回到操作）" : "编辑键位（拖动调整）",
                "保存当前布局",
                "切换布局…",
                "关闭控件层（本次游戏）",
        };
        new android.app.AlertDialog.Builder(this)
                .setTitle("触摸控制")
                .setItems(items, new android.content.DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(android.content.DialogInterface d, int which) {
                        switch (which) {
                            case 0:
                                touch.setEditMode(!touch.getEditMode());
                                break;
                            case 1:
                                cn.starcloud.launcher.compose.data.TouchLayout.INSTANCE
                                        .save(SdlGameActivity.this, touch.snapshot());
                                android.widget.Toast.makeText(SdlGameActivity.this,
                                        "已保存「" + touch.snapshot().getName() + "」",
                                        android.widget.Toast.LENGTH_SHORT).show();
                                break;
                            case 2:
                                pickLayout(touch);
                                break;
                            case 3:
                                touch.setVisibility(android.view.View.GONE);
                                break;
                        }
                    }
                })
                .setNegativeButton("关闭面板", null)
                .show();
    }

    private void pickLayout(final cn.starcloud.launcher.compose.game.TouchControlsView touch) {
        final java.util.List<String> names = new java.util.ArrayList<>(
                cn.starcloud.launcher.compose.data.TouchLayout.INSTANCE.list(this));
        if (names.isEmpty()) {
            names.add(touch.snapshot().getName());
        }
        new android.app.AlertDialog.Builder(this)
                .setTitle("选择布局")
                .setItems(names.toArray(new String[0]), new android.content.DialogInterface.OnClickListener() {
                    @Override
                    public void onClick(android.content.DialogInterface d, int which) {
                        String name = names.get(which);
                        cn.starcloud.launcher.compose.data.TouchLayout.INSTANCE.setCurrent(SdlGameActivity.this, name);
                        touch.setLayout(cn.starcloud.launcher.compose.data.TouchLayout.INSTANCE
                                .current(SdlGameActivity.this));
                        android.widget.Toast.makeText(SdlGameActivity.this, "已切换：" + name,
                                android.widget.Toast.LENGTH_SHORT).show();
                    }
                })
                .show();
    }

    /**
     *               +       
     *
     *            SurfaceHolder.setFixedSize       SDL      /   
     *    setFixedSize(2200,1440)       contentCrop           buffer  
     *        framework                              
     *    SDL   naturalOrientation/rotation     SDL           MC     
     *                      framework + SDL      
     */
    private void applyGameWindowSetup() {
        // onCreate 极早期 DecorView 可能未 attach（getWindowInsetsController NPE 实测）：
        // 此处尽力而为，onWindowFocusChanged 保证最终生效
        try {
            applyGameWindowSetupInner();
        } catch (Throwable ignored) {
        }
    }

    private void applyGameWindowSetupInner() {
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
     *           C#        SDLActivity        
     * SDLActivity.initialize     mActivityCreated / mSDLMainFinished /
     * mBrokenLibraries        onCreate   System.exit(0)          
     * native   run_count     manifest   SDL_ENV.SDL_ANDROID_ALLOW_RECREATE_ACTIVITY=1    
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

    /**      C# JvmHost.Exited    C#        SDLThread    finish    */
    public static void notifyExit() {
        sExitLatch.countDown();
    }
}
