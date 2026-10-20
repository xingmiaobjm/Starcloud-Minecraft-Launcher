package com.starcloud.launcher.keycodes;

import android.view.KeyEvent;

import java.util.Arrays;

/**
 * Android 键码与 GLFW 键码的双向索引表，修改自 PojavLauncher。
 * androidKeycodes 数组按升序排列，查找使用二分搜索。
 */
public final class EfficientAndroidLWJGLKeycode {

    private static final int KEYCODE_COUNT = 112;
    private static final int[] sAndroidKeycodes = new int[KEYCODE_COUNT];
    private static final short[] sLwjglKeycodes = new short[KEYCODE_COUNT];
    private static int mTmpCount = 0;

    private EfficientAndroidLWJGLKeycode() {
    }

    static {
        /*  BINARY SEARCH IS PERFORMED ON THE androidKeycodes ARRAY !
            WHEN ADDING A MAPPING, ADD IT SO THE androidKeycodes ARRAY STAYS SORTED ! */
        // Android 键码到 GLFW 键码的映射
        add(KeyEvent.KEYCODE_UNKNOWN, LwjglGlfwKeycode.KEY_UNKNOWN);
        add(KeyEvent.KEYCODE_HOME, LwjglGlfwKeycode.KEY_HOME);
        // Escape 键
        add(KeyEvent.KEYCODE_BACK, LwjglGlfwKeycode.KEY_ESCAPE);

        // 0-9 键
        add(KeyEvent.KEYCODE_0, LwjglGlfwKeycode.KEY_0); //7
        add(KeyEvent.KEYCODE_1, LwjglGlfwKeycode.KEY_1);
        add(KeyEvent.KEYCODE_2, LwjglGlfwKeycode.KEY_2);
        add(KeyEvent.KEYCODE_3, LwjglGlfwKeycode.KEY_3);
        add(KeyEvent.KEYCODE_4, LwjglGlfwKeycode.KEY_4);
        add(KeyEvent.KEYCODE_5, LwjglGlfwKeycode.KEY_5);
        add(KeyEvent.KEYCODE_6, LwjglGlfwKeycode.KEY_6);
        add(KeyEvent.KEYCODE_7, LwjglGlfwKeycode.KEY_7);
        add(KeyEvent.KEYCODE_8, LwjglGlfwKeycode.KEY_8);
        add(KeyEvent.KEYCODE_9, LwjglGlfwKeycode.KEY_9); //16

        add(KeyEvent.KEYCODE_POUND, LwjglGlfwKeycode.KEY_3);

        // 方向键
        add(KeyEvent.KEYCODE_DPAD_UP, LwjglGlfwKeycode.KEY_UP); //19
        add(KeyEvent.KEYCODE_DPAD_DOWN, LwjglGlfwKeycode.KEY_DOWN);
        add(KeyEvent.KEYCODE_DPAD_LEFT, LwjglGlfwKeycode.KEY_LEFT);
        add(KeyEvent.KEYCODE_DPAD_RIGHT, LwjglGlfwKeycode.KEY_RIGHT); //22

        // A-Z 键
        add(KeyEvent.KEYCODE_A, LwjglGlfwKeycode.KEY_A); //29
        add(KeyEvent.KEYCODE_B, LwjglGlfwKeycode.KEY_B);
        add(KeyEvent.KEYCODE_C, LwjglGlfwKeycode.KEY_C);
        add(KeyEvent.KEYCODE_D, LwjglGlfwKeycode.KEY_D);
        add(KeyEvent.KEYCODE_E, LwjglGlfwKeycode.KEY_E);
        add(KeyEvent.KEYCODE_F, LwjglGlfwKeycode.KEY_F);
        add(KeyEvent.KEYCODE_G, LwjglGlfwKeycode.KEY_G);
        add(KeyEvent.KEYCODE_H, LwjglGlfwKeycode.KEY_H);
        add(KeyEvent.KEYCODE_I, LwjglGlfwKeycode.KEY_I);
        add(KeyEvent.KEYCODE_J, LwjglGlfwKeycode.KEY_J);
        add(KeyEvent.KEYCODE_K, LwjglGlfwKeycode.KEY_K);
        add(KeyEvent.KEYCODE_L, LwjglGlfwKeycode.KEY_L);
        add(KeyEvent.KEYCODE_M, LwjglGlfwKeycode.KEY_M);
        add(KeyEvent.KEYCODE_N, LwjglGlfwKeycode.KEY_N);
        add(KeyEvent.KEYCODE_O, LwjglGlfwKeycode.KEY_O);
        add(KeyEvent.KEYCODE_P, LwjglGlfwKeycode.KEY_P);
        add(KeyEvent.KEYCODE_Q, LwjglGlfwKeycode.KEY_Q);
        add(KeyEvent.KEYCODE_R, LwjglGlfwKeycode.KEY_R);
        add(KeyEvent.KEYCODE_S, LwjglGlfwKeycode.KEY_S);
        add(KeyEvent.KEYCODE_T, LwjglGlfwKeycode.KEY_T);
        add(KeyEvent.KEYCODE_U, LwjglGlfwKeycode.KEY_U);
        add(KeyEvent.KEYCODE_V, LwjglGlfwKeycode.KEY_V);
        add(KeyEvent.KEYCODE_W, LwjglGlfwKeycode.KEY_W);
        add(KeyEvent.KEYCODE_X, LwjglGlfwKeycode.KEY_X);
        add(KeyEvent.KEYCODE_Y, LwjglGlfwKeycode.KEY_Y);
        add(KeyEvent.KEYCODE_Z, LwjglGlfwKeycode.KEY_Z); //54


        add(KeyEvent.KEYCODE_COMMA, LwjglGlfwKeycode.KEY_COMMA);
        add(KeyEvent.KEYCODE_PERIOD, LwjglGlfwKeycode.KEY_PERIOD);

        // Alt 键
        add(KeyEvent.KEYCODE_ALT_LEFT, LwjglGlfwKeycode.KEY_LEFT_ALT);
        add(KeyEvent.KEYCODE_ALT_RIGHT, LwjglGlfwKeycode.KEY_RIGHT_ALT);

        // Shift 键
        add(KeyEvent.KEYCODE_SHIFT_LEFT, LwjglGlfwKeycode.KEY_LEFT_SHIFT);
        add(KeyEvent.KEYCODE_SHIFT_RIGHT, LwjglGlfwKeycode.KEY_RIGHT_SHIFT);

        add(KeyEvent.KEYCODE_TAB, LwjglGlfwKeycode.KEY_TAB);
        add(KeyEvent.KEYCODE_SPACE, LwjglGlfwKeycode.KEY_SPACE);
        add(KeyEvent.KEYCODE_ENTER, LwjglGlfwKeycode.KEY_ENTER); //66
        add(KeyEvent.KEYCODE_DEL, LwjglGlfwKeycode.KEY_BACKSPACE); // 退格
        add(KeyEvent.KEYCODE_GRAVE, LwjglGlfwKeycode.KEY_GRAVE_ACCENT);
        add(KeyEvent.KEYCODE_MINUS, LwjglGlfwKeycode.KEY_MINUS);
        add(KeyEvent.KEYCODE_EQUALS, LwjglGlfwKeycode.KEY_EQUAL);
        add(KeyEvent.KEYCODE_LEFT_BRACKET, LwjglGlfwKeycode.KEY_LEFT_BRACKET);
        add(KeyEvent.KEYCODE_RIGHT_BRACKET, LwjglGlfwKeycode.KEY_RIGHT_BRACKET);
        add(KeyEvent.KEYCODE_BACKSLASH, LwjglGlfwKeycode.KEY_BACKSLASH);
        add(KeyEvent.KEYCODE_SEMICOLON, LwjglGlfwKeycode.KEY_SEMICOLON); //74
        add(KeyEvent.KEYCODE_APOSTROPHE, LwjglGlfwKeycode.KEY_APOSTROPHE);
        add(KeyEvent.KEYCODE_SLASH, LwjglGlfwKeycode.KEY_SLASH); //76
        add(KeyEvent.KEYCODE_AT, LwjglGlfwKeycode.KEY_2);
        add(KeyEvent.KEYCODE_PLUS, LwjglGlfwKeycode.KEY_KP_ADD);
        add(KeyEvent.KEYCODE_MENU, LwjglGlfwKeycode.KEY_MENU);

        // 翻页键
        add(KeyEvent.KEYCODE_PAGE_UP, LwjglGlfwKeycode.KEY_PAGE_UP); //92
        add(KeyEvent.KEYCODE_PAGE_DOWN, LwjglGlfwKeycode.KEY_PAGE_DOWN);

        add(KeyEvent.KEYCODE_ESCAPE, LwjglGlfwKeycode.KEY_ESCAPE);
        add(KeyEvent.KEYCODE_FORWARD_DEL, LwjglGlfwKeycode.KEY_DELETE);

        // Control 键
        add(KeyEvent.KEYCODE_CTRL_LEFT, LwjglGlfwKeycode.KEY_LEFT_CONTROL);
        add(KeyEvent.KEYCODE_CTRL_RIGHT, LwjglGlfwKeycode.KEY_RIGHT_CONTROL);

        add(KeyEvent.KEYCODE_CAPS_LOCK, LwjglGlfwKeycode.KEY_CAPS_LOCK);
        add(KeyEvent.KEYCODE_SCROLL_LOCK, LwjglGlfwKeycode.KEY_SCROLL_LOCK);
        add(KeyEvent.KEYCODE_META_LEFT, LwjglGlfwKeycode.KEY_LEFT_SUPER);
        add(KeyEvent.KEYCODE_META_RIGHT, LwjglGlfwKeycode.KEY_RIGHT_SUPER);
        add(KeyEvent.KEYCODE_SYSRQ, LwjglGlfwKeycode.KEY_PRINT_SCREEN);
        add(KeyEvent.KEYCODE_BREAK, LwjglGlfwKeycode.KEY_PAUSE);
        add(KeyEvent.KEYCODE_MOVE_HOME, LwjglGlfwKeycode.KEY_HOME);
        add(KeyEvent.KEYCODE_MOVE_END, LwjglGlfwKeycode.KEY_END);
        add(KeyEvent.KEYCODE_INSERT, LwjglGlfwKeycode.KEY_INSERT);


        // Fn 键
        add(KeyEvent.KEYCODE_F1, LwjglGlfwKeycode.KEY_F1); //131
        add(KeyEvent.KEYCODE_F2, LwjglGlfwKeycode.KEY_F2);
        add(KeyEvent.KEYCODE_F3, LwjglGlfwKeycode.KEY_F3);
        add(KeyEvent.KEYCODE_F4, LwjglGlfwKeycode.KEY_F4);
        add(KeyEvent.KEYCODE_F5, LwjglGlfwKeycode.KEY_F5);
        add(KeyEvent.KEYCODE_F6, LwjglGlfwKeycode.KEY_F6);
        add(KeyEvent.KEYCODE_F7, LwjglGlfwKeycode.KEY_F7);
        add(KeyEvent.KEYCODE_F8, LwjglGlfwKeycode.KEY_F8);
        add(KeyEvent.KEYCODE_F9, LwjglGlfwKeycode.KEY_F9);
        add(KeyEvent.KEYCODE_F10, LwjglGlfwKeycode.KEY_F10);
        add(KeyEvent.KEYCODE_F11, LwjglGlfwKeycode.KEY_F11);
        add(KeyEvent.KEYCODE_F12, LwjglGlfwKeycode.KEY_F12); //142

        // 数字锁定与小键盘
        add(KeyEvent.KEYCODE_NUM_LOCK, LwjglGlfwKeycode.KEY_NUM_LOCK); //143
        add(KeyEvent.KEYCODE_NUMPAD_0, LwjglGlfwKeycode.KEY_KP_0);
        add(KeyEvent.KEYCODE_NUMPAD_1, LwjglGlfwKeycode.KEY_KP_1);
        add(KeyEvent.KEYCODE_NUMPAD_2, LwjglGlfwKeycode.KEY_KP_2);
        add(KeyEvent.KEYCODE_NUMPAD_3, LwjglGlfwKeycode.KEY_KP_3);
        add(KeyEvent.KEYCODE_NUMPAD_4, LwjglGlfwKeycode.KEY_KP_4);
        add(KeyEvent.KEYCODE_NUMPAD_5, LwjglGlfwKeycode.KEY_KP_5);
        add(KeyEvent.KEYCODE_NUMPAD_6, LwjglGlfwKeycode.KEY_KP_6);
        add(KeyEvent.KEYCODE_NUMPAD_7, LwjglGlfwKeycode.KEY_KP_7);
        add(KeyEvent.KEYCODE_NUMPAD_8, LwjglGlfwKeycode.KEY_KP_8);
        add(KeyEvent.KEYCODE_NUMPAD_9, LwjglGlfwKeycode.KEY_KP_9);
        add(KeyEvent.KEYCODE_NUMPAD_DIVIDE, LwjglGlfwKeycode.KEY_KP_DIVIDE);
        add(KeyEvent.KEYCODE_NUMPAD_MULTIPLY, LwjglGlfwKeycode.KEY_KP_MULTIPLY);
        add(KeyEvent.KEYCODE_NUMPAD_SUBTRACT, LwjglGlfwKeycode.KEY_KP_SUBTRACT);
        add(KeyEvent.KEYCODE_NUMPAD_ADD, LwjglGlfwKeycode.KEY_KP_ADD);
        add(KeyEvent.KEYCODE_NUMPAD_DOT, LwjglGlfwKeycode.KEY_KP_DECIMAL);
        add(KeyEvent.KEYCODE_NUMPAD_COMMA, LwjglGlfwKeycode.KEY_COMMA);
        add(KeyEvent.KEYCODE_NUMPAD_ENTER, LwjglGlfwKeycode.KEY_KP_ENTER);
        add(KeyEvent.KEYCODE_NUMPAD_EQUALS, LwjglGlfwKeycode.KEY_KP_EQUAL); //161

    }

    public static boolean containsIndex(int index) {
        return index >= 0;
    }

    public static short getValueByIndex(int index) {
        return sLwjglKeycodes[index];
    }

    public static int getIndexByKey(int key) {
        return Arrays.binarySearch(sAndroidKeycodes, key);
    }

    /** @return 在数组中按值线性查找索引 */
    public static int getIndexByValue(int lwjglKey) {
        // 不建议在性能敏感区域使用此方法
        for (int i = 0; i < sLwjglKeycodes.length; i++) {
            if (sLwjglKeycodes[i] == lwjglKey) return i;
        }
        return -1;
    }

    private static void add(int androidKeycode, short LWJGLKeycode) {
        sAndroidKeycodes[mTmpCount] = androidKeycode;
        sLwjglKeycodes[mTmpCount] = LWJGLKeycode;

        mTmpCount++;
    }

    /**
     * 接收 GLFW 键码，返回等价的 Android 键码。
     */
    public static int getAndroidKeycode(int lwjglGlfwKeycode) {
        if (lwjglGlfwKeycode == LwjglGlfwKeycode.KEY_2) return KeyEvent.KEYCODE_2;
        if (lwjglGlfwKeycode == LwjglGlfwKeycode.KEY_3) return KeyEvent.KEYCODE_3;
        int index = getIndexByValue(lwjglGlfwKeycode);
        return index >= 0 && index < sAndroidKeycodes.length
                ? sAndroidKeycodes[index]
                : KeyEvent.KEYCODE_UNKNOWN;
    }
}
