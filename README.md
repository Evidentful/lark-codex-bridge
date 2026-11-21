# LARK A2 → Codex dictation and send (Windows)

Double-click **LARK Codex.exe** in this folder. A small window opens with **Start**, **Stop**, and a status line. Click **Start** and wait for **Ready**, then return to your Codex chat.

- **One press** starts dictation in the open Codex chat. When new worktrees are enabled, it waits up to 2.2 seconds for a second press. Wait for Codex's recording indicator, then speak.
- **Hold Shift and press LARK once** to start a new worktree without timing a second press. Keep Shift down briefly after pressing LARK, then release it. You can also press LARK twice, about one second apart. A fast double-tap is reserved by the LARK firmware for mute and unmute.
- A new-worktree gesture opens a blank Codex chat under the existing sidebar project and starts dictation once Codex shows the new worktree. Your dictation is the first message.
- **One press during recording** stops dictation, waits for transcription, and sends the text to the chat where you started recording.

Before using a worktree gesture, check **Enable new worktrees** and review the one-time confirmation. The app registers each worktree it creates with Codex after verifying that the source repository is already trusted. If Codex still shows a trust dialog for that exact folder, the app confirms it and waits for the blank composer. Dialogs for other folders or explicitly untrusted folders are left for you to review. Unchecking the option disables new worktree creation. Trust for previously created worktrees remains in Codex.

The **Project for new worktrees** selector defaults to **Current chat folder**. You can override that with an existing local Codex project while the bridge is stopped. The destination must be a local Git repository. The app uses the selected folder's committed HEAD, creates `branch-1`, `branch-2`, and so on based on local branch names, and places the worktrees in `%LOCALAPPDATA%\LarkCodex\worktrees`. It leaves the original checkout and uncommitted changes in place. These are standard Git worktrees and remain available until you remove them.

**Stop** turns the bridge off and keeps the window open. **Start** turns it back on. Closing the window exits the app and stops the bridge. You can minimize the window while using Codex. It does not start with Windows; reopen the executable after a restart. Opening it twice brings back the same window.

The receiver must be paired with a powered-on transmitter. Select **Wireless Microphone** in Codex's microphone settings (or as Windows' default input if Codex uses the default). Keep Hollyland Web Control and other receiver-setting apps closed.

## Behavior

- The helper operates only while the installed Codex app is foreground. It invokes Codex's existing Dictate, Stop dictation, and Send controls. For **Current chat folder**, it resolves the project directly in a blank composer when there is a unique match. In existing chats, it uses Codex's **Copy working directory** shortcut (Ctrl+Shift+C) and attempts to restore the prior clipboard contents. An explicit project selection avoids the clipboard step.
- A second click stops recording; it does **not** send until the recording/transcription controls have cleared, Codex's Dictate control is available again, the draft contains new text, and Send is enabled.
- Switching chats/windows, transcription failure, an empty or unchanged draft, Stop, or device loss cancels automatic sending. Text already inserted into the draft is left there. If you stop the bridge while recording, finish or cancel that recording in Codex before starting another.
- Existing draft text is preserved and included with your dictated words when sent.
- The helper never presses Enter, records audio, or stores transcript text. Codex handles the recording and transcription.
- The transmitter's noise-cancellation button changes the reported noise state. The helper restores its original setting and acts only after confirmation. The brief LED change is expected. Any other noise-setting change can also look like a click, so press Stop before adjusting it deliberately.
- Clicks during setup, restoration, or transcription may be ignored. Check Codex's recording indicator before speaking. If the microphone is muted, quickly double-tap to unmute it before dictating.
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

## Window and duration controls

The helper starts in a larger, wider window. Drag an edge to resize or maximize it: editable timing settings and fixed references occupy two side-by-side panels that expand with the available width. Labels wrap instead of clipping. Smaller screens can scroll the whole page; neither timing panel has a nested scrollbar. All timing values and the countdown use seconds with three decimal places. Click **Reset defaults** while stopped to restore all seven editable durations. During a gesture it is raised without activating the window, so Codex keeps keyboard focus. Clicking the helper manually still changes focus and cancels pending automatic sending.

The countdown fill shrinks from right to left. With worktrees enabled, the first detected press begins a red 0.60-second no-go interval, followed by a green interval until the second-press deadline (default 2.20 seconds after the first detected press). Red is a conservative spacing guide, not a measured or configurable LARK firmware double-tap threshold. A fast physical double-tap may mute the transmitter before the helper receives any usable press. Press during green to request a worktree; let green expire for dictation in the current chat. When worktrees are disabled there is no second-press selection interval.

Stop the bridge to change these session settings (reopening restores defaults):

| Duration | Default | Meaning |
| --- | ---: | --- |
| Second-press window | 2.20 s | Total interval from first detected press to worktree gesture expiry |
| Startup / Shift release | 8 s | Wait for recording to start or Shift to be released |
| Recording acceptance | 600 s | How long a stop-and-send press is accepted after recording is confirmed |
| Transcription | 60 s | Maximum wait for a completed transcript |
| Send confirmation | 8 s | Maximum wait to confirm the invoked send completed |
| Worktree opening | 45 s | Wait to verify a newly opened worktree chat |
| Worktree focus grace | 12 s | Allow Codex to regain focus while opening a worktree |

Timeouts cancel automatic sending; they do not automatically stop Codex recording or discard the draft. Other active phase deadlines are displayed in green, with their phase name. Receiver protocol timings are fixed references shown in the settings panel: settle 1 s, debounce 0.09 s, restoration initial/retry 0.6/0.5 s (three attempts), poll 0.075 s, reconnect 1 s, status loss 2 s, shutdown restoration 2 s. Transport implementation also uses an 0.08 s noise-level command gap and a 0.015 s receiver loop. These fixed hardware timings are not the firmware double-tap threshold.

## Source map and verification

- `src/App.cs`: resizable Windows Forms UI, timing controls, status and countdown.
- `src/CodexControl.cs`: native Codex accessibility controls, gesture deadlines and send policy.
- `src/ButtonDetector.cs` and `src/NativeHid.cs`: receiver protocol and restoration.
- `src/WorktreeLaunch.cs`: Git worktree creation and Codex project integration.
- `src/Tests.cs`, `src/WindowTests.cs`, `src/Diagnostics.cs`: automated checks and opt-in interactive diagnostics.

After building, run `.\bin\LarkCodex.Diagnostics.exe --test` for protocol, timing-boundary and send-policy checks. `--layout-test` briefly opens a stopped helper and saves layout PNGs at two sizes, without touching the receiver or sending messages. Live physical-button and speech behavior must be checked separately.
