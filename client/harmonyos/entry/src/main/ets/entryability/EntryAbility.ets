// EntryAbility.ets — 应用入口
// 对应 Android 端 MainActivity.kt

import { UIAbility, Want, AbilityConstant } from '@kit.AbilityKit'
import { window } from '@kit.ArkUI'
import { hilog } from '@kit.PerformanceAnalysisKit'

const TAG = 'EntryAbility'

export default class EntryAbility extends UIAbility {
  onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    hilog.info(0x0000, TAG, 'onCreate')
    // 全局初始化可以放这里
  }

  onDestroy(): void {
    hilog.info(0x0000, TAG, 'onDestroy')
  }

  onWindowStageCreate(windowStage: window.WindowStage): void {
    hilog.info(0x0000, TAG, 'onWindowStageCreate')

    // 创建主页面
    windowStage.loadContent('pages/MainPage', (err, data) => {
      if (err.code) {
        hilog.error(0x0000, TAG, `Failed to load MainPage: ${err.code}`)
        return
      }
      hilog.info(0x0000, TAG, 'MainPage loaded')
    })

    // 设置窗口属性
    windowStage.getMainWindow().then((win) => {
      // 允许窗口无极缩放
      win.setWindowLayoutFullScreen(true)
      // 保持屏幕常亮（投屏中）
      win.setWindowKeepScreenOn(true)
    })
  }

  onWindowStageDestroy(): void {
    hilog.info(0x0000, TAG, 'onWindowStageDestroy')
  }

  onForeground(): void {
    hilog.info(0x0000, TAG, 'onForeground')
  }

  onBackground(): void {
    hilog.info(0x0000, TAG, 'onBackground')
  }
}
