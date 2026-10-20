package com.starcloud.launcher.keycodes;

import android.view.KeyEvent;

import java.util.HashMap;
import java.util.Map;

/**
 * Android 键码到 GLFW 键码的映射表。
 * 输入源是 Android KeyEvent（C# 宿主发送 Android 键码），
 * 转换结果交给 CallbackBridge 的 native 层使用。
 */
public final class LwjglKeycodeMap {
    private static final Map<Integer, Integer> KEY_MAP = new HashMap<>();

    private LwjglKeycodeMap() {
    }

    private static void add(int lwjglKeycode, int androidKeycode) {
        KEY_MAP.put(androidKeycode, lwjglKeycode);
    }

    /** 将 Android 键码转换为 GLFW 键码，未知键码返回 KEY_UNKNOWN。 */
    public static int convertKeycode(int androidKeycode) {
        Integer key = KEY_MAP.get(androidKeycode);
        if (key != null) {
            return key;
        }
        return LwjglGlfwKeycode.KEY_UNKNOWN;
    }

    static {
        add(LwjglGlfwKeycode.KEY_UNKNOWN, KeyEvent.KEYCODE_UNKNOWN);
        add(LwjglGlfwKeycode.KEY_HOME, KeyEvent.KEYCODE_HOME);
        add(LwjglGlfwKeycode.KEY_HOME, KeyEvent.KEYCODE_MOVE_HOME);
        add(LwjglGlfwKeycode.KEY_ESCAPE, KeyEvent.KEYCODE_ESCAPE);
        add(LwjglGlfwKeycode.KEY_ESCAPE, KeyEvent.KEYCODE_BACK);

        // 数字键 0-9
        add(LwjglGlfwKeycode.KEY_0, KeyEvent.KEYCODE_0);
        add(LwjglGlfwKeycode.KEY_1, KeyEvent.KEYCODE_1);
        add(LwjglGlfwKeycode.KEY_2, KeyEvent.KEYCODE_2);
        add(LwjglGlfwKeycode.KEY_3, KeyEvent.KEYCODE_3);
        add(LwjglGlfwKeycode.KEY_4, KeyEvent.KEYCODE_4);
        add(LwjglGlfwKeycode.KEY_5, KeyEvent.KEYCODE_5);
        add(LwjglGlfwKeycode.KEY_6, KeyEvent.KEYCODE_6);
        add(LwjglGlfwKeycode.KEY_7, KeyEvent.KEYCODE_7);
        add(LwjglGlfwKeycode.KEY_8, KeyEvent.KEYCODE_8);
        add(LwjglGlfwKeycode.KEY_9, KeyEvent.KEYCODE_9);

        // 方向键
        add(LwjglGlfwKeycode.KEY_UP, KeyEvent.KEYCODE_DPAD_UP);
        add(LwjglGlfwKeycode.KEY_DOWN, KeyEvent.KEYCODE_DPAD_DOWN);
        add(LwjglGlfwKeycode.KEY_LEFT, KeyEvent.KEYCODE_DPAD_LEFT);
        add(LwjglGlfwKeycode.KEY_RIGHT, KeyEvent.KEYCODE_DPAD_RIGHT);

        // 字母键 A-Z
        add(LwjglGlfwKeycode.KEY_A, KeyEvent.KEYCODE_A);
        add(LwjglGlfwKeycode.KEY_B, KeyEvent.KEYCODE_B);
        add(LwjglGlfwKeycode.KEY_C, KeyEvent.KEYCODE_C);
        add(LwjglGlfwKeycode.KEY_D, KeyEvent.KEYCODE_D);
        add(LwjglGlfwKeycode.KEY_E, KeyEvent.KEYCODE_E);
        add(LwjglGlfwKeycode.KEY_F, KeyEvent.KEYCODE_F);
        add(LwjglGlfwKeycode.KEY_G, KeyEvent.KEYCODE_G);
        add(LwjglGlfwKeycode.KEY_H, KeyEvent.KEYCODE_H);
        add(LwjglGlfwKeycode.KEY_I, KeyEvent.KEYCODE_I);
        add(LwjglGlfwKeycode.KEY_J, KeyEvent.KEYCODE_J);
        add(LwjglGlfwKeycode.KEY_K, KeyEvent.KEYCODE_K);
        add(LwjglGlfwKeycode.KEY_L, KeyEvent.KEYCODE_L);
        add(LwjglGlfwKeycode.KEY_M, KeyEvent.KEYCODE_M);
        add(LwjglGlfwKeycode.KEY_N, KeyEvent.KEYCODE_N);
        add(LwjglGlfwKeycode.KEY_O, KeyEvent.KEYCODE_O);
        add(LwjglGlfwKeycode.KEY_P, KeyEvent.KEYCODE_P);
        add(LwjglGlfwKeycode.KEY_Q, KeyEvent.KEYCODE_Q);
        add(LwjglGlfwKeycode.KEY_R, KeyEvent.KEYCODE_R);
        add(LwjglGlfwKeycode.KEY_S, KeyEvent.KEYCODE_S);
        add(LwjglGlfwKeycode.KEY_T, KeyEvent.KEYCODE_T);
        add(LwjglGlfwKeycode.KEY_U, KeyEvent.KEYCODE_U);
        add(LwjglGlfwKeycode.KEY_V, KeyEvent.KEYCODE_V);
        add(LwjglGlfwKeycode.KEY_W, KeyEvent.KEYCODE_W);
        add(LwjglGlfwKeycode.KEY_X, KeyEvent.KEYCODE_X);
        add(LwjglGlfwKeycode.KEY_Y, KeyEvent.KEYCODE_Y);
        add(LwjglGlfwKeycode.KEY_Z, KeyEvent.KEYCODE_Z);

        // 标点符号
        add(LwjglGlfwKeycode.KEY_COMMA, KeyEvent.KEYCODE_COMMA);
        add(LwjglGlfwKeycode.KEY_PERIOD, KeyEvent.KEYCODE_PERIOD);
        add(LwjglGlfwKeycode.KEY_GRAVE_ACCENT, KeyEvent.KEYCODE_GRAVE);
        add(LwjglGlfwKeycode.KEY_MINUS, KeyEvent.KEYCODE_MINUS);
        add(LwjglGlfwKeycode.KEY_EQUAL, KeyEvent.KEYCODE_EQUALS);
        add(LwjglGlfwKeycode.KEY_LEFT_BRACKET, KeyEvent.KEYCODE_LEFT_BRACKET);
        add(LwjglGlfwKeycode.KEY_RIGHT_BRACKET, KeyEvent.KEYCODE_RIGHT_BRACKET);
        add(LwjglGlfwKeycode.KEY_BACKSLASH, KeyEvent.KEYCODE_BACKSLASH);
        add(LwjglGlfwKeycode.KEY_SEMICOLON, KeyEvent.KEYCODE_SEMICOLON);
        add(LwjglGlfwKeycode.KEY_APOSTROPHE, KeyEvent.KEYCODE_APOSTROPHE);
        add(LwjglGlfwKeycode.KEY_SLASH, KeyEvent.KEYCODE_SLASH);

        // 修饰键
        add(LwjglGlfwKeycode.KEY_LEFT_ALT, KeyEvent.KEYCODE_ALT_LEFT);
        add(LwjglGlfwKeycode.KEY_RIGHT_ALT, KeyEvent.KEYCODE_ALT_RIGHT);
        add(LwjglGlfwKeycode.KEY_LEFT_SHIFT, KeyEvent.KEYCODE_SHIFT_LEFT);
        add(LwjglGlfwKeycode.KEY_RIGHT_SHIFT, KeyEvent.KEYCODE_SHIFT_RIGHT);
        add(LwjglGlfwKeycode.KEY_LEFT_CONTROL, KeyEvent.KEYCODE_CTRL_LEFT);
        add(LwjglGlfwKeycode.KEY_RIGHT_CONTROL, KeyEvent.KEYCODE_CTRL_RIGHT);
        add(LwjglGlfwKeycode.KEY_CAPS_LOCK, KeyEvent.KEYCODE_CAPS_LOCK);
        add(LwjglGlfwKeycode.KEY_NUM_LOCK, KeyEvent.KEYCODE_NUM_LOCK);
        add(LwjglGlfwKeycode.KEY_SCROLL_LOCK, KeyEvent.KEYCODE_SCROLL_LOCK);
        add(LwjglGlfwKeycode.KEY_LEFT_SUPER, KeyEvent.KEYCODE_META_LEFT);
        add(LwjglGlfwKeycode.KEY_RIGHT_SUPER, KeyEvent.KEYCODE_META_RIGHT);

        // 功能键
        add(LwjglGlfwKeycode.KEY_TAB, KeyEvent.KEYCODE_TAB);
        add(LwjglGlfwKeycode.KEY_SPACE, KeyEvent.KEYCODE_SPACE);
        add(LwjglGlfwKeycode.KEY_ENTER, KeyEvent.KEYCODE_ENTER);
        add(LwjglGlfwKeycode.KEY_BACKSPACE, KeyEvent.KEYCODE_DEL);
        add(LwjglGlfwKeycode.KEY_DELETE, KeyEvent.KEYCODE_FORWARD_DEL);
        add(LwjglGlfwKeycode.KEY_INSERT, KeyEvent.KEYCODE_INSERT);
        add(LwjglGlfwKeycode.KEY_PAGE_UP, KeyEvent.KEYCODE_PAGE_UP);
        add(LwjglGlfwKeycode.KEY_PAGE_DOWN, KeyEvent.KEYCODE_PAGE_DOWN);
        add(LwjglGlfwKeycode.KEY_END, KeyEvent.KEYCODE_MOVE_END);
        add(LwjglGlfwKeycode.KEY_PAUSE, KeyEvent.KEYCODE_BREAK);
        add(LwjglGlfwKeycode.KEY_PRINT_SCREEN, KeyEvent.KEYCODE_SYSRQ);
        add(LwjglGlfwKeycode.KEY_MENU, KeyEvent.KEYCODE_MENU);

        // 功能键 F1-F12
        add(LwjglGlfwKeycode.KEY_F1, KeyEvent.KEYCODE_F1);
        add(LwjglGlfwKeycode.KEY_F2, KeyEvent.KEYCODE_F2);
        add(LwjglGlfwKeycode.KEY_F3, KeyEvent.KEYCODE_F3);
        add(LwjglGlfwKeycode.KEY_F4, KeyEvent.KEYCODE_F4);
        add(LwjglGlfwKeycode.KEY_F5, KeyEvent.KEYCODE_F5);
        add(LwjglGlfwKeycode.KEY_F6, KeyEvent.KEYCODE_F6);
        add(LwjglGlfwKeycode.KEY_F7, KeyEvent.KEYCODE_F7);
        add(LwjglGlfwKeycode.KEY_F8, KeyEvent.KEYCODE_F8);
        add(LwjglGlfwKeycode.KEY_F9, KeyEvent.KEYCODE_F9);
        add(LwjglGlfwKeycode.KEY_F10, KeyEvent.KEYCODE_F10);
        add(LwjglGlfwKeycode.KEY_F11, KeyEvent.KEYCODE_F11);
        add(LwjglGlfwKeycode.KEY_F12, KeyEvent.KEYCODE_F12);

        // 小键盘
        add(LwjglGlfwKeycode.KEY_KP_0, KeyEvent.KEYCODE_NUMPAD_0);
        add(LwjglGlfwKeycode.KEY_KP_1, KeyEvent.KEYCODE_NUMPAD_1);
        add(LwjglGlfwKeycode.KEY_KP_2, KeyEvent.KEYCODE_NUMPAD_2);
        add(LwjglGlfwKeycode.KEY_KP_3, KeyEvent.KEYCODE_NUMPAD_3);
        add(LwjglGlfwKeycode.KEY_KP_4, KeyEvent.KEYCODE_NUMPAD_4);
        add(LwjglGlfwKeycode.KEY_KP_5, KeyEvent.KEYCODE_NUMPAD_5);
        add(LwjglGlfwKeycode.KEY_KP_6, KeyEvent.KEYCODE_NUMPAD_6);
        add(LwjglGlfwKeycode.KEY_KP_7, KeyEvent.KEYCODE_NUMPAD_7);
        add(LwjglGlfwKeycode.KEY_KP_8, KeyEvent.KEYCODE_NUMPAD_8);
        add(LwjglGlfwKeycode.KEY_KP_9, KeyEvent.KEYCODE_NUMPAD_9);
        add(LwjglGlfwKeycode.KEY_KP_DECIMAL, KeyEvent.KEYCODE_NUMPAD_DOT);
        add(LwjglGlfwKeycode.KEY_KP_DIVIDE, KeyEvent.KEYCODE_NUMPAD_DIVIDE);
        add(LwjglGlfwKeycode.KEY_KP_MULTIPLY, KeyEvent.KEYCODE_NUMPAD_MULTIPLY);
        add(LwjglGlfwKeycode.KEY_KP_SUBTRACT, KeyEvent.KEYCODE_NUMPAD_SUBTRACT);
        add(LwjglGlfwKeycode.KEY_KP_ADD, KeyEvent.KEYCODE_NUMPAD_ADD);
        add(LwjglGlfwKeycode.KEY_KP_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER);
        add(LwjglGlfwKeycode.KEY_KP_EQUAL, KeyEvent.KEYCODE_NUMPAD_EQUALS);

        // 补充映射（与 EfficientAndroidLWJGLKeycode 保持一致）
        add(LwjglGlfwKeycode.KEY_2, KeyEvent.KEYCODE_AT);
        add(LwjglGlfwKeycode.KEY_3, KeyEvent.KEYCODE_POUND);
        add(LwjglGlfwKeycode.KEY_KP_ADD, KeyEvent.KEYCODE_PLUS);
        add(LwjglGlfwKeycode.KEY_COMMA, KeyEvent.KEYCODE_NUMPAD_COMMA);
    }
}
