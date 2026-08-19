using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using SharpDX.DirectInput;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ConfJoystick
{
    public partial class ButtonMappingWindow : Window
    {
        // ─── State machine ────────────────────────────────────────────────────

        private enum MappingState { WaitingForButton, WaitingForKey }

        private MappingState _state = MappingState.WaitingForButton;
        private int          _pendingButton = -1;
        private readonly List<string> _pendingModifiers = new();
        private string?      _pendingKey;

        // ─── Infrastructure ───────────────────────────────────────────────────

        private readonly DirectInput          _directInput;
        private readonly List<DeviceInstance> _joystickDevices;
        private readonly List<JoystickConfig> _configuration;

        // Index-aligned with _joystickDevices: every device is held open at once so a
        // button press on *any* stick can select that stick.
        private readonly Joystick?[]    _joysticks;
        private readonly List<bool[]?>  _prevButtons = new();

        private readonly DispatcherTimer _pollTimer;
        private readonly ObservableCollection<MappingItem> _items = new();

        // ─── Constructor ──────────────────────────────────────────────────────

        public ButtonMappingWindow(DirectInput directInput,
                                   List<DeviceInstance> joystickDevices,
                                   List<JoystickConfig> configuration,
                                   string? preselectJoystickName = null)
        {
            InitializeComponent();

            _directInput     = directInput;
            _joystickDevices = joystickDevices;
            _configuration   = configuration;

            _joysticks = new Joystick?[joystickDevices.Count];
            for (int i = 0; i < joystickDevices.Count; i++) _prevButtons.Add(null);

            MappingList.ItemsSource = _items;

            OpenAllJoysticks();

            foreach (var d in joystickDevices)
                JoystickComboBox.Items.Add(d.InstanceName);

            if (JoystickComboBox.Items.Count > 0)
            {
                // Start on the stick the main window is showing, not blindly on the first one.
                int idx = string.IsNullOrEmpty(preselectJoystickName) ? -1
                    : _joystickDevices.FindIndex(d =>
                        d.InstanceName.Equals(preselectJoystickName, StringComparison.OrdinalIgnoreCase));

                JoystickComboBox.SelectedIndex = idx >= 0 ? idx : 0;
            }

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();

            Loaded += (_, _) => MappingList.Focus();
        }

        /// <summary>
        /// Opens every detected joystick. The call order (construct → BufferSize → Acquire) is the
        /// one the rest of the app uses and must not be rearranged.
        /// </summary>
        private void OpenAllJoysticks()
        {
            for (int i = 0; i < _joystickDevices.Count; i++)
            {
                try
                {
                    var js = new Joystick(_directInput, _joystickDevices[i].InstanceGuid);
                    js.Properties.BufferSize = 128;
                    // A device that will not acquire yet is kept — PollTimer_Tick retries it.
                    try { js.Acquire(); } catch { }
                    _joysticks[i] = js;
                }
                catch
                {
                    // Leave the slot null — PollTimer_Tick skips it, the other devices still work.
                    _joysticks[i] = null;
                }
            }
        }

        // ─── Joystick selection ───────────────────────────────────────────────

        private void JoystickComboBox_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Devices stay open for the lifetime of the window; the selection only decides
            // which configuration is displayed and edited.
            ResetToWaitingForButton();
            RefreshList();
        }

        /// <summary>
        /// Configuration of the selected joystick, created on demand so mapping a device that the
        /// main window has not seen yet is never a silent no-op.
        /// </summary>
        private JoystickConfig? CurrentConfig()
        {
            if (JoystickComboBox.SelectedItem is not string name) return null;

            var config = _configuration.FirstOrDefault(c =>
                c.JoystickName.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (config == null)
            {
                config = new JoystickConfig { JoystickName = name };
                _configuration.Add(config);
            }

            return config;
        }

        // ─── Controller button polling ────────────────────────────────────────

        private void PollTimer_Tick(object? sender, EventArgs e)
        {
            for (int d = 0; d < _joysticks.Length; d++)
            {
                var js = _joysticks[d];
                if (js == null) continue;

                bool[] buttons;
                try
                {
                    js.Poll();
                    buttons = js.GetCurrentState().Buttons;
                }
                catch (Exception ex)
                {
                    // Lost the device — focus change, unplugged, or another exclusive owner.
                    // Keep the handle and try to get it back on the next tick.
                    _prevButtons[d] = null;
                    try { js.Acquire(); } catch { }

                    // Only report the device the user is looking at, so a stale handle on some
                    // other stick cannot overwrite the prompt. Same text every tick, no flicker.
                    if (d == JoystickComboBox.SelectedIndex && _state == MappingState.WaitingForButton)
                        StatusText.Text = $"{_joystickDevices[d].InstanceName}: {ex.Message}";

                    continue;
                }

                if (buttons.Length == 0) continue; // device reported no buttons

                var prev = _prevButtons[d];
                _prevButtons[d] = (bool[])buttons.Clone();
                if (prev == null) continue; // first frame is only a baseline

                for (int i = 0; i < Math.Min(buttons.Length, prev.Length); i++)
                {
                    if (buttons[i] && !prev[i]) // rising edge
                    {
                        // Pressing a button selects its joystick, in any state.
                        if (JoystickComboBox.SelectedIndex != d)
                            JoystickComboBox.SelectedIndex = d;

                        BeginCapture(i);
                        return;
                    }
                }
            }
        }

        // ─── Capture / confirm / reset ────────────────────────────────────────

        /// <summary>Starts editing a button, pre-filled with whatever it is currently mapped to.</summary>
        private void BeginCapture(int button)
        {
            _pendingButton = button;

            var existing = CurrentConfig()?.Buttons.FirstOrDefault(b => b.Button == button);
            _pendingKey = string.IsNullOrEmpty(existing?.Key) ? null : existing!.Key;
            _pendingModifiers.Clear();
            if (existing?.Modifiers != null)
                _pendingModifiers.AddRange(existing.Modifiers);

            _state = MappingState.WaitingForKey;

            SelectRow(button);
            UpdateUI();
        }

        private void BeginCaptureFromSelection()
        {
            if (MappingList.SelectedItem is MappingItem item)
                BeginCapture(item.ButtonIndex);
        }

        private void ConfirmMapping()
        {
            if (_pendingButton < 0) return;

            var config = CurrentConfig();
            if (config == null) return;

            int button = _pendingButton;

            // Remove any existing mapping for this button
            config.Buttons.RemoveAll(b => b.Button == button);

            // Add new mapping (if anything was set)
            if (!string.IsNullOrEmpty(_pendingKey) || _pendingModifiers.Count > 0)
            {
                config.Buttons.Add(new ButtonConfig
                {
                    Button    = button,
                    Key       = _pendingKey ?? "",
                    Modifiers = new List<string>(_pendingModifiers)
                });
            }

            ResetToWaitingForButton();
            RefreshList();
            SelectRow(button);
        }

        private void DeleteSelectedMapping()
        {
            if (MappingList.SelectedItem is not MappingItem item) return;

            var config = CurrentConfig();
            if (config == null) return;

            config.Buttons.RemoveAll(b => b.Button == item.ButtonIndex);

            int idx = MappingList.SelectedIndex;
            RefreshList();
            if (_items.Count > 0)
                MappingList.SelectedIndex = Math.Min(idx, _items.Count - 1);

            StatusText.Text = $"Removed the mapping for Button {item.ButtonIndex + 1}.";
        }

        private void ResetToWaitingForButton()
        {
            _state         = MappingState.WaitingForButton;
            _pendingButton = -1;
            _pendingModifiers.Clear();
            _pendingKey    = null;
            UpdateUI();
        }

        // ─── Keyboard input ───────────────────────────────────────────────────

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            // Alt and Alt-combos arrive as Key.System with the real key in SystemKey.
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            // While the drop-down has focus let it handle its own keys, so the joystick can also
            // be picked from the keyboard. Escape still closes the window unless the list is open.
            if (_state == MappingState.WaitingForButton &&
                JoystickComboBox.IsKeyboardFocusWithin &&
                !(key == Key.Escape && !JoystickComboBox.IsDropDownOpen))
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            if (_state == MappingState.WaitingForKey)
            {
                e.Handled = true; // every key belongs to the assignment being edited
                HandleAssignmentKey(key);
                return;
            }

            // ── WaitingForButton: navigate and act on the mapping list ─────────
            switch (key)
            {
                case Key.Escape:
                    e.Handled = true;
                    Close();
                    return;

                case Key.Return:
                    e.Handled = true;
                    BeginCaptureFromSelection();
                    return;

                case Key.Back:
                    e.Handled = true;
                    DeleteSelectedMapping();
                    return;

                case Key.Up:
                case Key.Down:
                case Key.Home:
                case Key.End:
                case Key.PageUp:
                case Key.PageDown:
                    e.Handled = true;
                    MoveSelection(key);
                    return;
            }

            base.OnPreviewKeyDown(e);
        }

        private void HandleAssignmentKey(Key key)
        {
            if (key == Key.Back)
            {
                // Clear the assignment; Enter then writes the empty result, i.e. removes it.
                _pendingKey = null;
                _pendingModifiers.Clear();
                UpdateUI();
                return;
            }

            if (key == Key.Return)  { ConfirmMapping();          return; }
            if (key == Key.Escape)  { ResetToWaitingForButton(); return; }

            // Modifier keys accumulate; any other key becomes the main key
            string? mod = ToModifierString(key);
            if (mod != null)
            {
                if (!_pendingModifiers.Contains(mod))
                    _pendingModifiers.Add(mod);
                UpdateUI();
                return;
            }

            string? ks = ToKeyString(key);
            if (ks == null)
            {
                StatusText.Text = $"\"{key}\" cannot be assigned — press a different key.";
                return;
            }

            _pendingKey = ks;
            UpdateUI();
        }

        private void MoveSelection(Key key)
        {
            if (_items.Count == 0) return;

            int idx = MappingList.SelectedIndex;
            if (idx < 0) idx = key == Key.Up || key == Key.End || key == Key.PageUp ? _items.Count : -1;

            int next = key switch
            {
                Key.Up       => idx - 1,
                Key.Down     => idx + 1,
                Key.Home     => 0,
                Key.End      => _items.Count - 1,
                Key.PageUp   => idx - 10,
                Key.PageDown => idx + 10,
                _            => idx
            };

            MappingList.SelectedIndex = Math.Clamp(next, 0, _items.Count - 1);
            MappingList.ScrollIntoView(MappingList.SelectedItem);
        }

        // ─── Mouse input ──────────────────────────────────────────────────────

        private void MappingList_MouseDoubleClick(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            BeginCaptureFromSelection();
        }

        // ─── UI helpers ───────────────────────────────────────────────────────

        private void UpdateUI()
        {
            if (_state == MappingState.WaitingForButton)
            {
                StatusText.Text     = "Press a controller button on any joystick, " +
                                      "or pick a mapping with ↑↓ and press Enter.";
                AssignmentText.Text = "";
                LegendText.Text     = "↑↓ Select    Enter Edit    Backspace Delete    Esc Close";
                return;
            }

            StatusText.Text = $"Button {_pendingButton + 1} captured.  " +
                              "Press keyboard key (Ctrl/Shift/Alt for combos).";

            string label = BuildLabel(_pendingModifiers, _pendingKey);
            AssignmentText.Text = label.Length > 0
                ? $"Assignment:  Button {_pendingButton + 1}  →  {label}"
                : $"Assignment:  Button {_pendingButton + 1}  →  (empty — Enter removes the mapping)";

            LegendText.Text = "Enter Save    Backspace Clear    Esc Cancel";
        }

        private void RefreshList()
        {
            int selectedButton = MappingList.SelectedItem is MappingItem sel ? sel.ButtonIndex : -1;

            _items.Clear();

            var config = CurrentConfig();
            if (config == null) return;

            foreach (var b in config.Buttons.OrderBy(b => b.Button))
                _items.Add(new MappingItem
                {
                    ButtonIndex = b.Button,
                    ButtonLabel = $"Button {b.Button + 1}",
                    KeyLabel    = BuildLabel(b.Modifiers, b.Key)
                });

            if (selectedButton >= 0) SelectRow(selectedButton);
        }

        /// <summary>Highlights the row of a button, if it has a mapping.</summary>
        private void SelectRow(int button)
        {
            var row = _items.FirstOrDefault(i => i.ButtonIndex == button);
            if (row == null) return;

            MappingList.SelectedItem = row;
            MappingList.ScrollIntoView(row);
        }

        // ─── Static helpers ───────────────────────────────────────────────────

        private static string BuildLabel(IEnumerable<string>? mods, string? key)
        {
            var parts = new List<string>(mods ?? Enumerable.Empty<string>());
            if (!string.IsNullOrEmpty(key)) parts.Add(key);
            return string.Join(" + ", parts);
        }

        private static string? ToModifierString(Key key) => key switch
        {
            Key.LeftCtrl  or Key.RightCtrl  => "CTRL",
            Key.LeftShift or Key.RightShift => "SHIFT",
            Key.LeftAlt   or Key.RightAlt   => "ALT",
            _                               => null
        };

        private static string? ToKeyString(Key key)
        {
            if (key >= Key.A      && key <= Key.Z)      return key.ToString();
            if (key >= Key.D0     && key <= Key.D9)     return ((int)(key - Key.D0)).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return $"NUMPAD{(int)(key - Key.NumPad0)}";
            if (key >= Key.F1     && key <= Key.F24)    return $"F{(int)(key - Key.F1) + 1}";

            return key switch
            {
                Key.Space    => "SPACE",
                Key.Tab      => "TAB",
                Key.Delete   => "DELETE",
                Key.Insert   => "INSERT",
                Key.Home     => "HOME",
                Key.End      => "END",
                Key.PageUp   => "PAGEUP",
                Key.PageDown => "PAGEDOWN",
                Key.Up       => "UP",
                Key.Down     => "DOWN",
                Key.Left     => "LEFT",
                Key.Right    => "RIGHT",
                // Lock, system and Windows keys. ParseKey accepts all of these names.
                Key.CapsLock    => "CAPSLOCK",
                Key.NumLock     => "NUMLOCK",
                Key.Scroll      => "SCROLLLOCK",
                Key.PrintScreen => "PRINTSCREEN",
                Key.Pause       => "PAUSE",
                Key.Apps        => "APPS",
                Key.LWin        => "LWIN",
                Key.RWin        => "RWIN",
                Key.Clear       => "CLEAR",
                Key.Help        => "HELP",

                Key.Multiply => "NUMPAD*",
                Key.Add      => "NUMPAD+",
                Key.Subtract => "NUMPAD-",
                Key.Divide   => "NUMPAD/",
                Key.Decimal  => "NUMPAD.",

                // Punctuation / OEM keys. Names are the US-layout symbol of the physical key;
                // ParseKey accepts the same names, and SendKey emits the scan code, so a mapping
                // always produces the character that key prints on the layout it was recorded on.
                Key.OemPeriod        => ".",
                Key.OemComma         => ",",
                Key.OemMinus         => "-",
                Key.OemPlus          => "=",
                Key.OemSemicolon     => ";",
                Key.OemQuestion      => "/",
                Key.OemTilde         => "`",
                Key.OemOpenBrackets  => "[",
                Key.OemCloseBrackets => "]",
                Key.OemPipe          => "\\",
                Key.OemQuotes        => "'",
                // Written as the key's Windows name, not as "<": the ISO extra key prints a
                // different character on every layout, and "<" now means the character itself.
                Key.OemBackslash     => "OEM102",

                _            => null
            };
        }

        // ─── Cleanup ──────────────────────────────────────────────────────────

        protected override void OnClosed(EventArgs e)
        {
            _pollTimer.Stop();

            for (int i = 0; i < _joysticks.Length; i++)
            {
                try
                {
                    _joysticks[i]?.Unacquire();
                    _joysticks[i]?.Dispose();
                }
                catch { }
                _joysticks[i] = null;
            }

            base.OnClosed(e);
        }
    }

    // ─── ListView row model ────────────────────────────────────────────────────

    public class MappingItem
    {
        /// <summary>0-based button index, as stored in <see cref="ButtonConfig.Button"/>.</summary>
        public int    ButtonIndex { get; set; }
        public string ButtonLabel { get; set; } = "";
        public string KeyLabel    { get; set; } = "";
    }
}
