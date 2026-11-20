# LARK A2 → Codex dictation and send (Windows)

Double-click **LARK Codex.exe** in this folder. A small window opens with **Start**, **Stop**, and a status line. Click **Start** and wait for **Ready**, then return to your Codex chat.

1. Click the LARK A2 transmitter button once. Wait for Codex's recording indicator, then speak.
2. Click the transmitter button again. The helper stops dictation, waits for Codex to finish transcription, and presses the native Send button in that same chat.

**Stop** turns the bridge off and keeps the window open. **Start** turns it back on. Closing the window exits the app and stops the bridge. You can minimize the window while using Codex. It does not start with Windows; reopen the executable after a restart. Opening it twice brings back the same window.

The receiver must be paired with a powered-on transmitter. Select **Wireless Microphone** in Codex's microphone settings (or as Windows' default input if Codex uses the default). Keep Hollyland Web Control and other receiver-setting apps closed.

## Behavior

- The helper operates only while the installed Codex app is foreground. It invokes Codex's existing Dictate, Stop dictation, and Send controls. It does not change your backtick shortcut or type into the draft.
- A second click stops recording; it does **not** send until the recording/transcription controls have cleared, Codex's Dictate control is available again, the draft contains new text, and Send is enabled.
- Switching chats/windows, transcription failure, an empty or unchanged draft, Stop, or device loss cancels automatic sending. Text already inserted into the draft is left there. If you stop the bridge while recording, finish or cancel that recording in Codex before starting another.
- Existing draft text is preserved and included with your dictated words when sent.
- The helper never presses Enter, uses the clipboard, records audio, stores transcript text, or makes network requests. Codex handles audio and transcription.
- The transmitter's noise-cancellation button changes the reported noise state. The helper restores its original setting and acts only after confirmation. The brief LED change is expected. Any other noise-setting change can also look like a click, so press Stop before adjusting it deliberately.
- Allow roughly half a second between clicks. Clicks during startup, restoration, or transcription may be ignored. Check Codex's recording indicator before speaking.
- Current English Codex accessibility labels are supported. An app update that changes the controls may require updating this helper; it leaves drafts unsent if it cannot verify the controls.

## Troubleshooting

Check the window's status line and `bridge.log` in this folder. Logs contain status/error messages, not transcripts. If restoration fails, reconnect the receiver and click Start to retry. If Codex cannot transcribe with its own microphone button, resolve its microphone selection/permission first.

Run these from PowerShell in this folder:

```powershell
.\bin\LarkCodex.Diagnostics.exe --probe        # Firmware and status queries
.\bin\LarkCodex.Diagnostics.exe --view         # Composer/state check; no text output
.\bin\LarkCodex.Diagnostics.exe --test         # Automated checks; no hardware/actions
.\bin\LarkCodex.Diagnostics.exe --record-test  # 2-second recording, then CANCEL; never send
.\bin\LarkCodex.Diagnostics.exe --stop         # Stop the running helper
```

Stop the helper before `--probe` or `--hardware-test`. The latter performs 20 real receiver cycles using software-generated noise changes at Off/Low/Medium/High, verifies exactly one event and restoration per cycle, and restores the original setting. It does not dictate or send messages. Physical button and speech verification is separate.

## Build and source

```powershell
.\build.ps1
```

Build uses the Windows-installed .NET Framework x64 C# compiler and framework assemblies. No package restore, Python, AutoHotkey, extra runtime, drivers, or administrator installation is required. **LARK Codex.exe is the single standalone app**; it does not need the source, shortcuts, or diagnostics next to it to run. The MIT notice is embedded in the executable. Source is in `src`; the developer-only diagnostics program is in `bin`.

Receiver protocol and restoration logic are adapted from **geekyshubham/lark-flow-bridge**, commit `2a8865dc37fe7949db43ef160211128f35753865`, downloaded intact to `upstream-lark-flow-bridge`. See `THIRD-PARTY-NOTICES.txt` for the MIT notice. This is an independent local utility.

The upstream reference is tracked as a Git submodule pinned to that commit. After cloning this project, run `git submodule update --init` to retrieve it (or clone with `--recurse-submodules`). Building the Windows helper does not require the upstream reference folder.
