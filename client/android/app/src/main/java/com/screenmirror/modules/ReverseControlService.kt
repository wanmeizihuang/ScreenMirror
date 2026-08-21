package com.screenmirror.modules

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.res.Configuration
import android.graphics.Path
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.DisplayMetrics
import android.util.Log
import android.view.InputDevice
import android.view.KeyCharacterMap
import android.view.KeyEvent
import android.view.WindowManager
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
    private val mainHandler = Handler(Looper.getMainLooper())

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        Log.i(TAG, "Accessibility service connected")

        updateScreenSize()
        Log.i(TAG, "Display size: ${screenWidth}x${screenHeight}")

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

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        updateScreenSize()
    }

    override fun onDestroy() {
        instance = null
        super.onDestroy()
    }

    // ============================================================
    // 触摸事件处理
    // ============================================================
    fun handleTouchEvent(event: TouchEventData) {
        mainHandler.post { handleTouchEventOnMain(event) }
    }

    private fun handleTouchEventOnMain(event: TouchEventData) {
        // 将归一化坐标转换为实际像素坐标
        val px = (event.x * screenWidth).toInt().coerceIn(0, screenWidth - 1)
        val py = (event.y * screenHeight).toInt().coerceIn(0, screenHeight - 1)

        when (event.action) {
            TouchAction.DOWN -> gestureDown(px, py)
            TouchAction.MOVE -> gestureMove(px, py)
            TouchAction.UP, TouchAction.CANCEL -> gestureUp(px, py)
        }
    }

    private var lastPath: Path? = null
    private var gestureInProgress = false
    private var gestureStartedAt = 0L

    private fun gestureDown(x: Int, y: Int) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            val path = Path().apply { moveTo(x.toFloat(), y.toFloat()) }
            lastPath = path
            gestureInProgress = true
            gestureStartedAt = SystemClock.uptimeMillis()
        }
    }

    private fun gestureMove(x: Int, y: Int) {
        if (gestureInProgress && lastPath != null) {
            // 累积移动路径
            lastPath?.lineTo(x.toFloat(), y.toFloat())
        }
    }

    private fun gestureUp(x: Int, y: Int) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && lastPath != null) {
            val path = lastPath!!
            path.lineTo(x.toFloat(), y.toFloat())
            val duration = (SystemClock.uptimeMillis() - gestureStartedAt)
                .coerceIn(50L, 1_000L)

            val gestureBuilder = GestureDescription.Builder()
            val stroke = GestureDescription.StrokeDescription(
                path, 0, duration
            )
            gestureBuilder.addStroke(stroke)

            dispatchLoggedGesture(gestureBuilder.build(), "pointer")
            lastPath = null
            gestureInProgress = false
        }
    }

    // ============================================================
    // 按键事件处理
    // ============================================================
    fun handleKeyEvent(event: KeyEventData) {
        mainHandler.post { handleKeyEventOnMain(event) }
    }

    private fun handleKeyEventOnMain(event: KeyEventData) {
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

        if (tryInjectInputEvent(keyEvent, 0)) return
        if (event.action != KeyAction.DOWN) return

        when (keyCode) {
            KeyEvent.KEYCODE_BACK, KeyEvent.KEYCODE_ESCAPE ->
                performGlobalAction(GLOBAL_ACTION_BACK)
            KeyEvent.KEYCODE_HOME ->
                performGlobalAction(GLOBAL_ACTION_HOME)
            KeyEvent.KEYCODE_APP_SWITCH ->
                performGlobalAction(GLOBAL_ACTION_RECENTS)
        }
    }

    // ============================================================
    // 鼠标事件处理（映射为触摸）
    // ============================================================
    private var activeMouseButton: Int? = null

    fun handleMouseEvent(event: MouseEventData) {
        mainHandler.post { handleMouseEventOnMain(event) }
    }

    private fun handleMouseEventOnMain(event: MouseEventData) {
        val px = (event.x * screenWidth).toInt().coerceIn(0, screenWidth - 1)
        val py = (event.y * screenHeight).toInt().coerceIn(0, screenHeight - 1)

        if (event.action != MouseAction.MOVE) {
            Log.i(
                TAG,
                "Mouse ${event.action} button=${event.button} normalized=(${event.x},${event.y}) pixels=($px,$py)"
            )
        }

        when (event.action) {
            MouseAction.MOVE -> {
                if (activeMouseButton == 0) gestureMove(px, py)
            }
            MouseAction.DOWN -> {
                if (event.button == 0 && activeMouseButton == null) {
                    activeMouseButton = 0
                    gestureDown(px, py)
                }
            }
            MouseAction.UP -> {
                if (event.button == 0 && activeMouseButton == 0) {
                    gestureUp(px, py)
                    activeMouseButton = null
                } else if (event.button == 2 && activeMouseButton == null) {
                    performGlobalAction(GLOBAL_ACTION_BACK)
                }
            }
        }
    }

    fun handleScrollEvent(event: ScrollEventData) {
        mainHandler.post { handleScrollEventOnMain(event) }
    }

    private fun handleScrollEventOnMain(event: ScrollEventData) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.N || activeMouseButton != null) return
        if (kotlin.math.abs(event.hScroll) < 0.01f &&
            kotlin.math.abs(event.vScroll) < 0.01f
        ) return

        val x = (event.x * screenWidth).coerceIn(0f, (screenWidth - 1).toFloat())
        val path = Path()
        if (kotlin.math.abs(event.hScroll) > kotlin.math.abs(event.vScroll)) {
            val startX = if (event.hScroll > 0) screenWidth * 0.35f else screenWidth * 0.65f
            val endX = if (event.hScroll > 0) screenWidth * 0.65f else screenWidth * 0.35f
            val y = (event.y * screenHeight).coerceIn(0f, (screenHeight - 1).toFloat())
            path.moveTo(startX, y)
            path.lineTo(endX, y)
        } else {
            val startY = if (event.vScroll > 0) screenHeight * 0.35f else screenHeight * 0.65f
            val endY = if (event.vScroll > 0) screenHeight * 0.65f else screenHeight * 0.35f
            path.moveTo(x, startY)
            path.lineTo(x, endY)
        }

        dispatchLoggedGesture(
            GestureDescription.Builder()
                .addStroke(GestureDescription.StrokeDescription(path, 0, 180))
                .build(),
            "scroll"
        )
    }

    // ============================================================
    // 辅助方法
    // ============================================================
    private fun tryInjectInputEvent(event: android.view.InputEvent, mode: Int): Boolean {
        return try {
            val method = injectMethod ?: return false
            val inputManager = getSystemService(INPUT_SERVICE)
            method.invoke(inputManager, event, mode) as? Boolean ?: false
        } catch (e: Exception) {
            Log.d(TAG, "System input injection unavailable: ${e.message}")
            false
        }
    }

    private fun dispatchLoggedGesture(gesture: GestureDescription, label: String) {
        val accepted = dispatchGesture(
            gesture,
            object : GestureResultCallback() {
                override fun onCompleted(gestureDescription: GestureDescription?) {
                    Log.i(TAG, "Gesture completed: $label")
                }

                override fun onCancelled(gestureDescription: GestureDescription?) {
                    Log.w(TAG, "Gesture cancelled: $label")
                }
            },
            mainHandler
        )
        Log.i(TAG, "Gesture submitted: label=$label accepted=$accepted")
    }

    private fun updateScreenSize() {
        val metrics = DisplayMetrics()
        @Suppress("DEPRECATION")
        (getSystemService(WINDOW_SERVICE) as WindowManager)
            .defaultDisplay
            .getRealMetrics(metrics)
        screenWidth = metrics.widthPixels
        screenHeight = metrics.heightPixels
    }
}
