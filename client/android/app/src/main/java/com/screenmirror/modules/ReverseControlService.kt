package com.screenmirror.modules

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.graphics.Rect
import android.os.Build
import android.util.DisplayMetrics
import android.util.Log
import android.view.InputDevice
import android.view.KeyCharacterMap
import android.view.KeyEvent
import android.view.ViewConfiguration
import android.view.accessibility.AccessibilityEvent
import com.screenmirror.protocol.*
import java.lang.reflect.Method

/**
 * 反向控制无障碍服务 — 接收控制指令，注入触摸/按键事件
 */
class ReverseControlService : AccessibilityService() {

    companion object {
        const val TAG = "ReverseControl"
        private var instance: ReverseControlService? = null
        fun getInstance(): ReverseControlService? = instance
    }

    private var screenWidth = 1080
    private var screenHeight = 1920
    private var injectMethod: Method? = null

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        Log.i(TAG, "Accessibility service connected")

        // 获取屏幕尺寸
        val metrics = resources.displayMetrics
        screenWidth = metrics.widthPixels
        screenHeight = metrics.heightPixels

        // 获取 InputManager.injectInputEvent 方法（反射，需要系统权限或签名应用）
        try {
            val inputManagerClass = Class.forName("android.hardware.input.InputManager")
            injectMethod = inputManagerClass.getMethod(
                "injectInputEvent",
                android.view.InputEvent::class.java,
                Int::class.javaPrimitiveType
            )
        } catch (e: Exception) {
            Log.w(TAG, "InputManager injection not available, using GestureDescription")
        }
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}

    override fun onInterrupt() {}

    override fun onDestroy() {
        instance = null
        super.onDestroy()
    }

    // ============================================================
    // 触摸事件处理
    // ============================================================
    fun handleTouchEvent(event: TouchEventData) {
        // 将归一化坐标转换为实际像素坐标
        val px = (event.x * screenWidth).toInt().coerceIn(0, screenWidth - 1)
        val py = (event.y * screenHeight).toInt().coerceIn(0, screenHeight - 1)

        when (event.action) {
            TouchAction.DOWN -> {
                gestureDown(px, py, event.pointerId)
            }
            TouchAction.MOVE -> {
                gestureMove(px, py, event.pointerId)
            }
            TouchAction.UP, TouchAction.CANCEL -> {
                gestureUp(px, py, event.pointerId)
            }
        }
    }

    private var lastPath: Path? = null
    private var gestureInProgress = false

    private fun gestureDown(x: Int, y: Int, pointerId: Int) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            val path = Path().apply { moveTo(x.toFloat(), y.toFloat()) }
            lastPath = path
            gestureInProgress = true
        }
    }

    private fun gestureMove(x: Int, y: Int, pointerId: Int) {
        if (gestureInProgress && lastPath != null) {
            // 累积移动路径
            lastPath?.lineTo(x.toFloat(), y.toFloat())
        }
    }

    private fun gestureUp(x: Int, y: Int, pointerId: Int) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && lastPath != null) {
            val path = lastPath!!
            path.lineTo(x.toFloat(), y.toFloat())

            val gestureBuilder = GestureDescription.Builder()
            val stroke = GestureDescription.StrokeDescription(
                path, 0,  // 从 0ms 开始
                ViewConfiguration.getLongPressTimeout().toLong()  // 持续时间
            )
            gestureBuilder.addStroke(stroke)

            dispatchGesture(gestureBuilder.build(), null, null)
            lastPath = null
            gestureInProgress = false
        }
    }

    // ============================================================
    // 按键事件处理
    // ============================================================
    fun handleKeyEvent(event: KeyEventData) {
        val keyCode = event.keyCode
        val action = when (event.action) {
            KeyAction.DOWN -> KeyEvent.ACTION_DOWN
            KeyAction.UP -> KeyEvent.ACTION_UP
        }

        val keyEvent = KeyEvent(
            event.eventTime,  // downTime
            event.eventTime,  // eventTime
            action,
            keyCode,
            0,                 // repeat
            event.metaState,
            KeyCharacterMap.VIRTUAL_KEYBOARD,
            0,                 // scancode
            KeyEvent.FLAG_FROM_SYSTEM or KeyEvent.FLAG_VIRTUAL_HARD_KEY,
            InputDevice.SOURCE_KEYBOARD
        )

        // 尝试使用 InputManager.injectInputEvent
        injectInputEvent(keyEvent, 0)
    }

    // ============================================================
    // 鼠标事件处理（映射为触摸）
    // ============================================================
    private var lastMouseX = 0f
    private var lastMouseY = 0f

    fun handleMouseEvent(event: MouseEventData) {
        val px = (event.x * screenWidth).toInt().coerceIn(0, screenWidth - 1)
        val py = (event.y * screenHeight).toInt().coerceIn(0, screenHeight - 1)

        when (event.action) {
            MouseAction.MOVE -> {
                lastMouseX = event.x
                lastMouseY = event.y
            }
            MouseAction.DOWN -> {
                gestureDown(px, py, 0)
            }
            MouseAction.UP -> {
                gestureUp(px, py, 0)
            }
        }
    }

    // ============================================================
    // 辅助方法
    // ============================================================
    private fun injectInputEvent(event: android.view.InputEvent, mode: Int) {
        try {
            injectMethod?.let { method ->
                val inputManager = getSystemService(INPUT_SERVICE)
                method.invoke(inputManager, event, mode)
            }
        } catch (e: Exception) {
            Log.e(TAG, "Inject failed: ${e.message}")
        }
    }
}
