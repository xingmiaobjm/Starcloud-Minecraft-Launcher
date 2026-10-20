package org.libsdl.app;


import android.content.Context;
import android.content.pm.ActivityInfo;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Insets;
import android.graphics.Matrix;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.Rect;
import android.hardware.Sensor;
import android.hardware.SensorEvent;
import android.hardware.SensorEventListener;
import android.hardware.SensorManager;
import android.os.Build;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.Display;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.PointerIcon;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.widget.RelativeLayout;

import android.view.ScaleGestureDetector;

/**
    SDLSurface. This is what we draw on, so we need to know when it's created
    in order to do anything useful.

    Because of this, that's where we set up the SDL thread
*/
public class SDLSurface extends SurfaceView implements SurfaceHolder.Callback,
    View.OnApplyWindowInsetsListener, View.OnKeyListener, View.OnTouchListener,
    SensorEventListener, ScaleGestureDetector.OnScaleGestureListener {

    // Sensors
    protected SensorManager mSensorManager;
    protected Display mDisplay;

    // Keep track of the surface size to normalize touch events
    protected float mWidth, mHeight;

    // Is SurfaceView ready for rendering
    protected boolean mIsSurfaceReady;

    // Is on-screen keyboard visible
    protected boolean mKeyboardVisible;

    // Pinch events
    private final ScaleGestureDetector scaleGestureDetector;

    // Startup
    protected SDLSurface(Context context) {
        super(context);
        getHolder().addCallback(this);

        scaleGestureDetector = new ScaleGestureDetector(context, this);

        setFocusable(true);
        setFocusableInTouchMode(true);
        requestFocus();
        setOnApplyWindowInsetsListener(this);
        setOnKeyListener(this);
        setOnTouchListener(this);

        mDisplay = ((WindowManager)context.getSystemService(Context.WINDOW_SERVICE)).getDefaultDisplay();
        mSensorManager = (SensorManager)context.getSystemService(Context.SENSOR_SERVICE);

        setOnGenericMotionListener(SDLActivity.getMotionListener());

        // Some arbitrary defaults to avoid a potential division by zero
        mWidth = 1.0f;
        mHeight = 1.0f;

        mIsSurfaceReady = false;
    }

    protected void handlePause() {
        enableSensor(Sensor.TYPE_ACCELEROMETER, false);
        stopTouchUiPulse();
    }

    protected void handleResume() {
        setFocusable(true);
        setFocusableInTouchMode(true);
        requestFocus();
        setOnApplyWindowInsetsListener(this);
        setOnKeyListener(this);
        setOnTouchListener(this);
        enableSensor(Sensor.TYPE_ACCELEROMETER, true);
        startTouchUiPulse();
    }

    protected Surface getNativeSurface() {
        return getHolder().getSurface();
    }

    // Called when we have a valid drawing surface
    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        Log.v("SDL", "surfaceCreated()");
        SDLActivity.onNativeSurfaceCreated();
    }

    // Called when we lose the surface
    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        Log.v("SDL", "surfaceDestroyed()");

        // Transition to pause, if needed
        SDLActivity.mNextNativeState = SDLActivity.NativeState.PAUSED;
        SDLActivity.handleNativeState();

        stopTouchUiPulse();
        releaseAllInputs();
        mIsSurfaceReady = false;
        SDLActivity.onNativeSurfaceDestroyed();
    }

    // Called when the surface is resized
    @Override
    public void surfaceChanged(SurfaceHolder holder,
                               int format, int width, int height) {
        Log.v("SDL", "surfaceChanged()");

        if (SDLActivity.mSingleton == null) {
            return;
        }

        mWidth = width;
        mHeight = height;
        int nDeviceWidth = width;
        int nDeviceHeight = height;
        float density = 1.0f;
        try
        {
            DisplayMetrics realMetrics = new DisplayMetrics();
            mDisplay.getRealMetrics( realMetrics );
            nDeviceWidth = realMetrics.widthPixels;
            nDeviceHeight = realMetrics.heightPixels;
            // Use densityDpi instead of density to more closely match what the UI scale is
            density = (float)realMetrics.densityDpi / 160.0f;
        } catch(Exception ignored) {
        }

        synchronized(SDLActivity.getContext()) {
            // In case we're waiting on a size change after going fullscreen, send a notification.
            SDLActivity.getContext().notifyAll();
        }

        Log.v("SDL", "Window size: " + width + "x" + height);
        Log.v("SDL", "Device size: " + nDeviceWidth + "x" + nDeviceHeight);
        SDLActivity.nativeSetScreenResolution(width, height, nDeviceWidth, nDeviceHeight, density, mDisplay.getRefreshRate());
        SDLActivity.onNativeResize();

        // Prevent a screen distortion glitch,
        // for instance when the device is in Landscape and a Portrait App is resumed.
        boolean skip = false;
        int requestedOrientation = SDLActivity.mSingleton.getRequestedOrientation();

        if (requestedOrientation == ActivityInfo.SCREEN_ORIENTATION_PORTRAIT || requestedOrientation == ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT) {
            if (mWidth > mHeight) {
               skip = true;
            }
        } else if (requestedOrientation == ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE || requestedOrientation == ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE) {
            if (mWidth < mHeight) {
               skip = true;
            }
        }

        // Special Patch for Square Resolution: Black Berry Passport
        if (skip) {
           double min = Math.min(mWidth, mHeight);
           double max = Math.max(mWidth, mHeight);

           if (max / min < 1.20) {
              Log.v("SDL", "Don't skip on such aspect-ratio. Could be a square resolution.");
              skip = false;
           }
        }

        // Don't skip if we might be multi-window or have popup dialogs
        if (skip) {
            if (Build.VERSION.SDK_INT >= 24 /* Android 7.0 (N) */) {
                skip = false;
            }
        }

        if (skip) {
           Log.v("SDL", "Skip .. Surface is not ready.");
           mIsSurfaceReady = false;
           return;
        }

        /* If the surface has been previously destroyed by onNativeSurfaceDestroyed, recreate it here */
        SDLActivity.onNativeSurfaceChanged();

        /* Surface is ready */
        mIsSurfaceReady = true;

        SDLActivity.mNextNativeState = SDLActivity.NativeState.RESUMED;
        SDLActivity.handleNativeState();

        startTouchUiPulse();
    }

    // Window inset
    @Override
    public WindowInsets onApplyWindowInsets(View v, WindowInsets insets) {
        if (Build.VERSION.SDK_INT >= 30 /* Android 11 (R) */) {
            Insets combined = insets.getInsets(WindowInsets.Type.systemBars() |
                                               WindowInsets.Type.systemGestures() |
                                               WindowInsets.Type.mandatorySystemGestures() |
                                               WindowInsets.Type.tappableElement() |
                                               WindowInsets.Type.displayCutout());

            SDLActivity.onNativeInsetsChanged(combined.left, combined.right, combined.top, combined.bottom);

            if (insets.isVisible(WindowInsets.Type.ime())) {
                if (!mKeyboardVisible) {
                    mKeyboardVisible = true;
                    SDLActivity.onNativeScreenKeyboardShown();
                }
            } else {
                if (mKeyboardVisible) {
                    mKeyboardVisible = false;
                    SDLActivity.onNativeScreenKeyboardHidden();
                }
            }
        }

        // Pass these to any child views in case they need them
        return insets;
    }

    // Key events
    @Override
    public boolean onKey(View v, int keyCode, KeyEvent event) {
        return SDLActivity.handleKeyEvent(v, keyCode, event, null);
    }

    private float getNormalizedX(float x)
    {
        if (mWidth <= 1) {
            return 0.5f;
        } else {
            return (x / (mWidth - 1));
        }
    }

    private float getNormalizedY(float y)
    {
        if (mHeight <= 1) {
            return 0.5f;
        } else {
            return (y / (mHeight - 1));
        }
    }

    // =========================================================================
    // 触控层（Bedrock 风格完整控制器）
    //
    // 模式判定（最可靠信号）：
    //   MC 进入世界 → InputConstants.grabMouse → SDL_SetWindowRelativeMouseMode(true)
    //   → SDL 的 Android 后端 Android_SetRelativeMouseMode
    //   → JNI 回调 SDLActivity.setRelativeMouseEnabled(true)；菜单/暂停时
    //   releaseMouse 走同一通路翻回 false。见 SDLActivity.isRelativeMouseActive()。
    //   该信号 = 「游戏内且鼠标被捕获」，可直接用于切换两种布局。
    //
    // 游戏内：
    //   · 左下区域拖动 = 移动摇杆（八方向合成 W/A/S/D；推程 → 点按频率 + 按住占比，
    //     模拟线性速度：轻推=慢走，推到底=持续按住走速）
    //   · 其余区域拖动 = 转视角（相对鼠标增量，relative=true → SDL 产生 xrel/yrel
    //     → MC 的 MouseHandler 在 mouseGrabbed 分支累加并旋转视角）
    //   · 右下按钮 = 跳/潜行(切换)/挖掘(按住左键)/使用(按住右键)
    //   · 右上按钮 = 背包；左上角 = 退(ESC)，两种模式常驻、绘制在最上层
    //     （对齐网易手机版把菜单键放左上角的习惯），其右为「换」手动模式切换（兜底）
    // 菜单内：
    //   · 绝对映射虚拟指针（视图像素 × gain = MC 帧缓冲像素域），按下即点击
    //   · 自绘箭头指示位置（MC 菜单不渲染系统指针）
    //   · 左上角「退」仍可用来关闭当前界面
    // =========================================================================

    /** 手动强制「游戏内」布局（左上 ⚙ 切换），用于自动判定失效时兜底 */
    private boolean uiForceGame = false;

    // 指针角色（按 pointerId 分配）
    private int ptrMove = -1, ptrLook = -1, ptrBtn = -1, ptrMenu = -1;
    private int uiActiveBtn = -1;
    private float lookLastX, lookLastY;
    private float joyCx, joyCy, joyKx, joyKy;      // 摇杆底座/旋钮（视图像素）
    private boolean[] moveKeys = new boolean[4];   // W A S D
    private boolean sneakOn = false;
    private float cursorVx = -1f, cursorVy = -1f;  // 菜单指针位置（视图像素）
    private boolean cursorDown = false;

    /** 摇杆生效区域：屏幕左侧 JOY_ZONE_X 宽度、上方留 JOY_ZONE_TOP 高度给其它操作 */
    private static final float JOY_ZONE_X = 0.42f;
    private static final float JOY_ZONE_TOP = 0.20f;
    private static final float JOY_BASE_FX = 0.15f, JOY_BASE_FY = 0.72f;
    private static final float JOY_RADIUS_F = 0.13f;   // 相对 min(w,h)

    /**
     * 按钮：{cx(占宽), cy(占高), half(半边长，占高)}；圆角方形，图标见 buildIconPath。
     * 下标与 BTN_* 一一对应。
     */
    private static final float[][] UI_BTN = {
            {0.905f, 0.815f, 0.072f}, // 跳：上箭头
            {0.905f, 0.610f, 0.060f}, // 潜：下箭头（开关）
            {0.795f, 0.895f, 0.066f}, // 挖：镐（按住 = 左键）
            {0.795f, 0.700f, 0.066f}, // 用：方块（按住 = 右键）
            {0.860f, 0.115f, 0.052f}, // 包：箱子
            {0.050f, 0.100f, 0.055f}, // 退 = ESC：左上角，两种模式常驻、绘制在最上层
            {0.155f, 0.100f, 0.046f}, // 换：手动模式切换（兜底，仅游戏内显示）
    };

    private static final int BTN_JUMP = 0, BTN_SNEAK = 1, BTN_ATTACK = 2,
            BTN_USE = 3, BTN_INV = 4, BTN_ESC = 5, BTN_MODE = 6;

    // Android 键码（SDLActivity.onNativeKeyDown/Up 用）
    private static final int AK_W = 51, AK_A = 29, AK_S = 47, AK_D = 32,
            AK_SPACE = 62, AK_SHIFT = 59, AK_E = 33, AK_ESC = 111;

    // MotionEvent 鼠标键位状态（SDL 的 Android_OnMouse 按此解释 button 参数）
    private static final int MB_LEFT = 1, MB_RIGHT = 2;

    /** 读系统属性；未设置（或为空串，setprop key '' 的结果）返回 null */
    private static String sysProp(String key)
    {
        try {
            Class<?> sp = Class.forName("android.os.SystemProperties");
            String v = (String) sp.getMethod("get", String.class, String.class)
                    .invoke(null, key, null);
            return (v == null || v.isEmpty()) ? null : v;
        } catch (Exception e) {
            return null;
        }
    }

    /**
     * 绝对注入增益：视图(物理)像素 × gain = MC 期望的坐标域。
     * MC 的 MouseHandler 用自身 framebuffer 尺寸做换算（getScaledXPos =
     * xpos × guiWidth / fbWidth），而本机 fb(854x480) 与窗口(2200x1440) 宽高比不同，
     * 故 gain = fb / 窗口（0.3882 / 0.3333），实测点击/悬停精确命中。
     */
    private static float touchGainX()
    {
        String v = sysProp("debug.starcloud.touchgain_x");
        return (v != null) ? Float.parseFloat(v) : 0.3881818f;   // 854 / 2200
    }

    private static float touchGainY()
    {
        String v = sysProp("debug.starcloud.touchgain_y");
        return (v != null) ? Float.parseFloat(v) : 0.3333333f;   // 480 / 1440
    }

    /** 视角灵敏度：手指像素 → 注入的相对鼠标像素 */
    private static float lookSens()
    {
        String v = sysProp("debug.starcloud.look_sens");
        return (v != null) ? Float.parseFloat(v) : 1.0f;
    }

    /** 当前是否「游戏内」布局（MC 已抓取鼠标，或手动强制） */
    private boolean gameLayout()
    {
        return uiForceGame || SDLActivity.isRelativeMouseActive();
    }

    // ---- 输入注入 ----

    /**
     * 按键状态位图：SDL 的 Android_OnMouse 用「本次状态 & ~上次状态」求按下键、
     * 「上次状态 & ~本次状态」求抬起键（见 SDL_androidmouse.c Android_OnMouse /
     * TranslateButton）。故 UP 必须传「抬起后的状态」（通常 0），传键号本身会让
     * last_state 永远残留该位，导致第二次及以后的点击全部失效。
     */
    private int menuBtnState = 0;
    private int gameBtnState = 0;

    /**
     * 游戏内鼠标键：relative=true 且位移 (0,0)。
     * SDL 的 Android_OnMouse 对 DOWN/UP/MOVE 都会先发一次 motion，若在相对模式下
     * 传绝对坐标会产生巨大 xrel 使视角跳变，故游戏内一律用零位移相对 motion。
     */
    private void sendGameMouse(int button, int action)
    {
        if (action == MotionEvent.ACTION_DOWN) {
            gameBtnState |= button;
        } else {
            gameBtnState &= ~button;
        }
        SDLActivity.onNativeMouse(gameBtnState, action, 0f, 0f, true);
    }

    /** 菜单指针绝对移动（先发 MOVE 使 MC 光标到位，DOWN/UP 才点得中） */
    private void sendMenuMove(float vx, float vy)
    {
        SDLActivity.onNativeMouse(0, MotionEvent.ACTION_MOVE,
                vx * touchGainX(), vy * touchGainY(), false);
    }

    /** 菜单点击：先同步位置，再按「位图差集」语义发按键 */
    private void sendMenuButton(int action, float vx, float vy)
    {
        sendMenuMove(vx, vy);
        if (action == MotionEvent.ACTION_DOWN) {
            menuBtnState |= MB_LEFT;
        } else {
            menuBtnState &= ~MB_LEFT;
        }
        SDLActivity.onNativeMouse(menuBtnState, action,
                vx * touchGainX(), vy * touchGainY(), false);
    }

    // ---- 移动摇杆 ----
    // 推程 = 模拟量：偏移越大 → 点按频率越高、单次按住占比越大；推到底转为持续按住。
    // 这样"推一半 ≈ 半速"才成立（MC 里按住才加速到走速，纯改频率不改占空比平均速度不变）。
    private static final float JOY_DEADZONE = 0.18f;       // 半径归一化后的死区
    private static final long JOY_PERIOD_MAX_MS = 450;     // 刚出死区：最慢点按
    private static final long JOY_PERIOD_MIN_MS = 70;      // 接近满推程
    private static final float JOY_HOLD_T = 0.90f;         // t 超过此值 = 直接按住，不再脉冲
    private static final float JOY_DIR_THRESHOLD = 0.38f;  // 单位方向分量阈值（八方向）

    private final boolean[] joyDirs = new boolean[4];      // 摇杆方向（期望）
    private long joyPeriodMs = JOY_PERIOD_MAX_MS;
    private long joyDownMs = 150;
    private boolean joyContinuous = false;                 // 满推程：常按
    private boolean joyPhaseDown = false;                  // 当前脉冲相位是否为"按下"

    private float joyRadius()
    {
        return Math.min(getWidth(), getHeight()) * JOY_RADIUS_F;
    }

    private void joyStart(float x, float y)
    {
        joyCx = getWidth() * JOY_BASE_FX;
        joyCy = getHeight() * JOY_BASE_FY;
        joyUpdate(x, y);
    }

    private void joyUpdate(float x, float y)
    {
        float r = joyRadius();
        if (r <= 1f) {
            return;
        }
        float dx = x - joyCx;
        float dy = y - joyCy;
        float len = (float) Math.hypot(dx, dy);
        if (len > r) {
            dx = dx * r / len;
            dy = dy * r / len;
        }
        joyKx = joyCx + dx;
        joyKy = joyCy + dy;

        float nx = dx / r, ny = dy / r;                    // 半径归一化分量
        float mag = Math.min(1f, (float) Math.hypot(nx, ny));
        boolean wasActive = joyAnyDir();

        if (mag < JOY_DEADZONE) {
            joyStopPulse();
            joyContinuous = false;
            setJoyDirs(false, false, false, false);
            joyPhaseDown = false;
            applyJoyOutput();
            return;
        }

        // 方向用单位向量判定：推程只影响速度，不改变方向档位
        float ux = nx / mag, uy = ny / mag;
        boolean w = uy < -JOY_DIR_THRESHOLD;
        boolean a = ux < -JOY_DIR_THRESHOLD;
        boolean s = uy > JOY_DIR_THRESHOLD;
        boolean d = ux > JOY_DIR_THRESHOLD;
        boolean dirsChanged = (joyDirs[0] != w) || (joyDirs[1] != a)
                || (joyDirs[2] != s) || (joyDirs[3] != d);
        setJoyDirs(w, a, s, d);

        float t = (mag - JOY_DEADZONE) / (1f - JOY_DEADZONE);   // 0..1
        // 频率曲线用 t^0.2：让"推一半"（t≈0.39）就已经是 ~7Hz 的高频点按
        joyPeriodMs = (long) (JOY_PERIOD_MAX_MS
                - (JOY_PERIOD_MAX_MS - JOY_PERIOD_MIN_MS) * Math.pow(t, 0.2));
        joyDownMs = Math.max(30, (long) (joyPeriodMs * (0.35f + 0.65f * t)));

        boolean cont = t >= JOY_HOLD_T;
        // 变向要立刻生效：重启脉冲即先按一下，否则要等本周期翻相位（最多 450ms）
        boolean needRestart = !wasActive || dirsChanged || cont != joyContinuous;
        joyContinuous = cont;
        if (cont) {
            // 满推程：直接按住，避免高频抬起造成的顿挫
            joyStopPulse();
            joyPhaseDown = true;
            applyJoyOutput();
        } else if (needRestart) {
            joyStartPulse();
        } else {
            applyJoyOutput();                               // 周期就地生效，本周期走完
        }
    }

    /** 按"方向期望 × 当前相位"投递 WASD，相位与方向分离，脉冲期间改方向也能即时生效 */
    private void applyJoyOutput()
    {
        for (int i = 0; i < 4; i++) {
            setMoveKey(i, joyDirs[i] && joyPhaseDown);
        }
    }

    private boolean joyAnyDir()
    {
        return joyDirs[0] || joyDirs[1] || joyDirs[2] || joyDirs[3];
    }

    private void setJoyDirs(boolean w, boolean a, boolean s, boolean d)
    {
        joyDirs[0] = w;
        joyDirs[1] = a;
        joyDirs[2] = s;
        joyDirs[3] = d;
    }

    private void joyStopPulse()
    {
        removeCallbacks(joyPulseTask);
    }

    private void joyStartPulse()
    {
        joyStopPulse();
        joyPhaseDown = true;
        applyJoyOutput();
        postDelayed(joyPulseTask, Math.max(20, joyDownMs));
    }

    /** 只在相位翻转点唤醒（一个周期 2 次），不做高频轮询 */
    private final Runnable joyPulseTask = new Runnable() {
        @Override
        public void run() {
            if (ptrMove == -1 || joyContinuous || !joyAnyDir()) {
                return;
            }
            if (joyPhaseDown) {
                joyPhaseDown = false;
                applyJoyOutput();
                postDelayed(this, Math.max(20, joyPeriodMs - joyDownMs));
            } else {
                joyPhaseDown = true;
                applyJoyOutput();
                postDelayed(this, Math.max(20, joyDownMs));
            }
        }
    };

    private void setMoveKey(int i, boolean on)
    {
        if (moveKeys[i] == on) {
            return;
        }
        moveKeys[i] = on;
        int ak = (i == 0) ? AK_W : (i == 1) ? AK_A : (i == 2) ? AK_S : AK_D;
        if (on) {
            SDLActivity.onNativeKeyDown(ak);
        } else {
            SDLActivity.onNativeKeyUp(ak);
        }
    }

    private void joyEnd()
    {
        joyStopPulse();
        joyContinuous = false;
        joyPhaseDown = false;
        setJoyDirs(false, false, false, false);
        applyJoyOutput();
    }

    // ---- 动作按钮 ----

    /** 单个按钮命中（方形判定，边长放宽 1.15 倍，手指不好点太小的方块） */
    private boolean hitButtonAt(int i, float x, float y)
    {
        float cx = UI_BTN[i][0] * getWidth();
        float cy = UI_BTN[i][1] * getHeight();
        float half = UI_BTN[i][2] * getHeight() * 1.15f;
        return x >= cx - half && x <= cx + half && y >= cy - half && y <= cy + half;
    }

    /** 游戏内按钮命中：全部按钮 */
    private int hitButton(float x, float y)
    {
        for (int i = 0; i < UI_BTN.length; i++) {
            if (hitButtonAt(i, x, y)) {
                return i;
            }
        }
        return -1;
    }

    /** 常驻按钮（退=ESC）命中：菜单/游戏两种模式下都优先响应 */
    private int hitAlwaysButton(float x, float y)
    {
        return hitButtonAt(BTN_ESC, x, y) ? BTN_ESC : -1;
    }

    private void onButtonDown(int i)
    {
        switch (i) {
            case BTN_JUMP:
                SDLActivity.onNativeKeyDown(AK_SPACE);
                break;
            case BTN_SNEAK:
                sneakOn = !sneakOn;
                if (sneakOn) {
                    SDLActivity.onNativeKeyDown(AK_SHIFT);
                } else {
                    SDLActivity.onNativeKeyUp(AK_SHIFT);
                }
                break;
            case BTN_ATTACK:
                sendGameMouse(MB_LEFT, MotionEvent.ACTION_DOWN);
                break;
            case BTN_USE:
                sendGameMouse(MB_RIGHT, MotionEvent.ACTION_DOWN);
                break;
            case BTN_INV:
                SDLActivity.onNativeKeyDown(AK_E);
                SDLActivity.onNativeKeyUp(AK_E);
                break;
            case BTN_ESC:
                SDLActivity.onNativeKeyDown(AK_ESC);
                SDLActivity.onNativeKeyUp(AK_ESC);
                break;
            case BTN_MODE:
                uiForceGame = !uiForceGame;
                Log.i("SDL", "触控层：手动模式切换 uiForceGame=" + uiForceGame);
                break;
            default:
                break;
        }
    }

    private void onButtonUp(int i)
    {
        switch (i) {
            case BTN_JUMP:
                SDLActivity.onNativeKeyUp(AK_SPACE);
                break;
            case BTN_ATTACK:
                sendGameMouse(MB_LEFT, MotionEvent.ACTION_UP);
                break;
            case BTN_USE:
                sendGameMouse(MB_RIGHT, MotionEvent.ACTION_UP);
                break;
            default:
                break;
        }
    }

    /** 释放所有由触控层按下的输入（模式切换/被打断时兜底，避免按键卡住） */
    private void releaseAllInputs()
    {
        joyEnd();
        if (sneakOn) {
            SDLActivity.onNativeKeyUp(AK_SHIFT);
            sneakOn = false;
        }
        if (ptrBtn != -1 && uiActiveBtn >= 0) {
            onButtonUp(uiActiveBtn);
        }
        if (ptrMenu != -1 && cursorDown) {
            SDLActivity.onNativeMouse(MB_LEFT, MotionEvent.ACTION_UP, 0f, 0f, false);
        }
        ptrMove = ptrLook = ptrBtn = ptrMenu = -1;
        uiActiveBtn = -1;
        cursorDown = false;
        menuBtnState = 0;
        gameBtnState = 0;
    }

    /**
     * 整段 MotionEvent 的分派（每事件调用一次）：按 pointerId 维持角色，
     * 一个手指一个角色，支持「摇杆 + 转视角」同时操作。
     */
    private void handleTouchController(MotionEvent e)
    {
        int action = e.getActionMasked();
        switch (action) {
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_POINTER_DOWN: {
                int idx = e.getActionIndex();
                int id = e.getPointerId(idx);
                float x = e.getX(idx), y = e.getY(idx);
                // 常驻按钮（退=ESC）先判定：两种模式下都生效
                int bi = hitAlwaysButton(x, y);
                if (bi < 0 && gameLayout()) {
                    bi = hitButton(x, y);
                }
                if (bi >= 0) {
                    ptrBtn = id;
                    uiActiveBtn = bi;
                    onButtonDown(bi);
                } else if (gameLayout()) {
                    if (x < getWidth() * JOY_ZONE_X && y > getHeight() * JOY_ZONE_TOP) {
                        ptrMove = id;
                        joyStart(x, y);
                    } else {
                        ptrLook = id;
                        lookLastX = x;
                        lookLastY = y;
                    }
                } else if (ptrMenu == -1) {
                    ptrMenu = id;
                    cursorVx = x;
                    cursorVy = y;
                    cursorDown = true;
                    sendMenuButton(MotionEvent.ACTION_DOWN, x, y);
                }
                break;
            }
            case MotionEvent.ACTION_MOVE: {
                for (int i = 0; i < e.getPointerCount(); i++) {
                    int id = e.getPointerId(i);
                    float x = e.getX(i), y = e.getY(i);
                    if (id == ptrMenu) {
                        cursorVx = x;
                        cursorVy = y;
                        sendMenuMove(x, y);
                    } else if (id == ptrMove) {
                        joyUpdate(x, y);
                    } else if (id == ptrLook) {
                        float dx = (x - lookLastX) * lookSens();
                        float dy = (y - lookLastY) * lookSens();
                        lookLastX = x;
                        lookLastY = y;
                        if (dx != 0f || dy != 0f) {
                            // 相对鼠标增量：SDL 产生 xrel/yrel，MC 抓取态累加后转视角
                            SDLActivity.onNativeMouse(0, MotionEvent.ACTION_MOVE, dx, dy, true);
                        }
                    }
                }
                break;
            }
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_POINTER_UP: {
                int idx = e.getActionIndex();
                int id = e.getPointerId(idx);
                if (id == ptrMenu) {
                    if (cursorDown) {
                        sendMenuButton(MotionEvent.ACTION_UP, cursorVx, cursorVy);
                    }
                    cursorDown = false;
                    ptrMenu = -1;
                } else if (id == ptrBtn) {
                    onButtonUp(uiActiveBtn);
                    uiActiveBtn = -1;
                    ptrBtn = -1;
                } else if (id == ptrMove) {
                    joyEnd();
                    ptrMove = -1;
                } else if (id == ptrLook) {
                    ptrLook = -1;
                }
                break;
            }
            case MotionEvent.ACTION_CANCEL: {
                releaseAllInputs();
                break;
            }
            default:
                break;
        }
        refreshTouchUi();
    }

    // ===== 触控层叠加绘制（非交互 View，与输入同层但不吃触摸） =====
    // 视觉：扁平像素风（参考网易手机版）——直角方形 + 像素切角，单一平面半透明填充
    // （能透出游戏画面），四边同色的细描边。不做渐变、高光、暗边，不模拟立体厚度。
    // 按下态整体提亮反色（浅底 + 深图标）。
    // 图标：矢量路径 → ICON_N×ICON_N 遮罩 → alpha 二值化 → 最近邻放大，得到硬边像素块；
    // 每个美术像素占固定整数个屏幕像素，故像素大小均匀、不糊。
    private View uiView;
    private final Paint uiPaint = new Paint(Paint.ANTI_ALIAS_FLAG);   // 填充
    private final Paint uiStroke = new Paint(Paint.ANTI_ALIAS_FLAG);  // 描边/高光
    private final Paint uiIcon = new Paint();                         // 图标位图（关滤波 = 像素块）
    private final Path uiPath = new Path();
    private final Rect iconSrc = new Rect(), iconDst = new Rect();
    private static Bitmap[] sIconBitmaps;                             // 二值化 sprite 缓存
    private static final int ICON_N = 18;                             // 图标美术像素分辨率（越小像素感越重）

    private void ensureTouchUi()
    {
        if (uiView != null || !(getParent() instanceof ViewGroup)) {
            return;
        }
        ViewGroup parent = (ViewGroup) getParent();
        uiPaint.setStyle(Paint.Style.FILL);
        uiStroke.setStyle(Paint.Style.STROKE);
        uiStroke.setStrokeCap(Paint.Cap.ROUND);
        uiIcon.setFilterBitmap(false);      // 最近邻放大 → 硬边像素块，不做插值
        uiIcon.setAntiAlias(false);
        uiIcon.setDither(false);
        View v = new View(getContext()) {
            @Override
            protected void onDraw(Canvas canvas) {
                drawTouchUi(canvas);
            }
        };
        v.setClickable(false);
        v.setFocusable(false);
        parent.addView(v, new RelativeLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        v.setElevation(900f);
        uiView = v;
    }

    private void refreshTouchUi()
    {
        ensureTouchUi();
        if (uiView != null) {
            uiView.invalidate();
        }
    }

    private void drawTouchUi(Canvas c)
    {
        if (uiView == null) {
            return;
        }
        float w = uiView.getWidth(), h = uiView.getHeight();
        if (w <= 0 || h <= 0) {
            return;
        }
        boolean game = gameLayout();
        if (game) {
            drawJoystick(c, w, h);
            for (int i = 0; i < UI_BTN.length; i++) {
                if (i != BTN_ESC) {
                    drawButton(c, i, w, h);
                }
            }
        }
        if (!game && cursorDown && cursorVx >= 0f) {
            drawCursor(c, cursorVx, cursorVy);
        }
        // 退 = ESC 最后绘制：始终位于最上层，且菜单/游戏两种模式下都常驻
        drawButton(c, BTN_ESC, w, h);
    }

    /** 摇杆：平面半透明底盘 + 浅色旋钮（圆是操作杆的正确形状，不做切角） */
    private void drawJoystick(Canvas c, float w, float h)
    {
        if (ptrMove == -1) {
            return;
        }
        float r = Math.min(w, h) * JOY_RADIUS_F;
        uiPaint.setColor(0x400C0F14);
        c.drawCircle(joyCx, joyCy, r, uiPaint);
        uiStroke.setStrokeWidth(Math.max(2f, r * 0.032f));
        uiStroke.setColor(0x8CFFFFFF);
        c.drawCircle(joyCx, joyCy, r, uiStroke);

        float kr = r * 0.42f;
        uiPaint.setColor(0xD0EDF1F6);
        c.drawCircle(joyKx, joyKy, kr, uiPaint);
        uiStroke.setStrokeWidth(Math.max(2f, r * 0.034f));
        uiStroke.setColor(0xB4000000);
        c.drawCircle(joyKx, joyKy, kr, uiStroke);
    }

    /**
     * 扁平像素按钮：单一平面半透明填充 + 像素切角 + 四边同色的细描边。
     * 不做渐变/上高光/下暗边，不模拟厚度；按下态整体提亮反色。
     * 图标为 ICON_N×ICON_N 二值化 sprite 用最近邻放大（每个美术像素 = 固定整数个屏幕像素）。
     */
    private void drawButton(Canvas c, int i, float w, float h)
    {
        float cx = UI_BTN[i][0] * w;
        float cy = UI_BTN[i][1] * h;
        float half = UI_BTN[i][2] * h;
        boolean active = (i == uiActiveBtn) || (i == BTN_SNEAK && sneakOn);

        int l = Math.round(cx - half), t = Math.round(cy - half);
        int r = Math.round(cx + half), b = Math.round(cy + half);
        float c2 = Math.max(2f, half * 0.11f);        // 像素切角大小

        // 扁平底：一块平面半透明填充，能透出游戏画面
        uiPath.reset();
        chamferPath(uiPath, l, t, r, b, c2);
        uiPaint.setColor(active ? 0xDCE8ECF3 : 0x7C0C0F14);
        c.drawPath(uiPath, uiPaint);

        // 统一细描边：四边同色，只负责在明暗背景上勾出轮廓
        uiStroke.setStrokeWidth(Math.max(2f, half * 0.045f));
        uiStroke.setColor(active ? 0x96FFFFFF : 0x3EFFFFFF);
        c.drawPath(uiPath, uiStroke);

        // 图标：二值化 sprite + 最近邻放大（px = 每个美术像素占的屏幕像素数，取整保证均匀）
        int px = Math.max(3, Math.round(half * 1.10f / ICON_N));
        int size = px * ICON_N;
        int ix = Math.round(cx - size / 2f), iy = Math.round(cy - size / 2f);
        iconSrc.set(0, 0, ICON_N, ICON_N);
        iconDst.set(ix, iy, ix + size, iy + size);
        c.drawBitmap(iconBitmaps()[i], iconSrc, iconDst, uiIcon);
    }

    /** 像素切角方形（四角各切掉 c×c），得到像素 UI 的典型轮廓 */
    private static void chamferPath(Path p, float l, float t, float r, float b, float c)
    {
        p.moveTo(l + c, t);
        p.lineTo(r - c, t);
        p.lineTo(r, t + c);
        p.lineTo(r, b - c);
        p.lineTo(r - c, b);
        p.lineTo(l + c, b);
        p.lineTo(l, b - c);
        p.lineTo(l, t + c);
        p.close();
    }

    private static Bitmap[] iconBitmaps()
    {
        if (sIconBitmaps == null) {
            sIconBitmaps = new Bitmap[UI_BTN.length];
            for (int k = 0; k < sIconBitmaps.length; k++) {
                sIconBitmaps[k] = buildIconBitmap(k);
            }
        }
        return sIconBitmaps;
    }

    /** 图标 sprite：深色粗描边 → 实心区填白（可选）→ 亮色细线，每层都二值化 → 硬边像素块 */
    private static Bitmap buildIconBitmap(int i)
    {
        Bitmap dark = iconMask(i, Math.max(2f, ICON_N * 0.150f), 0xFF000000, false);
        Bitmap light = iconMask(i, Math.max(1.2f, ICON_N * 0.090f), 0xFFFFFFFF, false);
        Bitmap out = Bitmap.createBitmap(ICON_N, ICON_N, Bitmap.Config.ARGB_8888);
        Canvas c = new Canvas(out);
        c.drawBitmap(dark, 0f, 0f, null);
        if (buildIconFillPath(i) != null) {
            Bitmap f = iconMask(i, 0f, 0xFFFFFFFF, true);
            c.drawBitmap(f, 0f, 0f, null);
            f.recycle();
        }
        c.drawBitmap(light, 0f, 0f, null);
        dark.recycle();
        light.recycle();
        return out;
    }

    /**
     * 把矢量图标画进 ICON_N×ICON_N 位图后做 alpha 二值化（>=128 保留），去掉软边；
     * fill=true 时画填充区，否则按 stroke 描边。
     */
    private static Bitmap iconMask(int i, float stroke, int color, boolean fill)
    {
        Bitmap bmp = Bitmap.createBitmap(ICON_N, ICON_N, Bitmap.Config.ARGB_8888);
        Canvas c = new Canvas(bmp);
        Paint p = new Paint(Paint.ANTI_ALIAS_FLAG);
        p.setColor(color);

        float pad = ICON_N * 0.04f;
        float sc = ICON_N - 2f * pad;
        Matrix m = new Matrix();
        m.setScale(sc, sc);
        m.postTranslate(pad, pad);
        Path dst = new Path();
        (fill ? buildIconFillPath(i) : buildIconPath(i)).transform(m, dst);

        if (fill) {
            p.setStyle(Paint.Style.FILL);
        } else {
            p.setStyle(Paint.Style.STROKE);
            p.setStrokeCap(Paint.Cap.SQUARE);
            p.setStrokeJoin(Paint.Join.MITER);
            p.setStrokeWidth(stroke);
        }
        c.drawPath(dst, p);

        int n = ICON_N;
        int[] px = new int[n * n];
        bmp.getPixels(px, 0, n, 0, 0, n, n);
        for (int k = 0; k < px.length; k++) {
            px[k] = ((px[k] >>> 24) >= 128) ? (px[k] | 0xFF000000) : 0;
        }
        bmp.setPixels(px, 0, n, 0, 0, n, n);
        return bmp;
    }

    /** 按钮图标：单位方框 (0..1)，线宽由按钮尺寸决定，故与分辨率无关 */
    private static Path buildIconPath(int i)
    {
        Path p = new Path();
        switch (i) {
            case BTN_JUMP:   // 上箭头
                p.moveTo(0.50f, 0.86f); p.lineTo(0.50f, 0.16f);
                p.moveTo(0.24f, 0.42f); p.lineTo(0.50f, 0.16f); p.lineTo(0.76f, 0.42f);
                break;
            case BTN_SNEAK:  // 下箭头
                p.moveTo(0.50f, 0.14f); p.lineTo(0.50f, 0.84f);
                p.moveTo(0.24f, 0.58f); p.lineTo(0.50f, 0.84f); p.lineTo(0.76f, 0.58f);
                break;
            case BTN_ATTACK: // 镐
                p.moveTo(0.38f, 0.30f);
                p.quadTo(0.66f, 0.08f, 0.90f, 0.40f);
                p.quadTo(0.62f, 0.30f, 0.44f, 0.46f);
                p.close();
                p.moveTo(0.22f, 0.86f); p.lineTo(0.70f, 0.36f);
                break;
            case BTN_USE:    // 方块：外框 + 中心实心（低分辨率下比等轴立方体可读）
                p.addRoundRect(0.14f, 0.14f, 0.86f, 0.86f, 0.10f, 0.10f, Path.Direction.CW);
                break;
            case BTN_INV:    // 箱子
                p.addRoundRect(0.14f, 0.36f, 0.86f, 0.86f, 0.16f, 0.16f, Path.Direction.CW);
                p.moveTo(0.14f, 0.55f); p.lineTo(0.86f, 0.55f);
                p.moveTo(0.50f, 0.47f); p.lineTo(0.50f, 0.64f);
                break;
            case BTN_ESC:    // 菜单横线
                p.moveTo(0.24f, 0.30f); p.lineTo(0.76f, 0.30f);
                p.moveTo(0.24f, 0.50f); p.lineTo(0.76f, 0.50f);
                p.moveTo(0.24f, 0.70f); p.lineTo(0.76f, 0.70f);
                break;
            case BTN_MODE:   // 双向箭头（切换）
                p.moveTo(0.22f, 0.36f); p.lineTo(0.78f, 0.36f);
                p.moveTo(0.64f, 0.22f); p.lineTo(0.78f, 0.36f); p.lineTo(0.64f, 0.50f);
                p.moveTo(0.78f, 0.64f); p.lineTo(0.22f, 0.64f);
                p.moveTo(0.36f, 0.50f); p.lineTo(0.22f, 0.64f); p.lineTo(0.36f, 0.78f);
                break;
            default:
                break;
        }
        return p;
    }

    /** 需要额外填白的区域（仅"方块"图标的中心）；其余图标返回 null */
    private static Path buildIconFillPath(int i)
    {
        if (i != BTN_USE) {
            return null;
        }
        Path p = new Path();
        p.addRoundRect(0.34f, 0.34f, 0.66f, 0.66f, 0.06f, 0.06f, Path.Direction.CW);
        return p;
    }

    /** 菜单模式的自绘箭头指针（MC 不渲染系统指针），tip 对准触点 */
    private void drawCursor(Canvas c, float x, float y)
    {
        float s = 48f / 24f;
        uiPath.reset();
        uiPath.moveTo(x + 1f * s, y + 1f * s);
        uiPath.lineTo(x + 1f * s, y + 17f * s);
        uiPath.lineTo(x + 5.5f * s, y + 13.5f * s);
        uiPath.lineTo(x + 8f * s, y + 19.5f * s);
        uiPath.lineTo(x + 10.8f * s, y + 18.3f * s);
        uiPath.lineTo(x + 8.3f * s, y + 12.4f * s);
        uiPath.lineTo(x + 13.5f * s, y + 12.4f * s);
        uiPath.close();
        uiPaint.setColor(0xFFFFFFFF);
        c.drawPath(uiPath, uiPaint);
        uiStroke.setStrokeWidth(4f);
        uiStroke.setColor(0xCC000000);
        c.drawPath(uiPath, uiStroke);
    }

    // ===== 模式轮询：MC 抓取/释放鼠标发生在游戏线程，UI 需要主动同步 =====
    private boolean uiPulseRunning = false;
    private boolean uiLastGame = false;
    private final Runnable uiPulse = new Runnable() {
        @Override
        public void run() {
            if (!mIsSurfaceReady) {
                uiPulseRunning = false;
                return;
            }
            boolean game = gameLayout();
            if (game != uiLastGame) {
                uiLastGame = game;
                Log.i("SDL", "触控层：模式切换 " + (game ? "游戏内" : "菜单")
                        + "（auto=" + SDLActivity.isRelativeMouseActive()
                        + " force=" + uiForceGame + "）");
                releaseAllInputs();
                if (!game) {
                    cursorVx = cursorVy = -1f;
                }
            }
            refreshTouchUi();
            postDelayed(this, 250);
        }
    };

    private void startTouchUiPulse()
    {
        if (uiPulseRunning) {
            return;
        }
        uiPulseRunning = true;
        postDelayed(uiPulse, 250);
    }

    private void stopTouchUiPulse()
    {
        uiPulseRunning = false;
        removeCallbacks(uiPulse);
    }

    // Touch events
    @Override
    public boolean onTouch(View v, MotionEvent event) {
        /* 手指触控：交给触控层控制器（整个 MotionEvent 只处理一次） */
        int toolType0 = event.getToolType(0);
        if (toolType0 == MotionEvent.TOOL_TYPE_FINGER || toolType0 == MotionEvent.TOOL_TYPE_UNKNOWN) {
            handleTouchController(event);
            if (!gameLayout()) {
                scaleGestureDetector.onTouchEvent(event);   // 菜单内保留双指缩放
            }
            return true;
        }

        /* Ref: http://developer.android.com/training/gestures/multi.html */
        int touchDevId = event.getDeviceId();
        final int pointerCount = event.getPointerCount();
        int action = event.getActionMasked();
        int pointerId;
        int i = 0;
        float x,y,p;

        if (action == MotionEvent.ACTION_POINTER_UP || action == MotionEvent.ACTION_POINTER_DOWN)
            i = event.getActionIndex();

        do {
            int toolType = event.getToolType(i);

            if (toolType == MotionEvent.TOOL_TYPE_MOUSE) {
                int buttonState = event.getButtonState();
                boolean relative = false;

                // We need to check if we're in relative mouse mode and get the axis offset rather than the x/y values
                // if we are. We'll leverage our existing mouse motion listener
                SDLGenericMotionListener_API14 motionListener = SDLActivity.getMotionListener();
                x = motionListener.getEventX(event, i);
                y = motionListener.getEventY(event, i);
                relative = motionListener.inRelativeMode();

                SDLActivity.onNativeMouse(buttonState, action, x, y, relative);
            } else if (toolType == MotionEvent.TOOL_TYPE_STYLUS || toolType == MotionEvent.TOOL_TYPE_ERASER) {
                pointerId = event.getPointerId(i);
                x = event.getX(i);
                y = event.getY(i);
                p = event.getPressure(i);
                if (p > 1.0f) {
                    // may be larger than 1.0f on some devices
                    // see the documentation for getPressure(i)
                    p = 1.0f;
                }

                // BUTTON_STYLUS_PRIMARY is 2^5, so shift by 4, and apply SDL_PEN_INPUT_DOWN/SDL_PEN_INPUT_ERASER_TIP
                int buttonState = (event.getButtonState() >> 4) | (1 << (toolType == MotionEvent.TOOL_TYPE_STYLUS ? 0 : 30));
                if ((event.getButtonState() & MotionEvent.BUTTON_TERTIARY) != 0) {
                    buttonState |= 0x08;
                }

                SDLActivity.onNativePen(pointerId, SDLActivity.getMotionListener().getPenDeviceType(event.getDevice()), buttonState, action, x, y, p);
            } else { // MotionEvent.TOOL_TYPE_UNKNOWN
                pointerId = event.getPointerId(i);
                p = event.getPressure(i);
                if (p > 1.0f) {
                    // may be larger than 1.0f on some devices
                    // see the documentation for getPressure(i)
                    p = 1.0f;
                }

                x = getNormalizedX(event.getX(i));
                y = getNormalizedY(event.getY(i));
                SDLActivity.onNativeTouch(touchDevId, pointerId, action, x, y, p);
            }

            // Non-primary up/down
            if (action == MotionEvent.ACTION_POINTER_UP || action == MotionEvent.ACTION_POINTER_DOWN)
                break;
        } while (++i < pointerCount);

        scaleGestureDetector.onTouchEvent(event);

        return true;
    }

    // Sensor events
    protected void enableSensor(int sensortype, boolean enabled) {
        // TODO: This uses getDefaultSensor - what if we have >1 accels?
        if (enabled) {
            SDLSensorManager.registerListener(mSensorManager, this,
                            mSensorManager.getDefaultSensor(sensortype),
                            SensorManager.SENSOR_DELAY_GAME);
        } else {
            SDLSensorManager.unregisterListener(mSensorManager, this,
                            mSensorManager.getDefaultSensor(sensortype));
        }
    }

    @Override
    public void onAccuracyChanged(Sensor sensor, int accuracy) {
        // TODO
    }

    @Override
    public void onSensorChanged(SensorEvent event) {
        if (event.sensor.getType() == Sensor.TYPE_ACCELEROMETER) {

            // Since we may have an orientation set, we won't receive onConfigurationChanged events.
            // We thus should check here.
            int newRotation;

            float x, y;
            switch (mDisplay.getRotation()) {
                case Surface.ROTATION_0:
                default:
                    x = event.values[0];
                    y = event.values[1];
                    newRotation = 0;
                    break;
                case Surface.ROTATION_90:
                    x = -event.values[1];
                    y = event.values[0];
                    newRotation = 90;
                    break;
                case Surface.ROTATION_180:
                    x = -event.values[0];
                    y = -event.values[1];
                    newRotation = 180;
                    break;
                case Surface.ROTATION_270:
                    x = event.values[1];
                    y = -event.values[0];
                    newRotation = 270;
                    break;
            }

            if (newRotation != SDLActivity.mCurrentRotation) {
                SDLActivity.mCurrentRotation = newRotation;
                SDLActivity.onNativeRotationChanged(newRotation);
            }

            SDLActivity.onNativeAccel(-x / SensorManager.GRAVITY_EARTH,
                                      y / SensorManager.GRAVITY_EARTH,
                                      event.values[2] / SensorManager.GRAVITY_EARTH);


        }
    }

    // Prevent android internal NullPointerException (https://github.com/libsdl-org/SDL/issues/13306)
    @Override
    public PointerIcon onResolvePointerIcon(MotionEvent event, int pointerIndex) {
        try {
            return super.onResolvePointerIcon(event, pointerIndex);
        } catch (NullPointerException e) {
            return null;
        }
    }

    // Captured pointer events for API 26.
    @Override
    public boolean onCapturedPointerEvent(MotionEvent event)
    {
        int action = event.getActionMasked();
        int pointerCount = event.getPointerCount();

        for (int i = 0; i < pointerCount; i++) {
            float x, y;
            switch (action) {
                case MotionEvent.ACTION_SCROLL:
                    x = event.getAxisValue(MotionEvent.AXIS_HSCROLL, i);
                    y = event.getAxisValue(MotionEvent.AXIS_VSCROLL, i);
                    SDLActivity.onNativeMouse(0, action, x, y, false);
                    return true;

                case MotionEvent.ACTION_HOVER_MOVE:
                case MotionEvent.ACTION_MOVE:
                    x = event.getX(i);
                    y = event.getY(i);
                    SDLActivity.onNativeMouse(0, action, x, y, true);
                    return true;

                case MotionEvent.ACTION_BUTTON_PRESS:
                case MotionEvent.ACTION_BUTTON_RELEASE:

                    // Change our action value to what SDL's code expects.
                    if (action == MotionEvent.ACTION_BUTTON_PRESS) {
                        action = MotionEvent.ACTION_DOWN;
                    } else { /* MotionEvent.ACTION_BUTTON_RELEASE */
                        action = MotionEvent.ACTION_UP;
                    }

                    x = event.getX(i);
                    y = event.getY(i);
                    int button = event.getButtonState();

                    SDLActivity.onNativeMouse(button, action, x, y, true);
                    return true;
            }
        }

        return false;
    }

    @Override
    public boolean onScale(ScaleGestureDetector detector) {
        float scale = detector.getScaleFactor();
        SDLActivity.onNativePinchUpdate(scale);
        return true;
    }

    @Override
    public boolean onScaleBegin(ScaleGestureDetector detector) {
        SDLActivity.onNativePinchStart();
        return true;
    }

    @Override
    public void onScaleEnd(ScaleGestureDetector detector) {
        SDLActivity.onNativePinchEnd();
    }

}
