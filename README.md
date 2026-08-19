# ConfJoystick

Confjoystick is an Windows app to to emulate keypresses controlled by a Joystick. Similar to eg. Joytokey. Why a new app?  I tried many of these Apps but I could not configer all I wanted. So I let AI (ClaudeCode) create a configurable Joystick, short just confJoystick. I created it to use in Simrail or Train Driver 2 and it is not tested with other  Games.  
<img width="470" height="436" alt="Conjoystick_image" src="https://github.com/user-attachments/assets/2f439251-1ac8-4da3-a865-aa65110867fd" />

# ConfJoystick — AI - Documentation

## Getting Started

Add the config directory as asked by the confJoystick on startup. When your Joysticks are recognized, click Options/Save, to create empty config where you can configure, how your joystick inputs should be translated into keypresess.

## Overview

ConfJoystick maps physical joystick/wheel axis movements and button presses to keyboard key
events so you can play games that have no controller support. Axes are read via SharpDX
DirectInput; keys are sent as hardware scan codes (`KEYEVENTF_SCANCODE`) through the Windows
SendInput API, making them indistinguishable from a real keyboard in browsers and in
DirectInput/Raw-Input based games alike. Running ConfJoystick as Administrator may be
required for games with anti-cheat or elevated privileges.

Build instructions live in [How_to_build.md](How_to_build.md).

---

## Program Features

### 1. Joystick Detection
- Detects all connected DirectInput game controllers on startup; select the active one from
  the Joystick combo box, or click **Refresh** to re-scan after plugging in a device.
- **Press any button on a controller and that controller becomes the selected one.** Every
  attached device is watched on its own background, non-exclusive handle, so this works while
  another window has the focus. It is suppressed while emulation is running (there, button
  presses are gameplay), and pressing a button on the already-selected device changes nothing
  (re-selecting would restart the axis monitor).

### 2. Axis Monitor
- Select any axis from the Axis combo box; the live value (0–100) updates in real time so you
  can verify the axis is moving and identify its range.
- Shown to **two decimals** (e.g. `62.47`), because thresholds may themselves be decimal — the
  position you read off the monitor is one you can paste straight into a config.

### 3. Emulation Toggle
- The large **START / STOP EMULATION** button enables or disables all key emulation without
  closing the program. When stopped, any held keys are released automatically.
- Emulation processes **all** configured joysticks simultaneously, not just the selected one.

### 3a. Status Bar
- Runs along the top of the window next to the **Options** menu: the current status message
  followed by the loaded **Lever**, **Button** and **FFB** config filenames (`—` when none).
- The window has a fixed width, so entries **wrap onto further rows** when they do not fit on
  one line; a label and its filename always stay together on the same row.
- A filename longer than ~170 px is shortened with an ellipsis; hover it to see the full name.

### 4. Force Feedback

- Drives **notches** (detents) and **friction resistance** on force-feedback capable
  joysticks, wheels and levers, configured per axis in an FFB config
  (see [FFB Config](#ffb-config-_ffbjson)).
- **Notches** place spring detents at any number of positions on the 0–100 axis scale. Within
  `SnapZoneWidth` of a position the device pulls the axis toward that notch, so a plain
  analogue lever feels like a notched controller. Positions may be decimal, so a lever can
  carry far more detents than 100 whole units would allow.
- **Resistance** applies a constant friction that opposes movement, for a heavier feel. It
  does not center the axis. Either effect can be used on its own.
- The forces are calculated by the device hardware (~1 kHz), not by ConfJoystick's poll loop,
  so detents stay crisp regardless of system load.
- Starts and stops together with **START / STOP EMULATION**. **Options → Load FFB Config…**
  and **Options → Reload Config** take effect immediately, without stopping emulation, so
  `Strength` and `SnapZoneWidth` can be tuned with the lever in hand.
- The status bar reports what was actually claimed (`FFB on 1 device(s), 2 axis/axes`) and
  names any device or axis the hardware refused. Devices without force feedback support are
  skipped; key emulation continues normally.
- Notch positions and lever thresholds are read from the same unrounded axis value, so key
  steps and detents can be lined up exactly — `generate_notches.py` emits both from one run.
- Details: [FFB Parameters](#ffb-parameters), [FFB Lifecycle](#ffb-lifecycle),
  [Tips → Force feedback](#force-feedback).

### 5. Verification Field
- A text box you can click into to give it focus. While focused, emulated key presses type
  into it, letting you verify that the correct characters are produced.
- A counter next to the field shows the character count; **Clear** empties both.

### 6. Configure Keys (Button Mapping)

**Options → Configure Keys…** opens a dedicated, fully keyboard-driven window for mapping
physical controller buttons to keys, starting on the joystick selected in the main window.

**Picking a button**

- **All** attached joysticks are held open at once. Pressing a button on any of them selects
  that joystick in the window's combo box and captures the button in one step — including
  while another button is already being edited.
- Alternatively pick an existing mapping from the list with `↑` `↓` `Home` `End` `PageUp`
  `PageDown` and press `Enter`, or double-click it (a single click only highlights a row).
  The list shows only buttons that have a mapping; unmapped buttons are reached by pressing
  them on the controller.

**Editing the assignment**

- The captured button's **current** assignment is shown immediately, so you always see what
  you are about to overwrite.
- Hold Ctrl / Shift / Alt to add modifiers, then press the desired key. The preview updates
  live. Pressing a key that cannot be assigned says so in the status line instead of being
  silently ignored.

| Key | While editing a button | While picking a button |
|---|---|---|
| `Enter` | Save the assignment shown. Saving an empty assignment removes the mapping. | Edit the highlighted mapping |
| `Backspace` | **Clear** the assignment (`Enter` then removes the mapping) | Delete the highlighted mapping |
| `Esc` | Cancel the edit, leaving the mapping untouched | Close the window |

- `Enter` alone never destroys an assignment — deleting always starts with `Backspace`.
- The joystick combo box keeps its own keyboard behaviour while focused, so the device can
  also be changed with the keyboard.
- While the window is open, key emulation for buttons is suspended, so the buttons you press
  to map them do not also type their current keys into whatever window has the focus. Axis
  emulation and force feedback are unaffected.
- Mappings are held down while the button is held and released when it is released, exactly
  like a keyboard key.
- `F13`–`F24`, `Caps Lock`, `Num Lock`, `Scroll Lock`, `Print Screen`, `Pause`, the Windows
  keys and the context-menu key can all be recorded. `Enter`, `Esc` and `Backspace` cannot —
  they drive the window itself; assign those by editing the button config file by hand.
- A character that needs `Shift` is recorded as the modifier plus the key, e.g. pressing
  `Shift`+`1` on a US layout stores `SHIFT` + `1` and types `!`.
- Changes are made in memory only — use **Options → Save** to write them to disk.

### 7. Settings (settings.json)
Stored automatically in the config directory alongside lever/button configs.

| Setting | Default | Description |
|---|---|---|
| `MaxKeypressIntervalMs` | `60` | Minimum gap between consecutive queued actions (ms) — presses, holds and releases alike. |
| `DefaultPressMs` | `30` | Hold duration for press types that don't specify one. Override per-binding with `NPressMSm`. |
| `MaxConcurrentKeys` | `0` | Maximum simultaneous held keys. `0` = unlimited. |
| `DirectionChangeUnits` | `0` | How far a lever must travel back before the movement counts as a reversal and its queued presses are dropped. `0` (the default) disables the rule entirely. See [The key queue](#the-key-queue). |
| `PressQueueMaxAgeMs` | `10000` | How long a queued press may wait before it is dropped instead of sent (ms). `0` disables. Holds and releases are never dropped for age. |

The file also records the last used configs, joystick and axis — see
[Startup restore](#startup-restore).

#### The key queue

Lever bindings do not type their keys immediately. Every keyboard action a lever asks for —
a queued press from `NPress`, the key-down of a `Hold`/`KeyDown`, and the key-up that ends
one — goes onto that lever's queue and is sent in the order the lever produced it.

**Everything shares one queue, so nothing can overtake anything.** Holds used to bypass the
queue and fire instantly, which meant a hold could engage *before* presses the lever had
asked for earlier. When those presses then drained, their key-up lifted the key the hold was
still holding, and the hold silently did nothing for the rest of the range. Putting holds and
releases in the same queue as presses removes that class of bug.

**One queue per lever.** Each axis of each joystick has its own, and a single sender
round-robins between them: one action per lever per pass. A lever sweeping across a dense set
of thresholds cannot delay a different lever, while order *within* each lever stays exact.
There is deliberately only one sender thread — concurrent `SendInput` calls would interleave,
and a stroke that needs a modifier could be split so the target window reads the wrong
character.

**Pacing applies to every action**, not just presses: after each one the sender waits
`MaxKeypressIntervalMs`. This also guarantees a hold stays down for at least that long before
anything else happens, so a lever flicked quickly through a narrow range still produces a
key-down the game can actually see.

The cost is latency. Sweeping a lever across many thresholds queues actions faster than they
can be sent, so the keys lag behind the lever and keep running briefly after it stops. As a
worked example, the `Simrail_186` X axis falling from its 67 detent through 20 produces nine
actions (two hold/release pairs and five presses) — roughly 690 ms to drain at the defaults.

Two rules can discard a queued action once it no longer describes what the lever is doing:

| Rule | Setting | Default | Effect |
|---|---|---|---|
| **Age** | `PressQueueMaxAgeMs` | `10000` | A **press** that has waited longer than this is discarded when it reaches the front of the queue. |
| **Direction change** | `DirectionChangeUnits` | `0` (off) | Drops the **presses** queued on that lever as soon as it travels back `DirectionChangeUnits` from where it turned around. |

**Holds and releases are never dropped by either rule.** Discarding a release would leave its
key physically down with nothing left in the queue to lift it, so the reversal rule filters
the queue rather than emptying it, and the age rule is only ever applied to presses.

The reversal rule is **off by default**: a keystroke the lever asked for should be delayed
rather than thrown away, and the age limit already bounds how long a backlog can run on for.
Set `DirectionChangeUnits` above zero to trade keystrokes for a lever that stops sooner after
a hard reversal — its test measures travel back from the **furthest point reached**, not
movement since the last poll, so a slow one-way sweep in sub-unit steps is never mistaken for
a reversal, and neither is jitter at the end of travel. A drop is reported in the status line
(`Dropped 12 queued keypress(es) — lever direction changed`); keystrokes are never discarded
silently.

Button mappings do **not** use the queue — they press and release inline, so a button stays
instant and never waits behind a lever's backlog.

#### Startup restore

The lever config in use when the app was last closed is reopened on the next start, together
with the button and FFB configs it names as associations. A button or FFB config that was last
loaded on its own is restored too, and a config file that has since been deleted or renamed is
simply skipped rather than reported as a broken association.

The last selected joystick and axis are restored the same way.

### 8. Loading and Saving Configs

All config commands live under the **Options** menu:

| Menu item | Effect |
|---|---|
| **Reload Config** | Re-reads **every config that is currently loaded** from disk — lever, button and FFB — whether or not the lever config names them as associations. Force feedback is restarted so the new values take effect immediately. |
| **Save** | Saves the button config, then the lever config, back to the files they were loaded from. Prompts for a filename the first time. |
| **Load / Save Lever Config As…** | Lever (axis) configuration. Saving records the currently loaded button and FFB config filenames as associations. |
| **Load / Save Button Config As…** | Button configuration. |
| **Load FFB Config…** | Force feedback configuration. Takes effect immediately, even while emulation is running. |
| **Change Config Directory…** | Chooses the directory all config files are read from and written to. |
| **Configure Keys…** | Opens the button mapping window. |

- Saving a lever config writes every detected joystick and all 8 standard axes; axes with no
  events get an empty `Events` array. Loading merges the file with the currently detected
  devices (newly detected axes are added automatically).
- Edit the JSON files in any text editor. Trailing commas and `//` line comments are allowed.
- The last used configs are reopened automatically on the next start — see
  [Startup restore](#startup-restore).
- Key names are written to the file exactly as you would type them. `"NUM+"` used to be
  saved as `"NUM\u002B"`, and `<`, `>`, `&`, `` ` `` and `'` were mangled the same way;
  they are now written literally. Older config files containing the escaped form still load
  correctly — the two are the same string as far as JSON is concerned.

#### Reload vs. Load

The two are deliberately different when a lever config does **not** name an association:

| | Lever config names the button/FFB config | Lever config names nothing |
|---|---|---|
| **Load Lever Config…** | Loads the named file. | Drops the loaded button/FFB config. The file is authoritative — otherwise the previous config's mappings would survive into an unrelated lever config and be written into its file on the next save. |
| **Reload Config** | Re-reads the named file. | **Keeps and re-reads whatever is loaded.** Reload never throws away a config you loaded by hand. |

This is what makes an FFB config loaded via **Load FFB Config…** survive a reload even before
the lever config has been saved with the association in it.

---

## Directories

| Location | File | Purpose |
|---|---|---|
| Exe directory | `config_dir.txt` | Stores the path to the user-chosen config directory. |
| Config directory | `settings.json` | App settings (auto-loaded configs, key limits, press interval). |
| Config directory | `*_levers.json` | Lever (axis) configuration files. |
| Config directory | `*_buttons.json` | Button configuration files. |
| Config directory | `*_ffb.json` | Force feedback configuration files. |
| Repo root | `generate_notches.py` | Generates evenly spaced notch positions plus the matching Rising/Falling lever switch points, and copies them to the clipboard. |

The config directory is chosen on first run and can be changed via the File menu.

---

## Axis Names

| Name | Description |
|---|---|
| `X` | Primary horizontal axis |
| `Y` | Primary vertical axis |
| `Z` | Primary twist / throttle axis |
| `RotationX` | Secondary horizontal (Rx) |
| `RotationY` | Secondary vertical (Ry) |
| `RotationZ` | Secondary twist (Rz) |
| `Slider0` | First slider / throttle |
| `Slider1` | Second slider / throttle |

All axis values are normalised to **0–100** (0 = minimum, 100 = maximum); the DirectInput raw
range (0–65535) is mapped automatically. The scale is **not** limited to whole numbers —
thresholds, hold ranges and notch positions may all be decimal.

---

## Directions

| Value | Description |
|---|---|
| `Rising` (aliases `Right`, `Down`) | Fires when the axis value crosses the threshold going upward. |
| `Falling` (aliases `Left`, `Up`) | Fires when the axis value crosses the threshold going downward. |
| `Both` | Fires on both Rising and Falling crossings. |

---

## Press Types

| Value | Description |
|---|---|
| `KeyDown` | Sends a key-down event. The key stays held until a matching `KeyUp` fires (or emulation is stopped). |
| `KeyUp` | Sends a key-up event. Use paired with `KeyDown`. |
| `Hold` | Holds the key down while the axis is past the threshold; releases automatically when the axis moves back. |
| `1Press` / `NPress` | Sends N complete key presses (down + up). Hold duration uses `DefaultPressMs`. Any positive integer is accepted (e.g. `5Press`, `10Press`). |
| `NPressMSm` | Sends N key presses, each held for exactly **m** milliseconds. Overrides `DefaultPressMs` for this binding only. e.g. `3PressMS50` = 3 presses held 50 ms each. |

Legacy aliases still accepted: `SinglePress` = `1Press`, `DoublePress` = `2Press`.

---

## Threshold

A single value (0–100) or an array of values; each value in the array acts as an independent
crossing point for the same event. For Hold events, each threshold tracks its hold state
separately.

**Values may be decimal**, in both the plain form and the
[hold-range](#hold-ranges-key-held-while-axis-is-within-a-range) form, so a lever can be split
far more finely than 100 steps:

```json
"Threshold": 75
"Threshold": [25, 50, 75]
"Threshold": 62.5
"Threshold": [0, 1.162791, 2.325581, 3.488372]
"Threshold": "30.25-70.75"
"Threshold": ["55.5-65.5", "80-90"]
```

| Point | Detail |
|---|---|
| Resolution | The axis is compared at its **full unrounded resolution**. One 0–100 unit is ~655 raw axis counts, so roughly three decimal places are meaningful; beyond that neighbouring thresholds fire on the same physical position. |
| Decimal separator | Always a **point**, never a comma — inside a JSON number (`62.5`) and inside a range string (`"30.25-70.75"`) alike. Range strings use the invariant culture, so a config is portable between machines with different regional settings. |
| Whole numbers | Written back unchanged (`60`, not `60.0`), so existing configs stay readable after a save. |
| Quoted numbers | A bare number in quotes (`"62.5"`) is read as a plain threshold, not as a zero-width range. |
| Mixed lists | A list may combine both forms — `"Threshold": ["30-40", 55.5]` holds across 30–40 *and* fires on the 55.5 crossing. |

**Behaviour change for existing configs.** Lever emulation used to round the axis to whole
units before comparing; it now compares against the unrounded position, so a `Rising`
threshold of `60` fires when the axis truly reaches 60.0 rather than at 59.5. Crossings land
up to half a unit later than before. Nothing needs changing in a config — only the moment of
the crossing shifts.

---

## Supported Keys

| Category | Keys |
|---|---|
| Letters | `A` – `Z` |
| Digits | `0` – `9` |
| Function | `F1` – `F24` |
| Navigation | `SPACE`, `ENTER` / `RETURN`, `TAB`, `ESCAPE` / `ESC`, `BACKSPACE`, `DELETE` / `DEL`, `INSERT` / `INS`, `HOME`, `END`, `PAGEUP` / `PGUP`, `PAGEDOWN` / `PGDN`, `UP`, `DOWN`, `LEFT`, `RIGHT` |
| Modifiers | `SHIFT`, `LSHIFT`, `RSHIFT`, `CTRL` / `CONTROL`, `LCTRL`, `RCTRL`, `ALT`, `LALT`, `RALT` |
| Numpad | `NUMPAD0`–`NUMPAD9` / `NUM0`–`NUM9`, `NUMPAD+` / `NUMADD` / `NUM+`, `NUMPAD-` / `NUMSUBTRACT` / `NUM-`, `NUMPAD*` / `NUMMULTIPLY` / `NUM*`, `NUMPAD/` / `NUMDIVIDE` / `NUM/`, `NUMPAD.` / `NUMDECIMAL` / `NUM.` |
| Lock / system | `CAPSLOCK` / `CAPS`, `NUMLOCK`, `SCROLLLOCK` / `SCROLL`, `PRINTSCREEN` / `PRTSC` / `SNAPSHOT`, `PAUSE` / `BREAK`, `CLEAR`, `HELP`, `CANCEL` |
| Windows | `LWIN` / `LEFTWIN` / `WIN`, `RWIN` / `RIGHTWIN`, `APPS` / `CONTEXTMENU` |
| Physical punctuation keys | `.` / `PERIOD` / `DOT`, `,` / `COMMA`, `-` / `MINUS`, `=` / `EQUALS`, `;` / `SEMICOLON`, `/` / `SLASH`, `` ` `` / `TILDE` / `BACKTICK`, `[` / `LBRACKET`, `]` / `RBRACKET`, `\` / `BACKSLASH`, `'` / `QUOTE` / `APOSTROPHE`, `OEM102` |
| Any other character | A single character, e.g. `!` `?` `:` `%` `~` `&` `^` `+` `<` `>` `"` `@` `#` `{` `\|` `€` `ö` — see below |
| Unicode escape | `U+XXXX`, e.g. `U+20AC` for `€` |
| Disabled | `NONE` (or an empty string) — see below |

Key names are **case-insensitive** in the JSON.

### Special characters

Anything that is not one of the names above is treated as a **character to produce**, and is
looked up on the keyboard layout that is active at the time. The lookup returns both the key to
press and the modifiers that make it print that character, so all of these work without being
named individually:

```json
{ "Threshold": 40, "Direction": "Rising", "PressType": "1Press", "Key": "!" }
{ "Threshold": 60, "Direction": "Rising", "PressType": "1Press", "Key": "@" }
{ "Threshold": 80, "Direction": "Rising", "PressType": "1Press", "Key": "€" }
```

On a Swiss German layout those resolve to `Shift`+`¨`, `AltGr`+`2` and `AltGr`+`E`; on a US
layout `!` resolves to `Shift`+`1` instead. The active layout is checked on every keystroke, so switching
layouts while ConfJoystick runs is picked up without a reload.

Use `U+XXXX` for characters that are awkward to put in a JSON string — `U+0022` for `"` and
`U+005C` for `\` avoid having to escape them. Only the Basic Multilingual Plane (`U+0001` to
`U+FFFF`) is supported; a character above it would need a surrogate pair, which a single
keystroke cannot carry.

**Characters the layout cannot produce at all** are injected as Unicode directly. Text fields
accept them, but anything reading raw scan codes — which is most games — will not see them.
That is unavoidable: there is no key to press for a character the keyboard does not have.

**Names win over characters.** The punctuation names in the table above identify a *physical
key* (see **Punctuation and keyboard layouts** below), so `"Key": "'"` presses the apostrophe key
position rather than producing an apostrophe — on a German layout that key prints `ö`. Write
`U+0027` if you want the character regardless of layout.

Two names changed meaning to make room for this:

| Key field | Before | Now |
|---|---|---|
| `"+"` / `"PLUS"` | The `=` key (so it printed `=` on a US layout) | The character `+` |
| `"<"` | The extra ISO key next to the left `Shift`, absent on US keyboards | The character `<` |

`"="` / `"EQUALS"` and `"OEM102"` still name those two physical keys, so both behaviours remain
available. The button mapping window now writes `OEM102` where it used to write `<`.

### Disabling an event or button with `NONE`

`"Key": "NONE"` switches a binding off while leaving everything else about it in place:

```json
{ "Threshold": 62.5, "Direction": "Rising", "PressType": "SinglePress", "Key": "NONE" }
```

The event is skipped at runtime and — unlike a misspelled key name — it is **not** reported as
a warning when the config is loaded. That distinction is the whole point: a typo should still
be caught, so `"Key": "NONE"` is the way to say "off" on purpose. An empty string (`"Key": ""`)
behaves the same way. Useful for bisecting a dense lever config: disable events one at a time
to find the one that misbehaves, without deleting their thresholds. Works for button mappings
too.

**Punctuation and keyboard layouts.** The punctuation names identify a *physical key position*
(the Windows `VK_OEM_*` codes), spelled with the symbol that key prints on a US layout. Key
presses are sent as scan codes, so a mapping always reproduces the same physical key — and
therefore the same character — on the layout it was recorded on. `.` and `,` are in the same
place on QWERTY and QWERTZ, so those two are portable; a config moved between layouts may
produce a different character for the others (e.g. `=` is the `+` key on a German layout).
The mapping window writes these names automatically: press `.` while assigning a key and the
mapping is stored as `"Key": "."`.

---

## Config File Format

### Lever Config (`*_levers.json`)

```json
{
  "AssociatedButtonConfigFile": "my_buttons.json",
  "AssociatedFfbConfigFile":    "my_ffb.json",
  "Joysticks": [
    {
      "JoystickName": "<exact device name as shown in the combo box>",
      "Axes": [
        {
          "Axis": "<axis name>",
          "Events": [
            {
              "Threshold": "<0-100, decimals allowed, or [array]>",
              "Direction": "<Rising|Falling|Both|Left|Right|Up|Down>",
              "PressType": "<KeyDown|KeyUp|Hold|NPress|NPressMSm>",
              "Key":       "<key name>"
            }
          ]
        }
      ]
    }
  ]
}
```

Both `Associated…` fields are optional; they auto-load the named button and FFB configs.

### Button Config (`*_buttons.json`)

```json
{
  "Joysticks": [
    {
      "JoystickName": "<exact device name>",
      "Buttons": [
        {
          "Button":    0,
          "Key":       "<key name>",
          "Modifiers": ["CTRL", "SHIFT"]
        }
      ]
    }
  ]
}
```

- `JoystickName` must match the device name exactly (case-insensitive).
- Button indices are 0-based (Button 1 in the UI = index `0` in the config).
- `Modifiers` can be an empty array if no modifiers are needed.
- Axes with no events can use an empty `Events` array or be omitted entirely.

### FFB Config (`*_ffb.json`)

```json
{
  "Devices": [
    {
      "DeviceName": "<exact device name as shown in the combo box>",
      "Axes": [
        {
          "Axis": "X",
          "Notches": {
            "Enabled": true,
            "Positions": [ 25, 50, 75 ],
            "SnapZoneWidth": 5,
            "Strength": 5000
          },
          "Resistance": {
            "Enabled": true,
            "Strength": 3000
          }
        }
      ]
    }
  ]
}
```

Either `Notches` or `Resistance` (or both) can be omitted or set to `"Enabled": false` to skip
that effect on the axis.

**Linking to a lever config** — whichever button and FFB configs are loaded when the lever
config is saved are written back as `AssociatedButtonConfigFile` / `AssociatedFfbConfigFile`.
So linking an FFB config is simply: load the lever config → **Options → Load FFB Config…** →
**Options → Save**. If nothing is loaded, the field is written as `""`, which unlinks it again.
An FFB config can also be used unlinked; **Reload Config** keeps it either way.

#### FFB Parameters

| Field | Type | Range | Description |
|---|---|---|---|
| `DeviceName` | string | — | Must match the device name shown in the joystick combo box. |
| `Axis` | string | — | Axis name (`X`, `Y`, `Z`, `RotationX`, `RotationY`, `RotationZ`, `Slider0`, `Slider1`). |
| `Notches.Positions` | **decimal[]** | 0–100 | Notch positions on the normalized 0–100 axis scale. **Decimals are allowed.** Order does not matter — the nearest notch always wins. |
| `Notches.SnapZoneWidth` | **decimal** | > 0 | Maximum **pull range**: how far, in normalized units, a notch can reach. See *Notch spacing and pull range* below. |
| `Notches.Strength` | int | 0–**10000** | Spring coefficient inside the pull range. 10000 = DI_FFNOMINALMAX (device-rated maximum). Higher = stronger pull toward notch center. |
| `Resistance.Strength` | int | 0–**10000** | Friction coefficient in DirectInput units. 10000 = DI_FFNOMINALMAX. |

#### Notch spacing and pull range

`SnapZoneWidth` is the **maximum distance over which a notch pulls the axis**. What that
produces depends on how far apart the notches actually are, and it is evaluated **per gap**,
so unevenly spaced notches behave correctly:

| Gap between two neighbouring notches | Behaviour in that gap |
|---|---|
| **narrower** than `2 × SnapZoneWidth` | The axis is always pulled to whichever notch is nearer. No free travel — the pull flips at the midpoint. This is how a real notched controller feels. |
| **wider** than `2 × SnapZoneWidth` | Each notch gets a ±`SnapZoneWidth` zone; between the zones the axis moves freely (spring coefficient zero). |

So a single `SnapZoneWidth` can give continuous detents in a densely notched band and free
travel elsewhere on the same axis, with no extra configuration.

#### Notch resolution

One 0–100 unit is 200 DirectInput offset units (the ±10000 spring-offset space), so the
practical resolution is far finer than whole numbers:

| Layout | Spacing (0–100 scale) | Raw axis counts | DI offset units |
|---|---|---|---|
| 44 notches across 0–50 | 50/43 ≈ 1.162791 | ≈ 762 | ≈ 233 |
| 29 notches across 0–50 | 50/28 ≈ 1.785714 | ≈ 1170 | ≈ 357 |
| smallest useful spacing | 0.005 | ≈ 3 | 1 |

Note that *n* notches spanning a range inclusive of both ends leave *n − 1* intervals, so the
spacing divisor is one less than the notch count. Positions closer together than ≈0.005 round
to the same spring centre and act as one notch; the config validator warns when that happens.

#### Generating an evenly spaced list

Positions are written out explicitly, so for a dense run use `generate_notches.py` in the
repository root. Run it with Python and enter the start, end and notch count as
comma-separated values, optionally followed by a trip percentage (see *Matching switch points*
below):

```
> python generate_notches.py

Start, End, Notches, Trip%: 0, 50, 44

FFB config - "Positions":
[
  0, 1.162791, 2.325581, 3.488372, ... 47.674419, 48.837209, 50
],

Lever config - "Events":
{ "Threshold": [ 0.755814, 1.918605, 3.081395, ... ],
  "Direction": "Rising",  "PressType": "1Press", "Key": "NUMPAD+" },

{ "Threshold": [ 0.406977, 1.569767, 2.732558, ... ],
  "Direction": "Falling", "PressType": "1Press", "Key": "NUMPAD-" }

44 notches from 0 to 50 - spacing 1.162791
  ~233 DirectInput offset units, ~762 raw axis counts apart
  set SnapZoneWidth above 0.581395 for continuous pull to the nearest notch, below it for free travel

43 Rising switch points at 65% of each gap, 43 Falling at 35%
  dead band 0.348837 wide between the two (229 raw counts)
  0.174419 of margin past the halfway line where the spring takes over

Positions copied to clipboard.

Press Enter to run again, C to copy the lever events, or Q to quit:
```

The `Positions` list is printed **and copied to the clipboard**, ready to paste into the FFB
config; press `C` at the prompt to put the `Events` block on the clipboard instead. Notes:

- Both ends are notches, so **N notches leave N−1 intervals**.
- Start and end may be given in either order, and must lie within 0–100.
- It prompts to go again after each run, so several lists can be generated in one session —
  Enter to repeat, `Q` (or Ctrl+C) to exit. A rejected input re-prompts rather than exiting.
- It warns if the requested notches would be closer together than DirectInput can resolve.
- Standard library only — no `pip install`. It uses `clip.exe` for the clipboard and falls back
  to `tkinter`; if neither works it says so and the printed list can be copied by hand.

The equivalent as a PowerShell one-liner, if you would rather not leave the shell:

```powershell
# 44 notches spanning 0–50 inclusive
(0..43 | ForEach-Object { [math]::Round($_ * 50 / 43, 6) }) -join ', '
```

Dropped into a config, with `SnapZoneWidth` of 5 comfortably exceeding half the 1.16 spacing,
the whole 0–50 band becomes continuously notched while 50–100 stays free.

#### Matching switch points

The generated `Events` block places one `Rising` and one `Falling` threshold in **each gap
between adjacent notches**, so a keystroke fires exactly once per detent the lever is moved
through — and only once the lever has committed to that detent.

The spring changes which notch it pulls towards at the **halfway line** of a gap. A `Rising`
threshold placed past that line therefore only fires when the lever is already being pulled
onwards to the next notch, so it cannot spring back and retrigger. `Falling` sits mirrored on
the other side:

```
notch i                                             notch i+1
|---------------|---------------|---------------|---------------|
0%             35%             50%             65%            100%
             Falling      spring tips        Rising
```

Trip% is the `Rising` position within the gap; `Falling` is placed at 100−Trip%.

| Trip% | Effect |
|---|---|
| **65** (default) | 15% of the gap as margin past the tipping point, 30% dead band. Absorbs the play most levers have at a detent. |
| **75** | The textbook 75/25 split — maximum margin on both sides, but the key fires late in the travel. |
| ≤ 50 | **Rejected.** The key would fire before the spring commits, so the lever could fall back and trigger it again. |
| 50–55 | Accepted with a warning — very little room for lever play. |

The **dead band** between `Falling` and `Rising` in the same gap is the slack the lever can
wander within without toggling anything; keep it comfortably wider than the physical play at a
detent. The script warns when the switch points end up closer together than the axis can
resolve.

`Key` and `PressType` in the generated block are placeholders (`NUMPAD+` / `NUMPAD-`,
`1Press`) — edit them in place, or change `RISING_KEY`, `FALLING_KEY` and `PRESS_TYPE` at the
top of the script. Since notch positions and switch points come out of the same run, the FFB
detents and the key steps stay aligned by construction.

#### FFB Lifecycle

- FFB starts and stops with emulation (the **START / STOP EMULATION** button).
- **Reloading takes effect immediately.** Loading an FFB config, or **Options → Reload
  Config**, tears down the running effects and rebuilds them from the new values without
  stopping emulation.
- When force feedback comes up, the status line reports what was actually claimed, e.g.
  `Emulation ACTIVE — FFB on 1 device(s), 2 axis/axes`. If it reports `no FFB-capable device
  matched the config`, nothing is being driven — check the device name and that no other
  application holds the device exclusively.
- **What is running is always reported first, problems are appended to it**, so a config asking
  for one thing the hardware cannot do never looks like a dead force-feedback loop. With more
  than one problem the line ends `— N issues, first: …`; hover the status line for the full text.
- FFB devices are acquired **exclusively** (a DirectInput requirement for force feedback
  output), preferring **background** mode so effects keep running once the game rather than
  ConfJoystick has the window focus. Drivers that refuse exclusive background access fall back
  to foreground automatically — on those, FFB is only active while ConfJoystick has the focus.
- Errors are reported in the status line rather than being swallowed, naming the device, axis
  and reason. Two wordings are deliberately different:
  - *"the device has no Spring/Friction effect"* — the hardware does not advertise that effect
    type at all, so the config is asking for something it can never do. Turn the section off,
    or move it to hardware that has it.
  - *"device rejected the … effect"* — the device advertises the effect but refused it on that
    particular axis. The usual cause is an axis with no force-feedback motor behind it: a wheel
    typically drives only its steering axis, so a `Y` (pedal) section is rejected while `X` works.
- `Resistance` with `"Enabled": true, "Strength": 0` is treated as **disabled** — a friction
  effect with a zero coefficient produces no force, so the device is never asked for one.
  Loading such a config warns about it.
- If a device is lost (unplugged, power-management wake, or another app taking it), the FFB
  loop keeps retrying and re-downloads its effects as soon as the device comes back.
- Devices without DirectInput force feedback support are skipped — emulation continues normally.
- Auto-centering is disabled on FFB devices while emulation is active, and re-enabled when
  emulation stops.
- The `Resistance` (friction) effect opposes movement but does **not** center the axis.
- The `Notches` effect uses a DirectInput **Spring** condition effect. Within `SnapZoneWidth`
  of a notch position the device applies a spring force pulling toward that notch center —
  calculated by the device hardware at its internal rate (~1 kHz), not our software poll rate.
  Outside all pull ranges the spring coefficient is set to zero (no force).
- Notch positions are read from the axis at full resolution (unrounded), as are lever
  thresholds; the axis monitor displays that same position to two decimals. Notch density is
  therefore limited by the device, not by the config.
- Spring parameters are only re-sent to the device when they actually change, so a lever
  resting inside or outside a notch costs no device traffic.

#### DirectInput constraints worth knowing

Two DirectInput rules are easy to get wrong and both fail silently:

| Rule | Consequence if broken |
|---|---|
| `DIPROP_AUTOCENTER` may only be written while the device is **unacquired**. | Setting it after `Acquire()` fails with `DIERR_ACQUIRED`; the device is dropped and no force feedback runs at all. On teardown it leaves the device limp. |
| A downloaded effect's **axes and direction cannot be changed**. Live updates must send only the type-specific parameters (`DIEP_TYPESPECIFICPARAMS`). | SharpDX's single-argument `Effect.SetParameters(ep)` passes `EffectParameterFlags.All`, which includes `Axes` and `Direction`, so every notch update is rejected and the notch never moves — while a friction effect configured once at creation keeps working. |

---

## Config Validation

### Lever configs

When a lever config is loaded, ConfJoystick checks every event's `Direction`, `PressType` and
`Key`. If any are invalid, a warning popup lists each problem with its line number and the
correct syntax, e.g.:

```
Line 14: Invalid PressType "1Press2000"
  Valid values: Hold, KeyDown, KeyUp, SinglePress, DoublePress,
               nPress (e.g. 3Press), nPressMSm (e.g. 3PressMS50)

Line 22: Invalid Direction "Upward"
  Valid values: Rising, Falling, Both, Right, Down, Left, Up

Line 30: Unknown Key "Wq" — this event will never fire.
  Valid values: A-Z, 0-9, F1-F24, SPACE, ENTER, TAB, ESC, BACKSPACE, ...
```

Only multi-character names can be unknown. A single character is always valid — it is either a
key on the current layout or a Unicode injection — so `"Key": "!"` is never reported, while a
typo like `"Key": "Wq"` still is.

`"Key": "NONE"` and `"Key": ""` are **not** reported — those disable the event deliberately.
`Direction` and `PressType` are still checked on a disabled event, so it is ready to go when
you switch it back on.

The reported line number is found by matching the field name and the offending value **as a
pair** (`"PressType": "d"`). Matching them independently used to point at the wrong line
whenever the bad value also appeared as some other field's value earlier in the file — an
invalid `"PressType": "d"` on line 193 was reported as line 182, because line 182 happened to
contain both `"PressType"` and a `"Key": "d"`.

### FFB configs

An FFB config is checked against the currently detected devices when loaded. This catches the
mistakes that would otherwise show up only as force feedback silently doing nothing:

- `DeviceName` matching no detected device (the warning lists the names that *are* available,
  so you can copy the exact string).
- An unknown `Axis` name.
- `Notches` enabled with an empty `Positions` list.
- `Positions` outside 0–100, or `Strength` outside 0–10000.
- `SnapZoneWidth` of zero or less — notches would never engage.
- Two `Positions` closer together than DirectInput can resolve (≈0.005 units).
- A section enabled with a `Strength` of 0 — `Notches` that will not be felt, or `Resistance`
  that is treated as disabled. Almost always a leftover from tuning the value down to nothing.

In both cases the config is still loaded — the warnings are informational only.

Separately, when force feedback starts, each configured axis is checked against the raw range
the device reports. The normalization assumes a raw span of 0–65535; if a device reports
something else the status line says so, e.g. `FFB: MyWheel/X reports raw range -32768..32767,
not 0..65535 — notch positions will be misplaced.` This is diagnostic only — nothing is written
to the device and the range is not corrected automatically. It matters most with dense notches,
where a range mismatch stretches and shifts every detent rather than merely rounding it.

---

## Example Configs

### Throttle lever (Z axis) mapped to Numpad +/−

```json
{
  "JoystickName": "BU0836X Interface",
  "Axes": [
    {
      "Axis": "Z",
      "Events": [
        { "Threshold": [10, 20, 30, 40, 50, 60, 70, 80, 90, 99],
          "Direction": "Rising",  "PressType": "1Press", "Key": "NUMPAD+" },

        { "Threshold": [0, 10, 20, 30, 40, 50, 60, 70, 80, 90],
          "Direction": "Falling", "PressType": "1Press", "Key": "NUMPAD-" }
      ]
    }
  ]
}
```

### Steering wheel (X axis) holding left/right arrow keys

```json
{
  "JoystickName": "Logitech Driving Force GT USB",
  "Axes": [
    {
      "Axis": "X",
      "Events": [
        { "Threshold": 60, "Direction": "Right", "PressType": "Hold", "Key": "RIGHT" },
        { "Threshold": 40, "Direction": "Left",  "PressType": "Hold", "Key": "LEFT"  }
      ]
    }
  ]
}
```

### Press with a specific hold time, and a KeyDown / KeyUp pair

```json
{
  "Axis": "Y",
  "Events": [
    { "Threshold": 95, "Direction": "Rising", "PressType": "3PressMS50", "Key": "SPACE" }
  ]
}
```

```json
{
  "Axis": "X",
  "Events": [
    { "Threshold": 60, "Direction": "Rising",  "PressType": "KeyDown", "Key": "A" },
    { "Threshold": 60, "Direction": "Falling", "PressType": "KeyUp",   "Key": "A" }
  ]
}
```

### Hold ranges (key held while axis is within a range)

The key is held for as long as the axis value stays inside the range, and released the moment
it leaves. `Direction` controls which side of the range the axis must enter from before the
hold activates:

| Direction | Entry required |
|---|---|
| `Both` (default) | Either side — activates whenever the value lands inside the range |
| `Rising` / `Right` / `Down` | Must enter from **below** (previous value < range minimum) |
| `Falling` / `Left` / `Up` | Must enter from **above** (previous value > range maximum) |

```json
{
  "Axis": "Z",
  "Events": [
    { "Threshold": "30-70", "Direction": "Falling", "PressType": "Hold", "Key": "W" },
    { "Threshold": "30-70", "Direction": "Rising",  "PressType": "Hold", "Key": "S" },
    { "Threshold": ["55-65", "80-90"], "Direction": "Both", "PressType": "Hold", "Key": "E" },
    { "Threshold": "70.25-72.75",      "Direction": "Both", "PressType": "Hold", "Key": "R" }
  ]
}
```

Range bounds may be decimal, are **inclusive**, and are given in either order — `"72.75-70.25"`
is the same range. Above, `W` is only held when the axis enters the 30–70 range from above
(e.g. a lever pulled back) and `S` only when it enters from below; release works the same way
in both cases — once the value leaves the range the key is released.

Because the bounds are inclusive, a range must not *end* on the position the lever rests at.
A `Falling` range of `"58-67"` on a lever that centres at 67 can never latch: the entry test
asks for a previous value **above** the maximum, and a lever sitting at 67 is already inside
the range. Stop the range short of the detent (`"58-66"`) so the lever has an outside to
enter from.

A plain `NPress` threshold that sits **inside** an active hold range is allowed — the press
is sent as a discrete press and the hold is restored afterwards, so the held key is not left
up. Both go through [the key queue](#the-key-queue) in the order the lever produced them.

---

## Tips

- Use the Axis Monitor to observe live values and pick your thresholds. It reads to two
  decimals, so you can copy a position straight into a decimal threshold.
- For levers/sliders that travel from 0 to 100, space Rising thresholds evenly (e.g. every 10
  units) to get one press per notch of travel.
- Need more steps than 100? Decimal thresholds remove the whole-unit limit —
  `generate_notches.py` emits the notch `"Positions"` and a ready-made pair of Rising/Falling
  `"Threshold"` lists in the same run, so a lever's key steps line up with its FFB detents by
  construction, and one detent of travel always sends exactly one keystroke.
- Pair every `KeyDown` with a matching `KeyUp` on the same threshold so the key is always
  released — or use `Hold`, which handles this automatically.
- Use `NPressMSm` when a game requires a longer key hold to register (e.g. `1PressMS200`).
- If keys work in a browser but not in your game, run ConfJoystick as Administrator
  (right-click → Run as administrator).

### Force feedback

- Copy `DeviceName` straight out of the joystick combo box — it must match exactly (case is
  ignored). A mismatch is reported when the config is loaded.
- Watch the status line after pressing **START EMULATION**: it says how many devices and axes
  force feedback actually claimed.
- FFB needs exclusive access to the device. If a wheel/lever utility from the manufacturer, a
  game, or a second ConfJoystick instance is running, the device is skipped — close the other
  application and press **START EMULATION** again.
- Tune `Strength` and `SnapZoneWidth` while emulation is running: edit the FFB config, then
  **Options → Reload Config**. No restart needed.
- If notches feel inverted or land in the wrong place, check with the Axis Monitor that the
  lever really reads 0–100 over its full travel — `Positions` are on that same scale.
- **Dense notches feel mushy or blurred together.** With tight spacing each detent is only a
  fraction of a percent of travel, so raise `Strength` (up to 10000) to make the catch points
  distinct. If it still feels smeared, the mechanical resolution of the device — not the
  config — is the limit; try fewer notches to confirm.
- **Dense notches buzz or oscillate.** The lever is being fought between two adjacent springs.
  Lower `Strength`, or reduce `Resistance.Strength`, which damps the movement.
- If the status line reports a raw range other than `0..65535` for an axis, every notch on it
  is scaled wrong. See *Config Validation → FFB configs*.
- **Notches are always produced by a DirectInput Spring condition.** An experimental
  software-driven Constant Force mode was tried and removed because it rattled the axis across
  the notched range. The keys it used — `ForceMode`, `RampWidth`, `DeadZone`, `Invert` and
  `Damping` — are no longer recognised, and are **silently ignored** rather than reported,
  because the config reader skips unknown properties. A config left over from those experiments
  will load and run as a plain Spring with no warning that those settings now do nothing.
  Delete them to avoid confusion.
