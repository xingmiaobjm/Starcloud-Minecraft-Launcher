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
 * ART      GLFW    pre-26.x   SDL    26.x          
 *
 *       
 * 1. C#   JavaBridge   JNI       send* / ensureLoaded / loadNativeLibrary /
 *    setGrabStateCallbacks / setAppContext    libpojavexec   ART           
 * 2. libpojavexec native           native*    JVM   LWJGL       
 *    notifyLauncher SDL_InitSubSystem     launcher       
 *    pojav_environ->bridgeClazz + method_notifyLauncher     
 *
 *     ensureLoaded             null  JavaBridge.Initialize       
 *            fail-fast             NoClassDefFoundError 
 *
 *                      native               
 */
public class CallbackBridge {
    public static final int CLIPBOARD_COPY = 2000;
    public static final int CLIPBOARD_PASTE = 2001;
    public static final int CLIPBOARD_OPEN = 2002;

    private static final int GRAB_DEBOUNCE_MILLIS = 16;

    private static final Handler MAIN_HANDLER = new Handler(Looper.getMainLooper());
    private static final KeyCharacterMap sKeyCharacterMap =
            KeyCharacterMap.load(KeyCharacterMap.VIRTUAL_KEYBOARD);

    /**       UI          C#   JavaBridge.IsGrabbing         */
    private static volatile boolean isGrabbing = false;
    private static final Object grabLock = new Object();
    /**                        */
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

    /** GLFW direct gamepad      native    native          */
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
            //                JavaBridge.Initialize    fail-fast
        }
        sGamepadButtonBuffer = buttonBuffer;
        sGamepadAxisBuffer = axisBuffer;
    }

    /**
     * libpojavexec   ART              pojav.exec.path    System.load 
     *         System.loadLibrary    so    classloader namespace    
     * C#   Initialize         FindClass         
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

    /**   ART        so System.load     Java                */
    public static synchronized void loadNativeLibrary(String path) {
        System.load(path);
    }

    /**
     * SDL      native   sdlInitSubSystemPrepare   pojav_environ    
     *       SDL Java     libSDL3    + setupJNI + SDLSurface   
     * SdlGameActivity.onCreate   JVM              
     * native                false           
     */
    @SuppressWarnings("unused")
    public static boolean notifyLauncher(int type, int... actions) {
        return false;
    }

    /**
     * LWJGL 3.4.1 SDL    org.lwjgl.sdl.SDLInit       
     * FCL    merged-modules      Java         C           
     */
    @SuppressWarnings("unused")
    public static void nativeNotifyLauncher(int type, int... actions) {
    }

    // ----      C#    JNI          JVM    native    ----

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
        //        keydown              lwjglx    Display.keyCallback  
        //   /  /   keydown     charMods                      
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

    /**                      '\0' */
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

    /**   -            33ms       */
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
        // Android    LwjglGlfwKeycode     CAPS_LOCK=280 NUM_LOCK=282
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

    /** C#     grab           ->         ->      */
    public static void setGrabStateCallbacks(Runnable grabbed, Runnable released) {
        sGrabbedCallback = grabbed;
        sReleasedCallback = released;
    }

    public static void setAppContext(Context context) {
        sAppContext = context;
    }

    /**          type   CLIPBOARD_*    null      /     */
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
                //                   
                break;
        }
        return null;
    }

    /** native           libpojavexec GLFW   grab     */
    @SuppressWarnings("unused")
    private static void onGrabStateChanged(final boolean grabbing) {
        synchronized (grabLock) {
            isGrabbing = grabbing;
        }
        deltaX = 0f;
        deltaY = 0f;
        //                                    
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

    /** native           libpojavexec  */
    @SuppressWarnings("unused")
    private static void onDirectInputEnable() {
        sGamepadDirectInput = true;
    }

    // ---- native    libpojavexec             ----

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
