

Confjoystick is an Windowsapp to to emulate keypresses controlled by a Joystick. Similar to eg. Joytokey. Why a new app?  I tried many of these Apps but I could not configer all I wanted. So I let AI (ClaudeCode) create a configurable Joystick, shor just confJoystick. I createed it to use in Simrail or TD2 and it is not tested with other  Games.
<img width="470" height="436" alt="Conjoystick_image" src="https://github.com/user-attachments/assets/2f439251-1ac8-4da3-a865-aa65110867fd" />


# ConfJoystick — Documentation

---

## Overview

ConfJoystick maps physical joystick/wheel axis movements and button presses to keyboard
key events so you can play games that have no controller support. It reads axes via
SharpDX DirectInput and sends hardware-level scan codes via the Windows SendInput API,
making the emulated keys indistinguishable from a real keyboard in both browsers and games.

Key presses are sent as hardware scan codes (`KEYEVENTF_SCANCODE`), which means they work
with DirectInput-based and Raw-Input-based games. Running ConfJoystick as Administrator
may be required for games with anti-cheat or elevated privileges.

---

## Program Features

### 1. Joystick Detection
- Detects all connected DirectInput game controllers on startup.
- Select the active controller from the Joystick combo box.
- Click **Refresh** to re-scan after plugging in a device.

### 2. Axis Monitor
- Select any axis from the Axis combo box.
- The live value (0–100) updates in real time so you can verify the axis is moving
  and identify its range.

### 3. Emulation Toggle
- The large **START / STOP EMULATION** button enables or disables all key emulation
  without closing the program.
- When stopped, any keys that were held down are released automatically.
- Emulation processes **all** configured joysticks simultaneously, not just the one
  selected in the combo box.

### 4. Verification Field
- A text box you can click into to give it focus.
- While focused, any emulated key presses will type into it, letting you verify that
  the correct characters are produced.
- A character counter next to the field shows the total number of characters currently
  in the box.
- The **Clear** button empties the field and resets the counter to 0.

### 5. Configure Keys (Button Mapping)
- Accessible via **Options → Configure Keys…**.
- Opens a dedicated window for mapping physical controller buttons to keys.
- Fully keyboard-driven — no mouse needed after the window opens.
- **Step 1:** Press a button on the controller.
- **Step 2:** Optionally hold Ctrl / Shift / Alt to add modifiers.
- **Step 3:** Press the desired keyboard key (preview shown live).
- `Enter` = confirm and save the mapping.
- `Backspace` = discard the current assignment and go back to step 1.
- `Enter` with no key set = remove the existing mapping for that button.
- `Esc` = close the window.
- Mappings are held down while the button is held and released when the button is
  released, exactly like a keyboard key.

### 6. Settings (settings.json)
Stored automatically in the config directory alongside lever/button configs.

| Setting | Default | Description |
|---|---|---|
| `MaxKeypressIntervalMs` | `60` | Minimum gap between consecutive queued presses (ms). |
| `DefaultPressMs` | `30` | Hold duration for press types that don't specify one. Override per-binding with `NPressMSm`. |
| `MaxConcurrentKeys` | `0` | Maximum simultaneous held keys. `0` = unlimited. |

### 7. Import / Export Config
- **Export Config** writes a JSON file with every detected joystick and all 8 standard
  axes. Axes with no events have an empty Events array.
- **Import Config** loads a JSON file and merges it with the currently detected devices
  (newly detected axes are added automatically).
- Edit the JSON file in any text editor to set up key bindings.
- Trailing commas and `//` line comments are allowed in the JSON.

---

## Directories

| Location | File | Purpose |
|---|---|---|
| Exe directory | `config_dir.txt` | Stores the path to the user-chosen config directory. |
| Config directory | `settings.json` | App settings (auto-loaded configs, key limits, press interval). |
| Config directory | `*_levers.json` | Lever (axis) configuration files. |
| Config directory | `*_buttons.json` | Button configuration files. |

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

All axis values are normalised to **0–100** (0 = minimum, 100 = maximum).
DirectInput raw range (0–65535) is mapped automatically.

---

## Directions

| Value | Description |
|---|---|
| `Rising` | Fires when the axis value crosses the threshold going upward. |
| `Falling` | Fires when the axis value crosses the threshold going downward. |
| `Both` | Fires on both Rising and Falling crossings. |
| `Right` | Alias for Rising (for horizontal axes). |
| `Down` | Alias for Rising (for vertical axes). |
| `Left` | Alias for Falling (for horizontal axes). |
| `Up` | Alias for Falling (for vertical axes). |

---

## Press Types

| Value | Description |
|---|---|
| `KeyDown` | Sends a key-down event. The key stays held until a matching `KeyUp` fires (or emulation is stopped). |
| `KeyUp` | Sends a key-up event. Use paired with `KeyDown`. |
| `Hold` | Holds the key down while the axis is past the threshold; releases automatically when the axis moves back. |
| `1Press` / `NPress` | Sends N complete key presses (down + up). Hold duration uses `DefaultPressMs`. Any positive integer is accepted (e.g. `5Press`, `10Press`). |
| `NPressMSm` | Sends N key presses, each held for exactly **m** milliseconds. Overrides `DefaultPressMs` for this binding only. e.g. `3PressMS50` = 3 presses held 50 ms each. |

**Legacy aliases (still accepted):**
- `SinglePress` = `1Press`
- `DoublePress` = `2Press`

---

## Threshold

A single value (0–100) or an array of values. Each value in the array acts as an
independent crossing point for the same event.

```json
"Threshold": 75
"Threshold": [25, 50, 75]
```

With the array form, each crossing fires the event independently.
For Hold events, each threshold tracks its hold state separately.

---

## Supported Keys

| Category | Keys |
|---|---|
| Letters | `A` – `Z` |
| Digits | `0` – `9` |
| Function | `F1` – `F12` |
| Navigation | `SPACE`, `ENTER` / `RETURN`, `TAB`, `ESCAPE` / `ESC`, `BACKSPACE`, `DELETE` / `DEL`, `INSERT` / `INS`, `HOME`, `END`, `PAGEUP` / `PGUP`, `PAGEDOWN` / `PGDN`, `UP`, `DOWN`, `LEFT`, `RIGHT` |
| Modifiers | `SHIFT`, `LSHIFT`, `RSHIFT`, `CTRL` / `CONTROL`, `LCTRL`, `RCTRL`, `ALT`, `LALT`, `RALT` |
| Numpad | `NUMPAD0`–`NUMPAD9` / `NUM0`–`NUM9`, `NUMPAD+` / `NUMADD` / `NUM+`, `NUMPAD-` / `NUMSUBTRACT` / `NUM-`, `NUMPAD*` / `NUMMULTIPLY` / `NUM*`, `NUMPAD/` / `NUMDIVIDE` / `NUM/`, `NUMPAD.` / `NUMDECIMAL` / `NUM.` |

Key names are **case-insensitive** in the JSON.

---

## Config File Format

### Lever Config (`*_levers.json`)

```json
{
  "AssociatedButtonConfigFile": "my_buttons.json",
  "Joysticks": [
    {
      "JoystickName": "<exact device name as shown in the combo box>",
      "Axes": [
        {
          "Axis": "<axis name>",
          "Events": [
            {
              "Threshold": "<0-100 or [array]>",
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

---

## Config Validation

When a lever config is loaded, ConfJoystick checks every event for invalid argument
values. If any are found, a warning popup lists each problem with its line number and
the correct syntax, e.g.:

```
Line 14: Invalid PressType "1Press2000"
  Valid values: Hold, KeyDown, KeyUp, SinglePress, DoublePress,
               nPress (e.g. 3Press), nPressMSm (e.g. 3PressMS50)

Line 22: Invalid Direction "Upward"
  Valid values: Rising, Falling, Both, Right, Down, Left, Up
```

The config is still loaded — the warning is informational only.

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

### Button-style press with specific hold time

```json
{
  "Axis": "Y",
  "Events": [
    { "Threshold": 95, "Direction": "Rising", "PressType": "3PressMS50", "Key": "SPACE" }
  ]
}
```

### KeyDown / KeyUp pair to hold a key between two thresholds

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

The key is held for as long as the axis value stays inside the range, and released the moment it leaves.

`Direction` controls which side of the range the axis must enter from before the hold activates:

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
    { "Threshold": ["55-65", "80-90"], "Direction": "Both", "PressType": "Hold", "Key": "E" }
  ]
}
```

In the example above `W` is only held when the axis enters the 30–70 range from above (e.g. a lever pulled back), and `S` is only held when it enters from below (lever pushed forward).
Release happens the same way in both cases — once the value leaves the range the key is released.

---

## Tips

- Use the Axis Monitor to observe live values and pick your thresholds.
- For levers/sliders that travel from 0 to 100, space Rising thresholds evenly
  (e.g. every 10 units) to get one press per notch of travel.
- Pair every `KeyDown` with a matching `KeyUp` on the same threshold so the key is
  always released. Alternatively use `Hold` which handles this automatically.
- Use `NPressMSm` when a game requires a longer key hold to register (e.g. `1PressMS200`).
- If keys work in a browser but not in your game, run ConfJoystick as Administrator
  (right-click → Run as administrator).
