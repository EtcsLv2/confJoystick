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

        private Joystick? _joystick;
        private bool[]?   _prevButtons;

        private readonly DispatcherTimer _pollTimer;
        private readonly ObservableCollection<MappingItem> _items = new();

        // ─── Constructor ──────────────────────────────────────────────────────

        public ButtonMappingWindow(DirectInput directInput,
                                   List<DeviceInstance> joystickDevices,
                                   List<JoystickConfig> configuration)
        {
            InitializeComponent();

            _directInput     = directInput;
            _joystickDevices = joystickDevices;
            _configuration   = configuration;

            MappingList.ItemsSource = _items;

            foreach (var d in joystickDevices)
                JoystickComboBox.Items.Add(d.InstanceName);

            if (JoystickComboBox.Items.Count > 0)
                JoystickComboBox.SelectedIndex = 0;

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();

            Loaded += (_, _) => Focus();
        }

        // ─── Joystick selection ───────────────────────────────────────────────

        private void JoystickComboBox_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _joystick?.Unacquire();
            _joystick?.Dispose();
            _joystick    = null;
            _prevButtons = null;

            int idx = JoystickComboBox.SelectedIndex;
            if (idx < 0 || idx >= _joystickDevices.Count) return;

            try
            {
                _joystick = new Joystick(_directInput, _joystickDevices[idx].InstanceGuid);
                _joystick.Properties.BufferSize = 128;
                _joystick.Acquire();
            }
            catch (Exception ex)
            {
                _joystick?.Dispose();
                _joystick = null; // must be null so PollTimer_Tick skips gracefully
                StatusText.Text = $"Could not open joystick: {ex.Message}";
                RefreshList();
                return;
            }

            ResetToWaitingForButton();
            RefreshList();
        }

        private JoystickConfig? CurrentConfig()
        {
            if (JoystickComboBox.SelectedItem is not string name) return null;
            return _configuration.FirstOrDefault(c =>
                c.JoystickName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        // ─── Controller button polling ────────────────────────────────────────

        private void PollTimer_Tick(object? sender, EventArgs e)
        {
            if (_joystick == null || _state != MappingState.WaitingForButton) return;

            try
            {
                _joystick.Poll();
                var buttons = _joystick.GetCurrentState().Buttons;
                if (buttons.Length == 0) return; // device reported no buttons

                if (_prevButtons == null)
                {
                    _prevButtons = (bool[])buttons.Clone();
                    return;
                }

                for (int i = 0; i < Math.Min(buttons.Length, _prevButtons.Length); i++)
                {
                    if (buttons[i] && !_prevButtons[i]) // rising edge
                    {
                        _pendingButton = i;
                        _pendingModifiers.Clear();
                        _pendingKey    = null;
                        _state         = MappingState.WaitingForKey;
                        UpdateUI();
                        break;
                    }
                }

                _prevButtons = (bool[])buttons.Clone();
            }
            catch (Exception ex)
            {
                // Poll failed — device may have been unplugged or lost.
                // Show the error so the user knows what happened.
                StatusText.Text = $"Joystick error: {ex.Message}  (try re-selecting)";
                _joystick?.Unacquire();
                _joystick?.Dispose();
                _joystick = null;
            }
        }

        // ─── Keyboard input ───────────────────────────────────────────────────

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            e.Handled = true; // prevent controls from consuming the event

            if (e.Key == Key.Escape) { Close(); return; }

            if (_state == MappingState.WaitingForButton)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            // ── WaitingForKey ──────────────────────────────────────────────

            if (e.Key == Key.Back)
            {
                ResetToWaitingForButton();
                return;
            }

            if (e.Key == Key.Return)
            {
                ConfirmMapping();
                return;
            }

            // Modifier keys accumulate; any other key becomes the main key
            string? mod = ToModifierString(e.Key);
            if (mod != null)
            {
                if (!_pendingModifiers.Contains(mod))
                    _pendingModifiers.Add(mod);
            }
            else
            {
                string? ks = ToKeyString(e.Key);
                if (ks != null) _pendingKey = ks;
            }

            UpdateUI();
            base.OnPreviewKeyDown(e);
        }

        // ─── Confirm / reset ──────────────────────────────────────────────────

        private void ConfirmMapping()
        {
            if (_pendingButton < 0) return;

            var config = CurrentConfig();
            if (config == null) return;

            // Remove any existing mapping for this button
            config.Buttons.RemoveAll(b => b.Button == _pendingButton);

            // Add new mapping (if anything was set)
            if (!string.IsNullOrEmpty(_pendingKey) || _pendingModifiers.Count > 0)
            {
                config.Buttons.Add(new ButtonConfig
                {
                    Button    = _pendingButton,
                    Key       = _pendingKey ?? "",
                    Modifiers = new List<string>(_pendingModifiers)
                });
            }

            RefreshList();
            ResetToWaitingForButton();
        }

        private void ResetToWaitingForButton()
        {
            _state         = MappingState.WaitingForButton;
            _pendingButton = -1;
            _pendingModifiers.Clear();
            _pendingKey    = null;
            _prevButtons   = null; // fresh baseline so button release is not detected
            UpdateUI();
        }

        // ─── UI helpers ───────────────────────────────────────────────────────

        private void UpdateUI()
        {
            if (_state == MappingState.WaitingForButton)
            {
                StatusText.Text    = "Press a controller button...";
                AssignmentText.Text = "";
                return;
            }

            StatusText.Text = $"Button {_pendingButton + 1} captured.  " +
                              "Press keyboard key (Ctrl/Shift/Alt for combos).";

            string label = BuildLabel(_pendingModifiers, _pendingKey);
            AssignmentText.Text = label.Length > 0
                ? $"Assignment:  Button {_pendingButton + 1}  →  {label}"
                : $"Assignment:  Button {_pendingButton + 1}  →  (press Enter to remove mapping)";
        }

        private void RefreshList()
        {
            _items.Clear();
            var config = CurrentConfig();
            if (config == null) return;

            foreach (var b in config.Buttons.OrderBy(b => b.Button))
                _items.Add(new MappingItem
                {
                    ButtonLabel = $"Button {b.Button + 1}",
                    KeyLabel    = BuildLabel(b.Modifiers, b.Key)
                });
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
            if (key >= Key.F1     && key <= Key.F12)    return $"F{(int)(key - Key.F1) + 1}";

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
                Key.Multiply => "NUMPAD*",
                Key.Add      => "NUMPAD+",
                Key.Subtract => "NUMPAD-",
                Key.Divide   => "NUMPAD/",
                Key.Decimal  => "NUMPAD.",
                _            => null
            };
        }

        // ─── Cleanup ──────────────────────────────────────────────────────────

        protected override void OnClosed(EventArgs e)
        {
            _pollTimer.Stop();
            _joystick?.Unacquire();
            _joystick?.Dispose();
            base.OnClosed(e);
        }
    }

    // ─── ListView row model ────────────────────────────────────────────────────

    public class MappingItem
    {
        public string ButtonLabel { get; set; } = "";
        public string KeyLabel    { get; set; } = "";
    }
}
