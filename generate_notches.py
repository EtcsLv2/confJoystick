"""Generate evenly spaced FFB notch positions, and the lever switch points to match.

Run it, then enter three or four comma-separated values:

    Start, End, Notches, Trip%: 0, 50, 44
    Start, End, Notches, Trip%: 0, 50, 44, 65

Both ends are notches, so N notches leave N-1 intervals. Two blocks are printed: the
bracketed "Positions" list for the FFB config, and a matching pair of Rising/Falling
lever events for the key emulation config.

Trip% places the switch points inside each gap between adjacent notches. The FFB spring
changes which notch it pulls towards at the halfway line, so a Rising point above 50%
only fires once the lever is committed to the next notch and cannot spring back and
retrigger. Falling sits mirrored at 100-Trip%, leaving a dead band in between that
absorbs the play in the lever.

    notch i                                             notch i+1
    |---------------|---------------|---------------|---------------|
    0%             35%             50%             65%            100%
                 Falling      spring tips        Rising
"""

import subprocess
import sys

PER_LINE = 8

# How far into each gap the Rising switch point sits, as a percentage. Must stay above 50
# so the spring has already committed to the next notch by the time the key fires.
TRIP_PERCENT = 65.0

# Placeholders for the generated lever events - edit to suit the config they land in.
RISING_KEY = "NUMPAD+"
FALLING_KEY = "NUMPAD-"
PRESS_TYPE = "1Press"


def fmt(value):
    """Trim a position to 6 decimals and drop trailing zeros (25.0 -> 25)."""
    text = f"{round(value, 6):.6f}".rstrip("0").rstrip(".")
    return "0" if text in ("", "-", "-0") else text


def generate(start, end, count):
    if count == 1:
        return [start]
    step = (end - start) / (count - 1)
    return [start + i * step for i in range(count)]


def switch_points(start, step, count, trip):
    """Rising and falling switch points, one of each per gap between adjacent notches.

    Rising sits `trip` of the way up a gap, Falling the same distance down from its top,
    so both lie past the halfway line where the spring changes which notch it pulls to.
    """
    rising = [start + (i + trip) * step for i in range(count - 1)]
    falling = [start + (i + 1 - trip) * step for i in range(count - 1)]
    return rising, falling


def as_list(values, indent="  ", trailing=""):
    items = [fmt(v) for v in values]
    if len(items) <= PER_LINE:
        return "[ " + ", ".join(items) + " ]"

    rows = [", ".join(items[i:i + PER_LINE]) for i in range(0, len(items), PER_LINE)]
    return "[\n" + indent + (",\n" + indent).join(rows) + "\n" + indent[:-2] + "]" + trailing


def as_events(rising, falling):
    """The two lever events, ready to paste into an axis "Events" list."""
    return (
        f'{{ "Threshold": {as_list(rising, "    ")},\n'
        f'  "Direction": "Rising",  "PressType": "{PRESS_TYPE}", "Key": "{RISING_KEY}" }},\n'
        f'\n'
        f'{{ "Threshold": {as_list(falling, "    ")},\n'
        f'  "Direction": "Falling", "PressType": "{PRESS_TYPE}", "Key": "{FALLING_KEY}" }}'
    )


def copy_to_clipboard(text):
    """Windows clip.exe first; fall back to tkinter if it is unavailable."""
    try:
        subprocess.run(["clip"], input=text.encode("utf-8"), check=True)
        return True
    except Exception:
        pass

    try:
        import tkinter

        root = tkinter.Tk()
        root.withdraw()
        root.clipboard_clear()
        root.clipboard_append(text)
        root.update()
        root.destroy()
        return True
    except Exception:
        return False


def parse(raw):
    # Strip a BOM and any surrounding brackets, so a list pasted back in still parses.
    raw = raw.lstrip("﻿").strip().strip("[]")
    parts = [p.strip() for p in raw.split(",")]
    if len(parts) not in (3, 4):
        raise ValueError(f"expected 3 or 4 values separated by commas, got {len(parts)}")

    start, end = float(parts[0]), float(parts[1])
    count = int(parts[2])
    trip = float(parts[3]) if len(parts) == 4 and parts[3] else TRIP_PERCENT

    for name, value in (("Start", start), ("End", end)):
        if not 0 <= value <= 100:
            raise ValueError(f"{name} must be within the 0-100 axis scale, got {value}")
    if count < 1:
        raise ValueError(f"Notches must be at least 1, got {count}")
    if start == end and count > 1:
        raise ValueError("Start and End are the same, so the notches would all overlap")
    if not 50 < trip < 100:
        raise ValueError(
            f"Trip% must be above 50 and below 100, got {fmt(trip)} - at or below 50 the key "
            f"fires before the spring commits to the next notch, so the lever can fall back "
            f"and trigger it again"
        )

    if start > end:
        start, end = end, start
    return start, end, count, trip / 100


def report(start, end, count, trip, step, rising, falling):
    """Print the spacing summary for the notches and the switch points between them."""
    print(f"{count} notches from {fmt(start)} to {fmt(end)} - spacing {fmt(step)}")
    # One 0-100 unit is 200 DirectInput offset units; positions closer than about
    # 0.005 collapse onto the same spring centre and act as a single notch.
    print(f"  ~{round(step * 200)} DirectInput offset units, "
          f"~{round(step * 655.35)} raw axis counts apart")
    print(f"  set SnapZoneWidth above {fmt(step / 2)} for continuous pull to the "
          f"nearest notch, below it for free travel between notches")
    if step < 0.005:
        print("  WARNING: closer than DirectInput can resolve - these will act as one notch")

    print()
    trip_pct = trip * 100
    label = "switch point" if len(rising) == 1 else "switch points"
    print(f"{len(rising)} Rising {label} at {fmt(trip_pct)}% of each gap, "
          f"{len(falling)} Falling at {fmt(100 - trip_pct)}%")

    # Between Falling and Rising in the same gap the lever toggles nothing, so this is the
    # play it can absorb; the margin is how far the trip sits past the spring's tipping point.
    band = (2 * trip - 1) * step
    margin = (trip - 0.5) * step
    print(f"  dead band {fmt(band)} wide between the two ({round(band * 655.35)} raw counts) "
          f"- lever play smaller than this cannot chatter the key")
    print(f"  {fmt(margin)} of margin past the halfway line where the spring takes over")

    # Rising[i] to Falling[i+1] is the other, usually tighter, threshold gap.
    closest = min(band, 2 * (1 - trip) * step)
    if closest < 0.005:
        print(f"  WARNING: switch points only {fmt(closest)} apart - below what the axis "
              f"resolves, so they will fire together")
    elif trip < 0.55:
        print("  WARNING: Trip% is close to 50 - little room for lever play before the key "
              "fires early")


def main():
    print("ConfJoystick notch generator")
    print("Enter:  Start, End, Notches[, Trip%]   (e.g.  0, 50, 44  or  0, 50, 44, 75)")
    print(f"Trip% defaults to {fmt(TRIP_PERCENT)}.")
    print()

    try:
        raw = input("Start, End, Notches, Trip%: ")
    except (EOFError, KeyboardInterrupt):
        return 1, None

    try:
        start, end, count, trip = parse(raw)
    except ValueError as exc:
        print(f"\nInvalid input: {exc}")
        return 1, None

    values = generate(start, end, count)
    positions = as_list(values, trailing=",")

    print()
    print('FFB config - "Positions":')
    print(positions)

    events = None
    if count > 1:
        step = (end - start) / (count - 1)
        rising, falling = switch_points(start, step, count, trip)
        events = as_events(rising, falling)

        print()
        print('Lever config - "Events":')
        print(events)
        print()
        report(start, end, count, trip, step, rising, falling)
    else:
        print()
        print("1 notch - no gaps, so there are no switch points to generate")

    print()
    print("Positions copied to clipboard." if copy_to_clipboard(positions)
          else "Could not access the clipboard - copy the list above manually.")
    return 0, events


if __name__ == "__main__":
    code = 0
    while True:
        code, events = main()
        while True:
            try:
                choice = input("\nPress Enter to run again, C to copy the lever "
                               "events, or Q to quit: ").strip().lower()
            except (EOFError, KeyboardInterrupt):
                choice = "q"
            if choice in ("c", "copy"):
                if events is None:
                    print("Nothing to copy - the last run produced no switch points.")
                else:
                    print("Lever events copied." if copy_to_clipboard(events)
                          else "Could not access the clipboard - copy the block above manually.")
                continue
            break
        if choice in ("q", "quit", "exit"):
            break
        print()
    sys.exit(code)
