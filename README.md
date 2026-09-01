# /stalking macro

`stalking-macro.exe` is a Windows desktop application written in C#/.NET. It does **not** use AutoHotkey, ship an AHK script, start an AHK process, or require AutoHotkey to be installed.

## Included

- True-black and neutral-gray, resizable macro-hub interface with crisp Corbel typography, high-contrast text, and restrained pastel-green icon, label, outline, and active-state accents.
- Custom transparent cat icon with the complete purple-and-white sword and glowing yellow engram, embedded at multiple Windows icon sizes and reused by the system tray.
- Destiny is one consolidated sidebar workspace with top tabs for Skates, Inventory, Misc and Game Binds, matching the compact tab treatment used by Sites.
- Game Binds use a cleaner text-first layout without the redundant A/S/D/2/I/arrow/X glyph tiles.
- Game Binds now uses a crisp vector controller mark.
- Macro and Game Bind controls use a labeled keycap-style button that keeps the current activation key obvious, with a pastel-green key label and neutral hover treatment.
- Expandable Loadout Swapper: press `+` to add a second independently bound swapper with its own A/B pair; it is saved between launches and can be removed with `−`.
- Every macro card uses the same compact one-third width. Destiny macros are grouped as Skates (Hunter, Warlock and Titan), Inventory, and Misc (Rocket Grapple and Wish Wall). Only Desync exposes an activation-mode selector.
- Fortnite sits directly in the aligned sidebar and opens a two-tab workspace for Macros and Keybinds. Its two disabled-by-default native presets are Edit Spam, which repeatedly sends the saved Edit and Select binds while held, and Drag Edit, which synchronously holds Edit, waits 8 ms, then holds Select for the full activation hold. On release it sends Select-up before Edit-up so Confirm Edit on Release can complete the edit. Fortnite activation, Edit and Select binds accept keyboard and mouse inputs, including left and right mouse buttons.
- A native Sites tab embeds DIM, D2ArmorPicker, D2Checkpoint and Light.gg through Microsoft WebView2. The browser is created only when Sites is opened, so browser initialization can never prevent the macro dashboard from starting. The page includes site switches, back/forward, reload, and open-in-browser controls; cookies and Bungie sessions persist in the app's local WebView2 profile.
- Native global keyboard and mouse-side-button capture, including standalone left/right modifier keys.
- Native scan-code `SendInput` macro execution.
- Controller buttons are activation binds only. They trigger the exact same keyboard/mouse sequence and timing as the matching Keyboard macro after a short controller-release settle; no separate controller macro sequence is used.
- Activation behavior is locked where timing matters: Shatterskate, Hunter Ground Skate, Wellskate, Warlock Strand Skate, Acrobatics Skate and Loadout Swapper use One Time; Wish Wall uses Toggle. Desync retains its One Time, Hold and Toggle selector.
- First-run Air Move / Shatterdive and Super setup.
- Focused game-bind editor for movement abilities, the shared Exit Weapon, loadout-menu inputs, and every Slipstream vehicle control, including a dedicated post-patch Dodge Left bind.
- Saved Wall of Wishes profile with in-game Sensitivity, FPS and a selector for all 14 wishes.
- Fixed macro inputs: Move Forward `W`, Jump `Space`, Heavy Attack `LMB` and Sword Slot `3`.
- Shatterskate chain: sword slot `3` -> wait 400 ms -> right-click -> Jump -> wait 22 ms -> Air Move / Shatterdive -> shared Exit Weapon.
- Hunter Ground Shatterskate draws sword slot `3`, waits 400 ms, then reproduces the supplied grounded XML events: hold Jump 20 ms -> wait 17 ms -> hold light attack 21 ms -> hold Jump 15 ms -> wait 12 ms -> press Air Move / Shatterdive and Jump together -> hold both 56 ms -> release Shatterdive -> wait 46 ms -> release Jump.
- Wellskate chain: `3` -> 500 ms -> right-click -> 50 ms -> hold Jump + tap Super -> 50 ms -> `2`.
- Warlock Strand Skate uses high-resolution timing: hold `3` for 80 ms -> release -> wait 1430 ms -> hold right-click -> wait 50 ms -> hold the saved Class Ability / Rift bind (`V` by default) -> wait 10 ms -> hold the saved Super bind (`F` by default) and release right-click in the same batch -> wait 60 ms -> release Super -> wait 10 ms -> release Class Ability -> wait 1570 ms -> tap the saved Air Move bind to enter Weavewalk -> shared Exit Weapon. High-resolution timing prevents ordinary Windows delay overshoot from extending the Super activation window.
- Acrobatics Skate timing: hold sword slot `3` for 10 ms -> wait 1.08 s -> hold right-click -> 50 ms -> hold Class Ability / Dodge for 90 ms -> release both -> wait 50 ms -> tap shared Exit Weapon.
- Every Hunter, Warlock, and Titan skate taps the shared Exit Weapon bind after completion; it defaults to keyboard `2`. Desync also taps it when its activation bind is released.
- Fifteen built-in macros plus an optional second independently bound Loadout Swapper, with no arm/disarm requirement.
- Desync exposes four equal-width, centered selectors labeled exactly `L1` through `L20` above its keybind; their left-to-right position is the saved cycle order. Loadout Swapper likewise shows larger, centered A/B selectors above the activation bind. Desync scales from the active window's height and restores the cursor when released. Each step confirms the cursor position, settles for at least two configured-FPS frames, sends a complete held click, and only then moves to the next tile.
- Loadout Swapper provides two saved selectors for slots 1-20. Each activation opens Character, opens Loadouts, equips the next selected slot in an A/B/A/B cycle, and closes both menus. Every added swapper keeps its own pair and alternation state.
- Native Wish Wall automation (testing phase can be buggy) ports every pattern from `wall_menu4.ahk`, including the two-stage Shuro Chi sequence. Stand on the activation plate at the calibrated position shown beneath the wall, center the crosshair on the top-left circle, select a wish in Game Binds, then use the enabled Wish Wall activation bind. All 20 target coordinates are calculated relative to that top-left origin. After filling the wall, the macro automatically steps off and back onto the plate to submit the wish.
- Rocket Grapple is a one-time Misc macro ported from the public D2Macros AHK event order: heavy weapon slot `3` -> 450 ms ready delay -> atomic no-delay fire click -> atomic no-delay Grapple / Grenade tap -> `230 / sensitivity` raw counts of downward recoil correction. Its dedicated Misc sensitivity control accepts every Destiny 2 mouse sensitivity from 1–100. It defaults to Mouse 5 activation and `Q` for Grapple.
- Slipstream Assist is a hold-only Misc macro for free-roam and raid spaces: Ghost -> summon Sparrow -> initial ledge boost -> establish the back-left launch angle -> repeat an FPS-aligned post-9.7 cycle of Back, Destabilizer, dedicated Dodge Left and a neutral window. Steering Left and Dodge Left are separate Game Binds; the macro no longer relies on the obsolete continuous Shift+S+A/double-tap pattern. Its absolute high-resolution clock prevents pulse duration from accumulating into timing drift, and releasing the bind immediately releases every vehicle input. It cannot roll-based slipstream inside Monument of Triumph SRL because [Bungie explicitly disables Sparrow Destabilizers in that mode](https://www.bungie.net/7/en-us/News/Article/dev_insights_return_of_the_director).
- Wish Wall preserves the supplied script's sensitivity scaling, coordinates and original speed-2 recoil curve, with one small additional downward unit every 16 shots. Fractional mouse counts are carried between moves so sensitivities other than 6 do not accumulate aim drift. FPS frame-aligns a high-resolution version of the original 100 ms cadence, wall clicks use the original AHK-style immediate input, and target movement begins immediately when the activation bind is pressed. Losing focus, disabling the macro or using panic safely cancels the run.
- Administrator manifest so Windows requests elevation automatically.

Settings are stored at `%LOCALAPPDATA%\D2MacroNative\settings.json`.

The Sites tab uses the Microsoft Edge WebView2 Runtime included with current Windows/Edge installations. If the runtime or a page is unavailable, the page offers an external-browser fallback.

## Build

From this folder:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

The published folder contains one runnable application executable. AutoHotkey is not required. This project disables release PDB output.

## Automatic updates

The app checks the latest public release in `carvesyou/StalkingMacros` at startup. Each release must include an asset named exactly `stalking-macro.exe`. The downloaded EXE replaces the running copy and restarts. User settings remain in `%LocalAppData%\D2MacroNative` and are not replaced. The title-bar label is `v1.01`; the internal update version is `10.0.5` so older builds can discover this update.

Input automation can conflict with game rules or anti-cheat systems. Review the current game policies and use at your own risk.

The embedded Destiny tricorn vector is sourced from the CC0-licensed `justrealmilk/destiny-icons` project. Destiny and its logo are trademarks of Bungie, Inc.
