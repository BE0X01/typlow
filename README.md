# Typlow

A little cue. Better flow.

Typlow is a minimal Windows tray app that switches between Korean and English when you return to your apps, keeps browser address bars in English, and briefly shows your input language beside the text caret.

**[Download the latest version](https://github.com/BE0X01/typlow/releases/latest)** · Windows x64 · No separate .NET installation required

## Preview

![Typlow Version 0.4 settings](docs/images/preview.png)

## Features

- **An input language for each app.** Choose Korean or English for your apps. Typlow applies that choice once when the app window regains focus. You can freely switch languages while you work.
- **English in the address bar.** In Chrome and Edge, entering the address bar switches to English. Returning to the page restores the previous Korean or English state.
- **A small language cue.** In registered apps, a brief `한` or `A` appears beside the text caret when you return to the app or press the Korean/English key. Clicking between fields in the same app does not repeatedly show the return cue.
- **App and process search.** Add an app from its visible window, search running processes, or browse for an executable. Hosted Microsoft Store apps are resolved to their actual executable.
- **Quiet tray operation.** Close the settings window to keep Typlow in the tray. A gray tray icon means automatic switching is paused.
- **Optional startup.** Start automatically in the tray when you sign in to Windows.

## Getting started

1. Download `Typlow-0.4-win-x64.zip` from [Releases](https://github.com/BE0X01/typlow/releases/latest).
2. Extract the ZIP into a folder where you have write permission, then run `Typlow.exe`.
3. Select **앱 추가** (Add app). Choose a running app, use **프로세스 검색** (Process search), or select **파일 찾기** (Browse).
4. Choose **한글** (Korean) or **영어** (English) beside each app.
5. Switch to another window and return to the registered app. Typlow applies its preferred input language once.

The settings interface is currently in Korean. Double-click the tray icon to reopen settings. Right-click it to pause/resume switching or quit Typlow.

## Settings

| Setting | What it does |
| --- | --- |
| 자동 전환 | Enable or pause automatic switching. |
| 앱별 입력 언어 | Add/remove apps and choose their preferred input language. |
| 주소창에서는 영어로 | Use English in Chrome/Edge address bars and restore the previous state afterward. |
| 입력 언어 살짝 표시 | Briefly show `한` or `A` beside the text caret in registered apps. |
| 표시 시간 | Choose how long the language cue stays visible. |
| Windows 시작 시 자동 실행 | Start in the tray when you sign in. Off by default. |

## Settings and updates

Your options and app list are saved in **`Typlow.ini`, beside `Typlow.exe`**. The file is created when you first change a setting and is saved whenever options or app rules change. Typlow does not periodically rewrite it.

If an older `%LOCALAPPDATA%\Typlow\settings.json` exists and there is no adjacent INI, Typlow imports it once. The original JSON is kept, and the INI is used afterward.

To update, quit Typlow from the tray and replace the application files while keeping `Typlow.ini`. To move Typlow, copy the INI with the executable. If startup is enabled, run Typlow once from its new location to update the startup path.

## Notes

- The language cue requires an app to expose a usable text-caret position. It is skipped when that position cannot be determined.
- Elevated apps and some IMEs or accessibility providers may restrict input switching or caret detection.
- A Microsoft Store app update can change its executable path. If its rule stops matching, add the app again.
- `SystemSettings`, `TextInputHost`, `steamwebhelper`, and `ApplicationFrameHost` are hidden from the app/process picker. Hosted apps are listed under their actual executable.
- Enabling startup manages only Typlow's entry in the current user's Windows startup settings. Typlow does not record typed text or passwords.

## License

Typlow is available under the [MIT License](LICENSE). Bundled .NET runtime licenses and third-party notices are included in the release ZIP's `licenses` folder.
