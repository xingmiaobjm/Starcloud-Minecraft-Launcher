package org.lwjgl.glfw;

import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;

import com.starcloud.launcher.keycodes.EfficientAndroidLWJGLKeycode;
import com.starcloud.launcher.keycodes.LwjglKeycodeMap;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;
import java.util.concurrent.atomic.AtomicInteger;

import dalvik.annotation.optimization.CriticalNative;

/**
 * ART 侧输入桥：GLFW 链路（pre-26.x）与 SDL 链路（26.x）共用的宿主契约类。
 *
 * 职责分两面：
 * 1. C# 侧（JavaBridge）经 JNI 调用本类的 send* / ensureLoaded / loadNativeLibrary /
 *    setGrabStateCallbacks / setAppContext 完成 libpojavexec 的 ART 视角加载与输入投递；
 * 2. libpojavexec native 侧按名字解析本类的 native*（游戏 JVM 的 LWJGL 事件入口）与
 *    notifyLauncher（SDL_InitSubSystem 钩子的 launcher 集成通知，经
 *    pojav_environ->bridgeClazz + method_notifyLauncher 调用）。
 *
 * 静态块 ensureLoaded 失败有意被吞（缓冲区置 null）：JavaBridge.Initialize 的显式补加载
 * 步骤会按依赖序重载并 fail-fast，此处不能在类加载期炸出 NoClassDefFoundError。
 *
 * 契约注意：类名、方法名与签名均不可改动——native 侧按名字查找，无编译期绑定。
 */
public class CallbackBridge {
    public static final int CLIPBOARD_COPY = 2000;
    public static final int CLIPBOARD_PASTE = 2001;
    public static final int CLIPBOARD_OPEN = 2002;

    private static final int GRAB_DEBOUNCE_MILLIS = 16;

    private static final Handler MAIN_HANDLER = new Handler(Looper.getMainLooper());
    private static final KeyCharacterMap sKeyCharacterMap =
            KeyCharacterMap.load(KeyCharacterMap.VIRTUAL_KEYBOARD);

    /** 游戏线程写、UI 线程读的抓取状态（C# 侧 JavaBridge.IsGrabbing 镜像同一状态） */
    private static volatile boolean isGrabbing = false;
    private static final Object grabLock = new Object();
    /** 防抖序号：延迟回调期间状态再变即作废本次回调 */
    private static final AtomicInteger sGrabSeq = new AtomicInteger();

    private static volatile boolean sLoaded = false;
    private static volatile Runnable sGrabbedCallback = null;
    private static volatile Runnable sReleasedCallback = null;
    private static volatile Context sAppContext = null;

    public static volatile int windowWidth;
    public static volatile int windowHeight;
    public static float mouseX, mouseY, deltaX, deltaY;
    public volatile static boolean holdingAlt, holdingCapslock, holdingCtrl,
            holdingNumlock, holdingShift;
    public static boolean sGamepadDirectInput = false;

    /** GLFW direct gamepad 共享缓冲（native 创建，native 侧直接持有指针） */
    public static final ByteBuffer sGamepadButtonBuffer;
    public static final FloatBuffer sGamepadAxisBuffer;

    static {
        ByteBuffer buttonBuffer = null;
        FloatBuffer axisBuffer = null;
        try {
            ensureLoaded(null);
            buttonBuffer = nativeCreateGamepadButtonBuffer();
            try {
                axisBuffer = nativeCreateGamepadAxisBuffer()
                        .order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer();
            } catch (Throwable ignored) {
            }
        } catch (Throwable ignored) {
            // 静态块失败被吞：依赖补加载在 JavaBridge.Initialize 显式 fail-fast
        }
        sGamepadButtonBuffer = buttonBuffer;
        sGamepadAxisBuffer = axisBuffer;
    }

    /**
     * libpojavexec 的 ART 视角加载（幂等）：优先按 pojav.exec.path 属性 System.load，
     * 属性缺失时回退 System.loadLibrary（要求 so 已在 classloader namespace 内）。
     * C# 侧 Initialize 前先写属性，再 FindClass 触发本类静态块。
     */
    public static synchronized void ensureLoaded(String path) {
        if (sLoaded) {
            return;
        }
        if (path == null || path.isEmpty()) {
            path = System.getProperty("pojav.exec.path");
        }
        if (path == null || path.isEmpty()) {
            System.loadLibrary("pojavexec");
        } else {
            System.load(path);
        }
        sLoaded = true;
    }

    /** 以 ART 视角加载任意 so（System.load 必须在 Java 帧内调用，取调用者类作锚点） */
    public static synchronized void loadNativeLibrary(String path) {
        System.load(path);
    }

    /**
     * SDL 集成通知：native 侧 sdlInitSubSystemPrepare 经 pojav_environ 调用。
     * 本启动器的 SDL Java 侧装配（libSDL3 加载 + setupJNI + SDLSurface）已在
     * SdlGameActivity.onCreate 于 JVM 拉起前完成，这里无需再做；
     * native 侧唯一调用点丢弃返回值，返回 false 仅表示未做附加处理。
     */
    @SuppressWarnings("unused")
    public static boolean notifyLauncher(int type, int... actions) {
        return false;
    }

    /**
     * LWJGL 3.4.1 SDL 绑定（org.lwjgl.sdl.SDLInit）的转发入口。
     * FCL 定制 merged-modules 中声明为 Java 方法（避免额外 C 符号），空实现即可。
     */
    @SuppressWarnings("unused")
    public static void nativeNotifyLauncher(int type, int... actions) {
    }

    // ---- 输入投递：C# 侧经 JNI 调用，转发给游戏 JVM 侧的 native 实现 ----

    public static void sendCursorPos(float x, float y) {
        float dx = x - mouseX;
        float dy = y - mouseY;
        mouseX = x;
        mouseY = y;
        deltaX = dx;
        deltaY = dy;
        nativeSendCursorPos(mouseX, mouseY);
    }

    public static void sendCursorDelta(float x, float y) {
        deltaX = x;
        deltaY = y;
        mouseX += x;
        mouseY += y;
        nativeSendCursorPos(mouseX, mouseY);
    }

    public static void sendKeycode(int keycode, char keychar, int scancode,
                                   int modifiers, boolean isDown) {
        if (keycode == 0) {
            return;
        }
        int code = LwjglKeycodeMap.convertKeycode(keycode);
        if (code <= 0) {
            return;
        }
        nativeSendKey(code, scancode, isDown ? 1 : 0, modifiers);
        // 补齐桌面键盘 keydown 与字符事件成对到达的语义（lwjglx 参考 Display.keyCallback）：
        // 字母/数字/标点 keydown 暂存等 charMods 合并后投递；虚拟按键无字符时按键位反查补发
        char charToSend = keychar;
        if (isDown && charToSend == '\u0000'
                && code > 32 /* KEY_SPACE */ && code <= 96 /* KEY_GRAVE_ACCENT */) {
            charToSend = getUnicodeChar(
                    EfficientAndroidLWJGLKeycode.getAndroidKeycode(code), modifiers);
        }
        if (isDown && !Character.isISOControl(charToSend)) {
            nativeSendCharMods(charToSend, modifiers);
            nativeSendChar(charToSend);
        }
    }

    public static void sendChar(char keychar, int modifiers) {
        nativeSendCharMods(keychar, modifiers);
        nativeSendChar(keychar);
    }

    /** 按键位与修饰键反查字符，键位无字符时返回 '\0' */
    private static char getUnicodeChar(int androidKeycode, int glfwMods) {
        int meta = 0;
        if ((glfwMods & 1) != 0) meta |= KeyEvent.META_SHIFT_ON;      // GLFW_MOD_SHIFT
        if ((glfwMods & 4) != 0) meta |= KeyEvent.META_ALT_ON;        // GLFW_MOD_ALT
        if ((glfwMods & 2) != 0) meta |= KeyEvent.META_CTRL_ON;       // GLFW_MOD_CONTROL
        int unicode = sKeyCharacterMap.get(androidKeycode, meta);
        return unicode > 0 && unicode < 0x10000 ? (char) unicode : '\u0000';
    }

    public static void sendKeyPress(int keyCode, boolean status) {
        sendKeyPress(keyCode, getCurrentMods(), status);
    }

    public static void sendKeyPress(int keyCode, int modifiers, boolean status) {
        sendKeyPress(keyCode, '\u0000', 0, modifiers, status);
    }

    public static void sendKeyPress(int keyCode, int scancode, int modifiers, boolean status) {
        sendKeyPress(keyCode, '\u0000', scancode, modifiers, status);
    }

    public static void sendKeyPress(int keyCode, char keyChar, int scancode,
                                    int modifiers, boolean status) {
        sendKeycode(keyCode, keyChar, scancode, modifiers, status);
    }

    public static void sendKeyPress(int keyCode) {
        sendKeyPress(keyCode, getCurrentMods(), true);
        sendKeyPress(keyCode, getCurrentMods(), false);
    }

    public static void sendMouseButton(int button, boolean status) {
        sendMouseKeycode(button, getCurrentMods(), status);
    }

    public static void sendMouseKeycode(int button, int modifiers, boolean isDown) {
        nativeSendMouseButton(button, isDown ? 1 : 0, modifiers);
    }

    public static void sendMouseKeycode(int button) {
        sendMouseKeycode(button, getCurrentMods(), true);
        sendMouseKeycode(button, getCurrentMods(), false);
    }

    public static void sendScroll(double xoffset, double yoffset) {
        nativeSendScroll(xoffset, yoffset);
    }

    /** 按下-抬起成对投递的合成点击（33ms 后补抬起） */
    public static void putMouseEventWithCoords(int button, float x, float y) {
        putMouseEventWithCoords(button, true, x, y);
        MAIN_HANDLER.postDelayed(() -> putMouseEventWithCoords(button, false, x, y), 33);
    }

    public static void putMouseEventWithCoords(int button, boolean isDown, float x, float y) {
        sendCursorPos(x, y);
        sendMouseKeycode(button, getCurrentMods(), isDown);
    }

    public static void sendUpdateWindowSize(int width, int height) {
        windowWidth = width;
        windowHeight = height;
        nativeSendScreenSize(width, height);
    }

    public static boolean isGrabbing() {
        return isGrabbing;
    }

    public static void resetInputState() {
        nativeResetInputState();
        deltaX = 0f;
        deltaY = 0f;
        holdingAlt = false;
        holdingCapslock = false;
        holdingCtrl = false;
        holdingNumlock = false;
        holdingShift = false;
    }

    public static int getCurrentMods() {
        int currMods = holdingAlt ? 4 : 0;   // GLFW_MOD_ALT
        if (holdingCapslock) currMods |= 16; // GLFW_MOD_CAPS_LOCK
        if (holdingCtrl) currMods |= 2;      // GLFW_MOD_CONTROL
        if (holdingNumlock) currMods |= 32;  // GLFW_MOD_NUM_LOCK
        if (holdingShift) currMods |= 1;     // GLFW_MOD_SHIFT
        return currMods;
    }

    public static void setModifiers(int keyCode, boolean isDown) {
        // Android 键码（LwjglGlfwKeycode 同值）：CAPS_LOCK=280 NUM_LOCK=282
        // SHIFT=340 CONTROL=341 ALT=342
        switch (keyCode) {
            case 280:
                holdingCapslock = isDown;
                break;
            case 282:
                holdingNumlock = isDown;
                break;
            case 340:
                holdingShift = isDown;
                break;
            case 341:
                holdingCtrl = isDown;
                break;
            case 342:
                holdingAlt = isDown;
                break;
        }
    }

    /** C# 侧注册 grab 状态回调：捕获鼠标 -> 相对移动；释放 -> 绝对坐标 */
    public static void setGrabStateCallbacks(Runnable grabbed, Runnable released) {
        sGrabbedCallback = grabbed;
        sReleasedCallback = released;
    }

    public static void setAppContext(Context context) {
        sAppContext = context;
    }

    /** 游戏侧剪贴板通道（type 取 CLIPBOARD_*；返回 null 表示不支持/无内容） */
    @SuppressWarnings("unused")
    public static String accessAndroidClipboard(int type, String copy) {
        ClipboardManager clipboard = sAppContext == null
                ? null : (ClipboardManager) sAppContext.getSystemService(Context.CLIPBOARD_SERVICE);
        if (clipboard == null) {
            return null;
        }
        switch (type) {
            case CLIPBOARD_COPY:
                clipboard.setPrimaryClip(ClipData.newPlainText("Starcloud", copy));
                break;
            case CLIPBOARD_PASTE:
                if (clipboard.hasPrimaryClip()
                        && clipboard.getPrimaryClipDescription() != null
                        && clipboard.getPrimaryClipDescription().hasMimeType("text/plain")) {
                    ClipData primaryClip = clipboard.getPrimaryClip();
                    if (primaryClip != null && primaryClip.getItemCount() > 0
                            && primaryClip.getItemAt(0) != null
                            && primaryClip.getItemAt(0).getText() != null) {
                        return primaryClip.getItemAt(0).getText().toString();
                    }
                }
                return "";
            case CLIPBOARD_OPEN:
                // 打开链接：宿主无浏览器跳转需求，忽略
                break;
        }
        return null;
    }

    /** native 侧抓取状态翻转入口（libpojavexec GLFW 侧 grab 事件） */
    @SuppressWarnings("unused")
    private static void onGrabStateChanged(final boolean grabbing) {
        synchronized (grabLock) {
            isGrabbing = grabbing;
        }
        deltaX = 0f;
        deltaY = 0f;
        // 防抖：延迟期间状态再次变化则本次回调已过期，最终状态由最后一次调用应用
        final int seq = sGrabSeq.incrementAndGet();
        MAIN_HANDLER.postDelayed(() -> {
            if (seq != sGrabSeq.get()) {
                return;
            }
            Runnable callback = grabbing ? sGrabbedCallback : sReleasedCallback;
            if (callback != null) {
                callback.run();
            }
        }, GRAB_DEBOUNCE_MILLIS);
    }

    /** native 侧直连手柄启用通知（libpojavexec） */
    @SuppressWarnings("unused")
    private static void onDirectInputEnable() {
        sGamepadDirectInput = true;
    }

    // ---- native 实现（libpojavexec 按名字解析，签名不可改）----

    @CriticalNative
    public static native void nativeSetUseInputStackQueue(boolean useInputStackQueue);

    @CriticalNative
    private static native boolean nativeSendChar(char codepoint);

    @CriticalNative
    private static native boolean nativeSendCharMods(char codepoint, int mods);

    @CriticalNative
    private static native void nativeSendKey(int key, int scancode, int action, int mods);

    @CriticalNative
    private static native void nativeSendCursorPos(float x, float y);

    @CriticalNative
    private static native void nativeSendMouseButton(int button, int action, int mods);

    @CriticalNative
    private static native void nativeResetInputState();

    @CriticalNative
    private static native void nativeSendScroll(double xoffset, double yoffset);

    @CriticalNative
    private static native void nativeSendScreenSize(int width, int height);

    public static native void nativeSetWindowAttrib(int attrib, int value);

    public static native void setupBridgeWindow(Object surface);

    public static native void nativeSetGrabbing(boolean grab);

    public static native int getFps();

    private static native ByteBuffer nativeCreateGamepadButtonBuffer();

    private static native ByteBuffer nativeCreateGamepadAxisBuffer();
}
