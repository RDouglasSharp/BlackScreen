# BlackScreen

A tiny Windows utility that blacks out your screen **without** letting Windows dim or
turn off the display, so a long GPU job (LLM inference, image/video generation,
training) keeps running at full speed while your OLED panel is protected from burn-in.

## The problem

On some Windows laptops (found on an ASUS laptop with the NVIDIA **RTX Spark N1X**),
the GPU drops to a low-power state as soon as Windows turns the display off. In our
test, a GPU job running at ~65 W fell to **~5 W within seconds** of the 2-minute
display timeout, while still reporting 96% utilisation. The job didn't stop, it just
crawled. Waking the screen brought it straight back to ~65 W.

Anything that turns the panel off or dims it can do this:

- Display timeout ("Turn off my screen after…")
- Presence sensing: "Turn off my screen when I leave", "Dim my screen when I look away"
- Sleep, and screen savers that power the panel down

The usual workaround is to switch all of those off and run PowerToys Awake with
"keep screen on". That works, but it leaves an **OLED screen showing a static image
for hours**, which is exactly what causes burn-in. And you lose features you
actually want the rest of the time.

## What BlackScreen does

While it runs, BlackScreen:

- covers **every monitor** with a black window and hides the mouse pointer
  (a black OLED pixel is off, so there's nothing to burn in)
- keeps the PC awake and the display on, the same as PowerToys Awake's
  "keep awake indefinitely" + "keep screen on"
- turns off the screen saver
- sets display timeout, dim timeout, "turn off my screen when I leave" and
  "dim my screen when I look away" to never

When you **click the mouse or press Esc**, it exits and puts every setting back
exactly as it was. So you can leave all your normal power-saving and presence
features on, and just run BlackScreen when you walk away from a GPU job.

If BlackScreen is ever killed (e.g. from Task Manager) it can't clean up, so it
saves your original settings first and restores them the next time it starts.
It only restores a setting that's still at the value BlackScreen set, so it never
overwrites a change you made in between.

No admin rights needed. Nothing is installed; settings are changed only in your
active power plan and only while it runs.

## Usage

1. Download `BlackScreen.exe` from [Releases](../../releases), or build it (below).
2. Start your GPU job, run `BlackScreen.exe`, walk away.
3. Come back and click (or press Esc).

### Optional: a hotkey

With [PowerToys](https://learn.microsoft.com/windows/powertoys/) installed:
**Keyboard Manager → Remap a shortcut → Add**, press **Win+Shift+B**, set the action
to **Run Program** and point it at `BlackScreen.exe`. (Win+Shift+B isn't used by
Windows or by PowerToys' defaults. Avoid Win+Ctrl+Shift+B, which resets the graphics
driver.) Only one copy runs at a time, so pressing it twice is harmless.

If the hotkey does nothing, check the program path in the editor. A leading space
or trailing full stop picked up by copy-paste breaks it silently. Also note the
editor records which Win/Shift key you pressed (e.g. "Win (Left)").

## Build

Uses the C# compiler that ships with Windows (.NET Framework 4.x), no SDK needed:

```
build.cmd
```

Produces `BlackScreen.exe` (AnyCPU, runs on x64 and ARM64). `make-icon.ps1` draws the
icon if `BlackScreen.ico` is missing.

## Notes

- Leave "Detect when other people are looking at my screen" off. BlackScreen doesn't
  override it, and it dims the screen.
- Vendor features BlackScreen can't control need to stay off. On ASUS, that's
  **MyASUS → Customization → OLED Care → Pixel Refresh**, which blanks the panel
  when idle even with Windows' screen saver set to None.
- Tested on Windows 11 (ARM64) on an ASUS laptop with an RTX Spark N1X. Other
  machines may throttle for different reasons; check your GPU power draw
  (`nvidia-smi`) with the display on and off.

## License

MIT
