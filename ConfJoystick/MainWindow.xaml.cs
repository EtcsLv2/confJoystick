using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SharpDX.DirectInput;

namespace ConfJoystick
{
    public partial class MainWindow : Window
    {
        // DirectInput
        private DirectInput _directInput;

        // Joystick for the UI selection (axis monitor)
        private Joystick _selectedJoystick;
        private DeviceInstance _selectedDevice;
        private List<DeviceInstance> _joystickDevices = new();

        // Axis monitor
        private CancellationTokenSource _monitorCts;
        private string _selectedAxis = "X";

        // Emulation
        private bool _isEmulationActive = false;
        private CancellationTokenSource _emulationCts;

        // Configuration (all detected joysticks)
        private List<JoystickConfig> _configuration = new();

        // Runtime state for the emulation loop
        private readonly Dictionary<VirtualKey, int> _heldKeyRefs = new();
        private readonly object _heldKeysLock = new();
        private Dictionary<string, Dictionary<string, int>> _previousAxisValues = new();
        private Dictionary<string, Dictionary<string, HashSet<ThresholdKey>>> _activeThresholds = new();

        // Rate-limited press queue  (key, holdMs)
        private readonly ConcurrentQueue<(VirtualKey key, int holdMs)> _pressQueue = new();

        // Button state tracking for the emulation loop
        private Dictionary<string, bool[]?> _previousButtonStates = new();

        // Tracks which hold-range event indices are currently holding their key
        // joystick name → axis name → set of event indices currently active
        private Dictionary<string, Dictionary<string, HashSet<int>>> _activeHoldEventIndices = new();

        // All standard axes we track
        private static readonly string[] StandardAxes =
            { "X", "Y", "Z", "RotationX", "RotationY", "RotationZ", "Slider0", "Slider1" };

        // ─── Config directory ─────────────────────────────────────────────────

        /// <summary>
        /// File next to the exe that stores the path to the user-chosen config directory.
        /// </summary>
        private static readonly string ConfigDirPointerPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config_dir.txt");

        private string _configDir = "";
        private AppSettings _appSettings = new();

        private string _currentLeverConfigFile = ""; // filename only, inside _configDir
        private string _currentButtonConfigFile = ""; // filename only, inside _configDir

        // ─── Constructor ──────────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();
            PopulateAxisComboBox();
            InitializeDirectInput();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (EnsureConfigDirectory())
                LoadSettings();

            RefreshJoysticks();
        }

        // ─── Config Directory ─────────────────────────────────────────────────

        /// <summary>
        /// Reads config_dir.txt. If missing or directory no longer exists, prompts the user.
        /// Returns true if a valid config directory is set.
        /// </summary>
        private bool EnsureConfigDirectory()
        {
            if (File.Exists(ConfigDirPointerPath))
            {
                string stored = File.ReadAllText(ConfigDirPointerPath).Trim();
                if (Directory.Exists(stored))
                {
                    _configDir = stored;
                    return true;
                }
            }

            return PromptForConfigDirectory(isFirstTime: true);
        }

        private bool PromptForConfigDirectory(bool isFirstTime = false)
        {
            string intro = isFirstTime
                ? "No configuration directory has been set yet.\n\n"
                : "The current configuration directory could not be found.\n\n";

            MessageBox.Show(
                intro + "Please select (or create) the directory where ConfJoystick will store all configuration files.",
                "Select Config Directory",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            var dialog = new OpenFolderDialog { Title = "Select Configuration Directory" };
            if (dialog.ShowDialog(this) != true)
            {
                SetStatus("No config directory set — file operations disabled");
                return false;
            }

            _configDir = dialog.FolderName;
            File.WriteAllText(ConfigDirPointerPath, _configDir);
            SetStatus($"Config directory: {_configDir}");
            return true;
        }

        // ─── Settings ─────────────────────────────────────────────────────────

        private string SettingsPath => Path.Combine(_configDir, "settings.json");

        private void LoadSettings()
        {
            if (string.IsNullOrEmpty(_configDir)) return;

            try
            {
                if (!File.Exists(SettingsPath)) return;

                string json = File.ReadAllText(SettingsPath);
                _appSettings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions()) ?? new();

                // Restore last selected axis (combo box is already populated at this point)
                if (!string.IsNullOrEmpty(_appSettings.LastSelectedAxis))
                {
                    int axisIdx = AxisComboBox.Items.IndexOf(_appSettings.LastSelectedAxis);
                    if (axisIdx >= 0)
                        AxisComboBox.SelectedIndex = axisIdx;
                }

                // Auto-load last lever config (which auto-loads its associated button config)
                if (!string.IsNullOrEmpty(_appSettings.LastLeverConfigFile))
                {
                    string path = Path.Combine(_configDir, _appSettings.LastLeverConfigFile);
                    if (File.Exists(path))
                        LoadLeverConfig(path);
                }
            }
            catch { /* silently ignore corrupt settings */ }
        }

        private void SaveSettings()
        {
            if (string.IsNullOrEmpty(_configDir)) return;

            try
            {
                _appSettings.LastLeverConfigFile = _currentLeverConfigFile;
                _appSettings.LastButtonConfigFile = _currentButtonConfigFile;
                _appSettings.LastSelectedJoystickName = _selectedDevice?.InstanceName ?? "";
                _appSettings.LastSelectedAxis = _selectedAxis;
                string json = JsonSerializer.Serialize(_appSettings, JsonOptions());
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }

        // ─── Lever Config ─────────────────────────────────────────────────────

        private void LoadLeverConfig(string filePath)
        {
            string json = File.ReadAllText(filePath);
            var file = JsonSerializer.Deserialize<LeverConfigFile>(json, JsonOptions());
            if (file == null) return;

            // Clear everything so no stale axes or buttons bleed through from a previous load
            _configuration.Clear();

            foreach (var entry in file.Joysticks)
                _configuration.Add(new JoystickConfig { JoystickName = entry.JoystickName, Axes = entry.Axes });

            _currentLeverConfigFile = Path.GetFileName(filePath);
            UpdateStatusBar();
            SetStatus("Lever config loaded");

            var warnings = ValidateLeverConfig(json, file);
            if (warnings.Count > 0)
                MessageBox.Show(
                    string.Join("\n\n", warnings),
                    "Config Warnings", MessageBoxButton.OK, MessageBoxImage.Warning);

            // Auto-load associated button config
            if (!string.IsNullOrEmpty(file.AssociatedButtonConfigFile))
            {
                string buttonPath = Path.Combine(_configDir, file.AssociatedButtonConfigFile);
                if (File.Exists(buttonPath))
                    LoadButtonConfig(buttonPath);
                else
                    MessageBox.Show(
                        $"Associated button config '{file.AssociatedButtonConfigFile}' was not found in the config directory.\n\nButton mappings are empty.",
                        "Button Config Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ─── Button Config ────────────────────────────────────────────────────

        private void LoadButtonConfig(string filePath)
        {
            string json = File.ReadAllText(filePath);
            var file = JsonSerializer.Deserialize<ButtonConfigFile>(json, JsonOptions());
            if (file == null) return;

            // Clear existing buttons so old mappings don't bleed through
            foreach (var cfg in _configuration)
                cfg.Buttons = new List<ButtonConfig>();

            foreach (var entry in file.Joysticks)
            {
                var existing = _configuration.FirstOrDefault(c =>
                    c.JoystickName.Equals(entry.JoystickName, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                    existing.Buttons = entry.Buttons;
                else
                    _configuration.Add(new JoystickConfig { JoystickName = entry.JoystickName, Buttons = entry.Buttons });
            }

            _currentButtonConfigFile = Path.GetFileName(filePath);
            UpdateStatusBar();
        }

        // ─── Menu: File ───────────────────────────────────────────────────────

        private void LoadLeverConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureHasConfigDir()) return;

            var dialog = new OpenFileDialog
            {
                Filter = "Lever config (*.json)|*.json|All files (*.*)|*.*",
                Title = "Load Lever Configuration",
                InitialDirectory = _configDir
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                LoadLeverConfig(dialog.FileName);
                SyncConfigWithDetectedJoysticks();
                SaveSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load lever config:\n{ex.Message}",
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveLeverConfigAs_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureHasConfigDir()) return;

            SyncConfigWithDetectedJoysticks();

            var dialog = new SaveFileDialog
            {
                Filter = "Lever config (*.json)|*.json|All files (*.*)|*.*",
                Title = "Save Lever Configuration As",
                InitialDirectory = _configDir,
                FileName = string.IsNullOrEmpty(_currentLeverConfigFile) ? "levers_config.json" : _currentLeverConfigFile
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                SaveLeverConfigTo(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save lever config:\n{ex.Message}",
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadButtonConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureHasConfigDir()) return;

            var dialog = new OpenFileDialog
            {
                Filter = "Button config (*.json)|*.json|All files (*.*)|*.*",
                Title = "Load Button Configuration",
                InitialDirectory = _configDir
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                LoadButtonConfig(dialog.FileName);
                SyncConfigWithDetectedJoysticks();
                SaveSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load button config:\n{ex.Message}",
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveButtonConfigAs_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureHasConfigDir()) return;

            SyncConfigWithDetectedJoysticks();

            var dialog = new SaveFileDialog
            {
                Filter = "Button config (*.json)|*.json|All files (*.*)|*.*",
                Title = "Save Button Configuration As",
                InitialDirectory = _configDir,
                FileName = string.IsNullOrEmpty(_currentButtonConfigFile) ? "buttons_config.json" : _currentButtonConfigFile
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                SaveButtonConfigTo(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save button config:\n{ex.Message}",
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureHasConfigDir()) return;

            SyncConfigWithDetectedJoysticks();

            bool savedAny = false;

            // Save lever config — prompt if no filename yet
            if (!string.IsNullOrEmpty(_currentLeverConfigFile))
            {
                try
                {
                    SaveLeverConfigTo(Path.Combine(_configDir, _currentLeverConfigFile));
                    savedAny = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save lever config:\n{ex.Message}",
                        "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "Lever config (*.json)|*.json|All files (*.*)|*.*",
                    Title = "Save Lever Configuration As",
                    InitialDirectory = _configDir,
                    FileName = "levers_config.json"
                };
                if (dialog.ShowDialog() != true) return;
                try { SaveLeverConfigTo(dialog.FileName); savedAny = true; }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save lever config:\n{ex.Message}",
                        "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            // Save button config — prompt if no filename yet
            if (!string.IsNullOrEmpty(_currentButtonConfigFile))
            {
                try
                {
                    SaveButtonConfigTo(Path.Combine(_configDir, _currentButtonConfigFile));
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save button config:\n{ex.Message}",
                        "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "Button config (*.json)|*.json|All files (*.*)|*.*",
                    Title = "Save Button Configuration As",
                    InitialDirectory = _configDir,
                    FileName = "buttons_config.json"
                };
                if (dialog.ShowDialog() != true) return;
                try { SaveButtonConfigTo(dialog.FileName); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save button config:\n{ex.Message}",
                        "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            if (savedAny)
                SetStatus("Saved lever and button configs");
        }

        // ─── Save helpers ─────────────────────────────────────────────────────

        private void SaveLeverConfigTo(string filePath)
        {
            var file = new LeverConfigFile
            {
                AssociatedButtonConfigFile = _currentButtonConfigFile,
                Joysticks = _configuration.Select(c => new LeverJoystickConfig
                {
                    JoystickName = c.JoystickName,
                    Axes = c.Axes
                }).ToList()
            };
            string raw = JsonSerializer.Serialize(file, JsonOptions());
            File.WriteAllText(filePath, FormatLeverConfig(raw));
            _currentLeverConfigFile = Path.GetFileName(filePath);
            UpdateStatusBar();
            SetStatus("Lever config saved");
            SaveSettings();
        }

        private void SaveButtonConfigTo(string filePath)
        {
            var file = new ButtonConfigFile
            {
                Joysticks = _configuration.Select(c => new ButtonJoystickConfig
                {
                    JoystickName = c.JoystickName,
                    Buttons = c.Buttons
                }).ToList()
            };
            File.WriteAllText(filePath, JsonSerializer.Serialize(file, JsonOptions()));
            _currentButtonConfigFile = Path.GetFileName(filePath);
            UpdateStatusBar();
            SetStatus("Button config saved");
            SaveSettings();
        }

        private void ChangeConfigDir_Click(object sender, RoutedEventArgs e)
        {
            PromptForConfigDirectory(isFirstTime: false);
        }

        private void ReloadConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureHasConfigDir()) return;

            bool reloaded = false;

            if (!string.IsNullOrEmpty(_currentLeverConfigFile))
            {
                string path = Path.Combine(_configDir, _currentLeverConfigFile);
                if (File.Exists(path))
                {
                    try
                    {
                        LoadLeverConfig(path);
                        SyncConfigWithDetectedJoysticks();
                        reloaded = true;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to reload lever config:\n{ex.Message}",
                            "Reload Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
                else
                {
                    MessageBox.Show($"Lever config '{_currentLeverConfigFile}' not found in config directory.",
                        "Reload Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(_currentButtonConfigFile))
            {
                string path = Path.Combine(_configDir, _currentButtonConfigFile);
                if (File.Exists(path))
                {
                    try
                    {
                        LoadButtonConfig(path);
                        SyncConfigWithDetectedJoysticks();
                        reloaded = true;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to reload button config:\n{ex.Message}",
                            "Reload Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
            }

            if (!reloaded)
                SetStatus("No config loaded to reload");
        }

        /// <summary>
        /// Makes sure a config directory exists before a file operation.
        /// Prompts the user if not. Returns false if still not set.
        /// </summary>
        private bool EnsureHasConfigDir()
        {
            if (!string.IsNullOrEmpty(_configDir) && Directory.Exists(_configDir))
                return true;

            return PromptForConfigDirectory(isFirstTime: false);
        }

        // ─── Initialization ───────────────────────────────────────────────────

        private void InitializeDirectInput()
        {
            try
            {
                _directInput = new DirectInput();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize DirectInput: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshJoysticks()
        {
            _monitorCts?.Cancel();
            _selectedJoystick?.Unacquire();
            _selectedJoystick?.Dispose();
            _selectedJoystick = null;

            _joystickDevices.Clear();
            JoystickComboBox.Items.Clear();

            if (_directInput == null) return;

            var devices = _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
            foreach (var device in devices)
            {
                _joystickDevices.Add(device);
                JoystickComboBox.Items.Add(device.InstanceName);
            }

            if (_joystickDevices.Count > 0)
            {
                // Restore last selected joystick by name, fall back to first
                int restoreIdx = string.IsNullOrEmpty(_appSettings.LastSelectedJoystickName) ? -1
                    : _joystickDevices.FindIndex(d =>
                        d.InstanceName.Equals(_appSettings.LastSelectedJoystickName, StringComparison.OrdinalIgnoreCase));

                JoystickComboBox.SelectedIndex = restoreIdx >= 0 ? restoreIdx : 0;
                SetStatus($"Found {_joystickDevices.Count} joystick(s)");
            }
            else
            {
                SetStatus("No joysticks detected");
            }

            SyncConfigWithDetectedJoysticks();
        }

        private void PopulateAxisComboBox()
        {
            AxisComboBox.Items.Clear();
            foreach (var axis in StandardAxes)
                AxisComboBox.Items.Add(axis);
            AxisComboBox.SelectedIndex = 0;
        }

        private void SyncConfigWithDetectedJoysticks()
        {
            foreach (var device in _joystickDevices)
            {
                var existing = _configuration.FirstOrDefault(c =>
                    c.JoystickName.Equals(device.InstanceName, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    _configuration.Add(new JoystickConfig
                    {
                        JoystickName = device.InstanceName,
                        Axes = StandardAxes.Select(a => new AxisConfig { Axis = a, Events = new List<AxisEvent>() }).ToList()
                    });
                }
                else
                {
                    foreach (var axisName in StandardAxes)
                    {
                        if (!existing.Axes.Any(a => a.Axis.Equals(axisName, StringComparison.OrdinalIgnoreCase)))
                            existing.Axes.Add(new AxisConfig { Axis = axisName, Events = new List<AxisEvent>() });
                    }
                }
            }
        }

        // ─── UI Events ────────────────────────────────────────────────────────

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshJoysticks();

        private void JoystickComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _monitorCts?.Cancel();
            _selectedJoystick?.Unacquire();
            _selectedJoystick?.Dispose();
            _selectedJoystick = null;

            int idx = JoystickComboBox.SelectedIndex;
            if (idx < 0 || idx >= _joystickDevices.Count) return;

            _selectedDevice = _joystickDevices[idx];

            try
            {
                _selectedJoystick = new Joystick(_directInput, _selectedDevice.InstanceGuid);
                _selectedJoystick.Properties.BufferSize = 128;
                _selectedJoystick.Acquire();
                SetStatus($"Selected: {_selectedDevice.InstanceName}");
                StartAxisMonitor();
                SaveSettings();
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to acquire joystick: {ex.Message}");
            }
        }

        private void AxisComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (AxisComboBox.SelectedItem is string axis)
            {
                _selectedAxis = axis;
                SaveSettings();
            }
        }

        private void VerificationTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            CharCountText.Text = $"Chars: {VerificationTextBox.Text.Length}";
        }

        private void ClearVerificationButton_Click(object sender, RoutedEventArgs e)
        {
            VerificationTextBox.Clear();
        }

        private void ToggleEmulationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isEmulationActive) StopEmulation();
            else StartEmulation();
        }

        private void ButtonMappingButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new ButtonMappingWindow(_directInput, _joystickDevices, _configuration);
            win.Owner = this;
            win.ShowDialog();
        }

        // ─── Axis Monitor ─────────────────────────────────────────────────────

        private void StartAxisMonitor()
        {
            _monitorCts = new CancellationTokenSource();
            Task.Run(() => AxisMonitorLoop(_monitorCts.Token));
        }

        private async Task AxisMonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_selectedJoystick != null)
                {
                    try
                    {
                        _selectedJoystick.Poll();
                        var state = _selectedJoystick.GetCurrentState();
                        int value = NormalizeAxisValue(GetAxisValue(state, _selectedAxis));
                        Dispatcher.Invoke(() => AxisValueText.Text = value.ToString());
                    }
                    catch { }
                }

                await Task.Delay(16, token).ConfigureAwait(false);
            }

            Dispatcher.Invoke(() => AxisValueText.Text = "---");
        }

        // ─── Emulation ────────────────────────────────────────────────────────

        private void StartEmulation()
        {
            if (_joystickDevices.Count == 0)
            {
                MessageBox.Show("No joysticks detected.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isEmulationActive = true;
            _emulationCts = new CancellationTokenSource();
            _previousAxisValues.Clear();
            _activeThresholds.Clear();
            _previousButtonStates.Clear();
            lock (_heldKeysLock) { _heldKeyRefs.Clear(); }
            _pressQueue.Clear();

            ToggleEmulationButton.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            ToggleEmulationButton.Content = "STOP EMULATION";
            SetStatus("Emulation ACTIVE");

            Task.Run(() => EmulationLoop(_emulationCts.Token));
            Task.Run(() => KeySenderLoop(_emulationCts.Token));
        }

        private void StopEmulation()
        {
            _isEmulationActive = false;
            _emulationCts?.Cancel();
            _pressQueue.Clear();

            List<VirtualKey> toRelease;
            lock (_heldKeysLock)
            {
                toRelease = _heldKeyRefs.Keys.ToList();
                _heldKeyRefs.Clear();
            }
            foreach (var key in toRelease) SendKeyUp(key);
            _activeHoldEventIndices.Clear();

            Dispatcher.Invoke(() =>
            {
                ToggleEmulationButton.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                ToggleEmulationButton.Content = "START EMULATION";
                SetStatus("Emulation stopped");
            });
        }

        private async Task EmulationLoop(CancellationToken token)
        {
            var joysticks = new Dictionary<string, Joystick>();

            foreach (var device in _joystickDevices)
            {
                try
                {
                    var js = new Joystick(_directInput, device.InstanceGuid);
                    js.Properties.BufferSize = 128;
                    js.Acquire();
                    joysticks[device.InstanceName] = js;
                    _previousAxisValues[device.InstanceName] = new Dictionary<string, int>();
                    _activeThresholds[device.InstanceName] = new Dictionary<string, HashSet<ThresholdKey>>();
                    _activeHoldEventIndices[device.InstanceName] = new Dictionary<string, HashSet<int>>();
                    _previousButtonStates[device.InstanceName] = null;
                }
                catch { }
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    foreach (var (name, js) in joysticks)
                    {
                        try
                        {
                            js.Poll();
                            var state = js.GetCurrentState();
                            var config = _configuration.FirstOrDefault(c =>
                                c.JoystickName.Equals(name, StringComparison.OrdinalIgnoreCase));
                            if (config != null)
                            {
                                ProcessJoystickState(name, state, config);
                                ProcessButtonStates(name, state, config);
                            }
                        }
                        catch { }
                    }

                    await Task.Delay(10, token).ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (var js in joysticks.Values)
                {
                    js.Unacquire();
                    js.Dispose();
                }
            }
        }

        private async Task KeySenderLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_pressQueue.TryDequeue(out var press))
                {
                    SendKeyPress(press.key, press.holdMs);
                    int interval = Math.Max(1, _appSettings.MaxKeypressIntervalMs);
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(5, token).ConfigureAwait(false);
                }
            }
        }

        private void ProcessButtonStates(string joystickName, JoystickState state, JoystickConfig config)
        {
            var current = state.Buttons;

            if (!_previousButtonStates.TryGetValue(joystickName, out bool[]? prev) || prev == null)
            {
                _previousButtonStates[joystickName] = (bool[])current.Clone();
                return;
            }

            for (int i = 0; i < Math.Min(current.Length, prev.Length); i++)
            {
                if (current[i] == prev[i]) continue;

                var mapping = config.Buttons?.FirstOrDefault(b => b.Button == i);
                if (mapping == null || string.IsNullOrEmpty(mapping.Key)) continue;

                var key = ParseKey(mapping.Key);
                if (key == VirtualKey.None) continue;

                var mods = (mapping.Modifiers ?? new List<string>())
                    .Select(m => ParseKey(m)).Where(k => k != VirtualKey.None).ToList();

                if (current[i])
                {
                    foreach (var m in mods) TryHoldKey(m);
                    TryHoldKey(key);
                }
                else
                {
                    ReleaseHeldKey(key);
                    foreach (var m in Enumerable.Reverse(mods)) ReleaseHeldKey(m);
                }
            }

            _previousButtonStates[joystickName] = (bool[])current.Clone();
        }

        private void ProcessJoystickState(string joystickName, JoystickState state, JoystickConfig config)
        {
            var prevValues   = _previousAxisValues[joystickName];
            var activeThresh = _activeThresholds[joystickName];

            if (!_activeHoldEventIndices.TryGetValue(joystickName, out var holdAxisMap))
            {
                holdAxisMap = new Dictionary<string, HashSet<int>>();
                _activeHoldEventIndices[joystickName] = holdAxisMap;
            }

            foreach (var axisConfig in config.Axes)
            {
                int current = NormalizeAxisValue(GetAxisValue(state, axisConfig.Axis));

                if (!prevValues.TryGetValue(axisConfig.Axis, out int previous))
                {
                    prevValues[axisConfig.Axis] = current;
                    activeThresh[axisConfig.Axis] = new HashSet<ThresholdKey>();
                    holdAxisMap[axisConfig.Axis] = new HashSet<int>();
                    continue;
                }

                var active = activeThresh[axisConfig.Axis];

                if (!holdAxisMap.TryGetValue(axisConfig.Axis, out var activeHoldIndices))
                {
                    activeHoldIndices = new HashSet<int>();
                    holdAxisMap[axisConfig.Axis] = activeHoldIndices;
                }

                for (int i = 0; i < axisConfig.Events.Count; i++)
                {
                    var evt = axisConfig.Events[i];
                    if (evt.PressType.Equals("HOLD", StringComparison.OrdinalIgnoreCase) &&
                        evt.HoldRanges is { Length: > 0 })
                        ProcessHoldRangeEvent(i, evt, current, previous, activeHoldIndices, axisConfig.Events);
                    else
                        ProcessAxisEvent(evt, current, previous, active);
                }

                prevValues[axisConfig.Axis] = current;
            }
        }

        /// <summary>
        /// Range-based HOLD: holds the key while the axis value is inside any configured range,
        /// releases it the moment the value leaves all ranges. Multiple ranges per event are ORed.
        /// If multiple HOLD events on the same axis map to the same key, the key is only released
        /// when none of those events are active.
        /// When Direction is set, the range must be entered from the correct side:
        ///   Rising  → entered from below (previous was below the range's minimum)
        ///   Falling → entered from above (previous was above the range's maximum)
        ///   Both    → no entry-direction restriction (default behaviour)
        /// </summary>
        private void ProcessHoldRangeEvent(int evtIndex, AxisEvent evt, int current, int previous,
            HashSet<int> activeHoldIndices, List<AxisEvent> allEvents)
        {
            if (evt.HoldRanges == null || evt.HoldRanges.Length == 0) return;

            var key = ParseKey(evt.Key);
            if (key == VirtualKey.None) return;

            bool inRange  = evt.HoldRanges.Any(r => r.Contains(current));
            bool isActive = activeHoldIndices.Contains(evtIndex);

            if (inRange && !isActive)
            {
                bool enteredCorrectly = evt.Direction.ToUpperInvariant() switch
                {
                    "RISING"  or "RIGHT" or "DOWN" => evt.HoldRanges.Any(r => r.Contains(current) && previous < r.Min),
                    "FALLING" or "LEFT"  or "UP"   => evt.HoldRanges.Any(r => r.Contains(current) && previous > r.Max),
                    _                               => true
                };

                if (enteredCorrectly && TryHoldKey(key))
                    activeHoldIndices.Add(evtIndex);
            }
            else if (!inRange && isActive)
            {
                activeHoldIndices.Remove(evtIndex);
                ReleaseHeldKey(key);
            }
        }

        private void ProcessAxisEvent(AxisEvent evt, int current, int previous, HashSet<ThresholdKey> active)
        {
            var key = ParseKey(evt.Key);
            if (key == VirtualKey.None) return;

            foreach (int t in evt.Thresholds)
            {
                bool risingCross  = previous < t && current >= t;
                bool fallingCross = previous >= t && current < t;

                var tkId = new ThresholdKey(
                    t,
                    evt.Direction.ToUpperInvariant(),
                    evt.Key.ToUpperInvariant(),
                    evt.PressType.ToUpperInvariant());
                bool isActive = active.Contains(tkId);

                // Threshold-based HOLD: position-driven (checked every poll).
                // Key is held while current value is "past" the threshold in the specified direction.
                if (evt.PressType.Equals("HOLD", StringComparison.OrdinalIgnoreCase))
                {
                    bool shouldHold = evt.Direction.ToUpperInvariant() switch
                    {
                        "RISING"  or "RIGHT" or "DOWN" or "BOTH" => current >= t,
                        "FALLING" or "LEFT"  or "UP"             => current <  t,
                        _                                         => false
                    };

                    if (shouldHold && !isActive)
                    {
                        if (TryHoldKey(key))
                            active.Add(tkId);
                    }
                    else if (!shouldHold && isActive)
                    {
                        ReleaseHeldKey(key);
                        active.Remove(tkId);
                    }
                    continue;
                }

                bool trigger = evt.Direction.ToUpperInvariant() switch
                {
                    "RISING"           => risingCross,
                    "FALLING"          => fallingCross,
                    "BOTH"             => risingCross || fallingCross,
                    "RIGHT" or "DOWN"  => risingCross,
                    "LEFT"  or "UP"    => fallingCross,
                    _                  => false
                };

                if (trigger)
                {
                    switch (evt.PressType.ToUpperInvariant())
                    {
                        case "KEYDOWN":
                            if (!active.Contains(tkId) && TryHoldKey(key))
                                active.Add(tkId);
                            break;

                        case "KEYUP":
                            if (IsKeyHeld(key))
                            {
                                ReleaseHeldKey(key);
                                // Clear paired KEYDOWN entries so they can re-trigger next crossing
                                string keyUpper = evt.Key.ToUpperInvariant();
                                foreach (var entry in active.Where(tk => tk.Key == keyUpper && tk.PressType == "KEYDOWN").ToList())
                                    active.Remove(entry);
                            }
                            active.Remove(tkId);
                            break;

                        default:
                        {
                            var (count, holdMs) = ParsePressType(evt.PressType);
                            int ms = holdMs >= 0 ? holdMs : _appSettings.DefaultPressMs;
                            for (int i = 0; i < count; i++)
                                _pressQueue.Enqueue((key, ms));
                            break;
                        }
                    }
                }
            }
        }

        // ─── Config Validation ────────────────────────────────────────────────

        private static List<string> ValidateLeverConfig(string json, LeverConfigFile file)
        {
            var warnings = new List<string>();
            string[] lines = json.Split('\n');

            foreach (var joystick in file.Joysticks)
            {
                foreach (var axis in joystick.Axes)
                {
                    foreach (var evt in axis.Events)
                    {
                        // Direction
                        var dirUpper = evt.Direction.ToUpperInvariant().Trim();
                        if (dirUpper is not ("RISING" or "FALLING" or "BOTH" or "RIGHT" or "DOWN" or "LEFT" or "UP"))
                        {
                            int ln = FindFieldLineNumber(lines, "Direction", evt.Direction);
                            warnings.Add(
                                $"Line {ln}: Invalid Direction \"{evt.Direction}\"\n" +
                                $"  Valid values: Rising, Falling, Both, Right, Down, Left, Up");
                        }

                        // PressType
                        var ptUpper = evt.PressType.ToUpperInvariant().Trim();
                        var (count, _) = ParsePressType(evt.PressType);
                        bool validPressType = ptUpper is "HOLD" or "KEYDOWN" or "KEYUP" || count > 0;
                        if (!validPressType)
                        {
                            int ln = FindFieldLineNumber(lines, "PressType", evt.PressType);
                            warnings.Add(
                                $"Line {ln}: Invalid PressType \"{evt.PressType}\"\n" +
                                $"  Valid values: Hold, KeyDown, KeyUp, SinglePress, DoublePress,\n" +
                                $"               nPress (e.g. 3Press), nPressMSm (e.g. 3PressMS50)");
                        }
                    }
                }
            }

            return warnings;
        }

        private static int FindFieldLineNumber(string[] lines, string fieldName, string value)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains($"\"{fieldName}\"", StringComparison.OrdinalIgnoreCase) &&
                    lines[i].Contains($"\"{value}\"",     StringComparison.OrdinalIgnoreCase))
                    return i + 1;
            }
            return -1;
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Atomically increments the held-key ref count and, if the key was not already
        /// physically held, sends KeyDown. Respects MaxConcurrentKeys (0 = unlimited).
        /// Returns false (and does nothing) when the limit would be exceeded by a new key.
        /// </summary>
        private bool TryHoldKey(VirtualKey key)
        {
            bool doSendDown = false;
            lock (_heldKeysLock)
            {
                if (_heldKeyRefs.TryGetValue(key, out int count) && count > 0)
                {
                    _heldKeyRefs[key] = count + 1; // already physically held — just add a ref
                    return true;
                }
                if (_appSettings.MaxConcurrentKeys > 0 && _heldKeyRefs.Count >= _appSettings.MaxConcurrentKeys)
                    return false;
                _heldKeyRefs[key] = 1;
                doSendDown = true;
            }
            if (doSendDown) SendKeyDown(key);
            return true;
        }

        /// <summary>
        /// Decrements the held-key ref count and, when it reaches zero, sends KeyUp.
        /// </summary>
        private void ReleaseHeldKey(VirtualKey key)
        {
            bool doSendUp = false;
            lock (_heldKeysLock)
            {
                if (_heldKeyRefs.TryGetValue(key, out int count) && count > 0)
                {
                    count--;
                    if (count == 0) { _heldKeyRefs.Remove(key); doSendUp = true; }
                    else _heldKeyRefs[key] = count;
                }
            }
            if (doSendUp) SendKeyUp(key);
        }

        private bool IsKeyHeld(VirtualKey key)
        {
            lock (_heldKeysLock)
                return _heldKeyRefs.TryGetValue(key, out int c) && c > 0;
        }

        private void SetStatus(string message)
        {
            if (StatusText.CheckAccess())
                StatusText.Text = message;
            else
                Dispatcher.Invoke(() => StatusText.Text = message);
        }

        private void UpdateStatusBar()
        {
            string lever  = string.IsNullOrEmpty(_currentLeverConfigFile)  ? "—" : _currentLeverConfigFile;
            string button = string.IsNullOrEmpty(_currentButtonConfigFile) ? "—" : _currentButtonConfigFile;

            if (LeverFileText.CheckAccess())
            {
                LeverFileText.Text  = lever;
                ButtonFileText.Text = button;
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    LeverFileText.Text  = lever;
                    ButtonFileText.Text = button;
                });
            }
        }

        private static int NormalizeAxisValue(int raw) =>
            (int)Math.Round(raw * 100.0 / 65535.0);

        private static int GetAxisValue(JoystickState state, string axisName) =>
            axisName.ToUpperInvariant() switch
            {
                "X"                          => state.X,
                "Y"                          => state.Y,
                "Z"                          => state.Z,
                "ROTATIONX" or "RX"          => state.RotationX,
                "ROTATIONY" or "RY"          => state.RotationY,
                "ROTATIONZ" or "RZ"          => state.RotationZ,
                "SLIDER0"   or "SLIDER"      => state.Sliders.Length > 0 ? state.Sliders[0] : 0,
                "SLIDER1"                    => state.Sliders.Length > 1 ? state.Sliders[1] : 0,
                _                            => 0
            };

        protected override void OnClosed(EventArgs e)
        {
            SaveSettings();
            _monitorCts?.Cancel();
            StopEmulation();
            _selectedJoystick?.Unacquire();
            _selectedJoystick?.Dispose();
            _directInput?.Dispose();
            base.OnClosed(e);
        }

        private static JsonSerializerOptions JsonOptions() => new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        /// <summary>
        /// Post-processes indented lever-config JSON into a more human-friendly compact form:
        ///   • Threshold integer arrays → single line: [10, 25, 40]
        ///   • Threshold string arrays  → single line: ["30-40", "50-60"]
        ///   • Direction / PressType / Key → same line
        /// </summary>
        private static string FormatLeverConfig(string json)
        {
            // Compact integer arrays (regular Threshold values)
            json = Regex.Replace(json,
                @"\[\s*\n(\s*-?\d+,?\s*\n)+\s*\]",
                m => "[" + string.Join(", ",
                    Regex.Matches(m.Value, @"-?\d+").Select(x => x.Value)) + "]");

            // Compact string arrays (HOLD range values like "30-40")
            json = Regex.Replace(json,
                @"\[\s*\n(\s*""[^""\n]+"",?\s*\n)+\s*\]",
                m => "[" + string.Join(", ",
                    Regex.Matches(m.Value, @"""[^""\n]+""").Select(x => x.Value)) + "]");

            // Put Direction, PressType, Key on one line
            json = Regex.Replace(json,
                "\"Direction\": \"([^\"]*)\",\\s*\\n\\s*\"PressType\": \"([^\"]*)\",\\s*\\n\\s*\"Key\": \"([^\"]*)\"",
                m => $"\"Direction\": \"{m.Groups[1].Value}\", \"PressType\": \"{m.Groups[2].Value}\", \"Key\": \"{m.Groups[3].Value}\"");

            return json;
        }

        // ─── Keyboard Simulation ──────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private const uint MAPVK_VK_TO_VSC = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public INPUTUNION Union;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int    dx;
            public int    dy;
            public uint   mouseData;
            public uint   dwFlags;
            public uint   time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT Mouse;
            [FieldOffset(0)] public KEYBDINPUT Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint   Flags;
            public uint   Time;
            public IntPtr ExtraInfo;
        }

        private const uint INPUT_KEYBOARD        = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP       = 0x0002;
        private const uint KEYEVENTF_SCANCODE    = 0x0008;

        private static readonly HashSet<VirtualKey> ExtendedKeys = new()
        {
            VirtualKey.Insert,   VirtualKey.Delete,
            VirtualKey.Home,     VirtualKey.End,
            VirtualKey.PageUp,   VirtualKey.PageDown,
            VirtualKey.Up,       VirtualKey.Down,
            VirtualKey.Left,     VirtualKey.Right,
            VirtualKey.RightControl, VirtualKey.RightAlt,
            VirtualKey.NumPadDivide
        };

        private INPUT BuildKeyInput(VirtualKey key, bool keyUp)
        {
            ushort scan = (ushort)MapVirtualKey((uint)key, MAPVK_VK_TO_VSC);
            uint flags = KEYEVENTF_SCANCODE;
            if (keyUp)                      flags |= KEYEVENTF_KEYUP;
            if (ExtendedKeys.Contains(key)) flags |= KEYEVENTF_EXTENDEDKEY;

            return new INPUT
            {
                Type  = INPUT_KEYBOARD,
                Union = new INPUTUNION
                {
                    Keyboard = new KEYBDINPUT { ScanCode = scan, Flags = flags }
                }
            };
        }

        private void SendKeyDown(VirtualKey key) =>
            SendInput(1, new[] { BuildKeyInput(key, false) }, Marshal.SizeOf<INPUT>());

        private void SendKeyUp(VirtualKey key) =>
            SendInput(1, new[] { BuildKeyInput(key, true) }, Marshal.SizeOf<INPUT>());

        private void SendKeyPress(VirtualKey key, int holdMs)
        {
            SendKeyDown(key);
            Thread.Sleep(holdMs);
            SendKeyUp(key);
        }

        // Returns (pressCount, holdMs) where holdMs is -1 when not specified (use DefaultPressMs).
        // Formats: "nPress" / "SinglePress" / "DoublePress"  →  (n, -1)
        //          "nPressMSm"                               →  (n, m)
        private static (int count, int holdMs) ParsePressType(string pressType)
        {
            var upper = pressType.ToUpperInvariant().Trim();

            // nPressMSm
            int msIdx = upper.IndexOf("PRESSMS", StringComparison.Ordinal);
            if (msIdx > 0)
            {
                string countPart = upper[..msIdx];
                string msPart    = upper[(msIdx + 7)..];
                if (int.TryParse(countPart, out int cn) && cn > 0 &&
                    int.TryParse(msPart,    out int ms) && ms >= 0)
                    return (cn, ms);
            }

            // Legacy formats
            if (upper == "SINGLEPRESS") return (1, -1);
            if (upper == "DOUBLEPRESS") return (2, -1);
            if (upper.EndsWith("PRESS"))
            {
                string numPart = upper[..^5];
                if (int.TryParse(numPart, out int n) && n > 0)
                    return (n, -1);
            }
            return (0, -1);
        }

        private static VirtualKey ParseKey(string keyString)
        {
            if (string.IsNullOrEmpty(keyString)) return VirtualKey.None;

            if (keyString.Length == 1)
            {
                char c = char.ToUpperInvariant(keyString[0]);
                if (c >= 'A' && c <= 'Z') return (VirtualKey)(0x41 + (c - 'A'));
                if (c >= '0' && c <= '9') return (VirtualKey)(0x30 + (c - '0'));
            }

            return keyString.ToUpperInvariant() switch
            {
                "SPACE"                              => VirtualKey.Space,
                "ENTER" or "RETURN"                  => VirtualKey.Return,
                "TAB"                                => VirtualKey.Tab,
                "ESCAPE" or "ESC"                    => VirtualKey.Escape,
                "BACKSPACE"                          => VirtualKey.Back,
                "DELETE" or "DEL"                    => VirtualKey.Delete,
                "INSERT" or "INS"                    => VirtualKey.Insert,
                "HOME"                               => VirtualKey.Home,
                "END"                                => VirtualKey.End,
                "PAGEUP"   or "PGUP"                 => VirtualKey.PageUp,
                "PAGEDOWN" or "PGDN"                 => VirtualKey.PageDown,
                "UP"    or "UPARROW"                 => VirtualKey.Up,
                "DOWN"  or "DOWNARROW"               => VirtualKey.Down,
                "LEFT"  or "LEFTARROW"               => VirtualKey.Left,
                "RIGHT" or "RIGHTARROW"              => VirtualKey.Right,
                "LSHIFT" or "LEFTSHIFT"              => VirtualKey.LeftShift,
                "RSHIFT" or "RIGHTSHIFT"             => VirtualKey.RightShift,
                "SHIFT"                              => VirtualKey.Shift,
                "LCTRL"  or "LEFTCTRL"  or "LEFTCONTROL"  => VirtualKey.LeftControl,
                "RCTRL"  or "RIGHTCTRL" or "RIGHTCONTROL" => VirtualKey.RightControl,
                "CTRL"   or "CONTROL"                => VirtualKey.Control,
                "LALT"   or "LEFTALT"                => VirtualKey.LeftAlt,
                "RALT"   or "RIGHTALT"               => VirtualKey.RightAlt,
                "ALT"                                => VirtualKey.Alt,
                "F1"  => VirtualKey.F1,  "F2"  => VirtualKey.F2,
                "F3"  => VirtualKey.F3,  "F4"  => VirtualKey.F4,
                "F5"  => VirtualKey.F5,  "F6"  => VirtualKey.F6,
                "F7"  => VirtualKey.F7,  "F8"  => VirtualKey.F8,
                "F9"  => VirtualKey.F9,  "F10" => VirtualKey.F10,
                "F11" => VirtualKey.F11, "F12" => VirtualKey.F12,
                "NUMPAD0" or "NUM0" => VirtualKey.NumPad0,
                "NUMPAD1" or "NUM1" => VirtualKey.NumPad1,
                "NUMPAD2" or "NUM2" => VirtualKey.NumPad2,
                "NUMPAD3" or "NUM3" => VirtualKey.NumPad3,
                "NUMPAD4" or "NUM4" => VirtualKey.NumPad4,
                "NUMPAD5" or "NUM5" => VirtualKey.NumPad5,
                "NUMPAD6" or "NUM6" => VirtualKey.NumPad6,
                "NUMPAD7" or "NUM7" => VirtualKey.NumPad7,
                "NUMPAD8" or "NUM8" => VirtualKey.NumPad8,
                "NUMPAD9" or "NUM9" => VirtualKey.NumPad9,
                "NUMPAD+" or "NUMADD"      or "NUM+" => VirtualKey.NumPadAdd,
                "NUMPAD-" or "NUMSUBTRACT" or "NUM-" => VirtualKey.NumPadSubtract,
                "NUMPAD*" or "NUMMULTIPLY" or "NUM*" => VirtualKey.NumPadMultiply,
                "NUMPAD/" or "NUMDIVIDE"   or "NUM/" => VirtualKey.NumPadDivide,
                "NUMPAD." or "NUMDECIMAL"  or "NUM." => VirtualKey.NumPadDecimal,
                _                   => VirtualKey.None
            };
        }
    }

    // ─── App Settings ─────────────────────────────────────────────────────────

    public class AppSettings
    {
        /// <summary>Filename (no path) of the last loaded lever config, inside the config directory.</summary>
        public string LastLeverConfigFile { get; set; } = "";
        /// <summary>Filename (no path) of the last loaded button config, inside the config directory.</summary>
        public string LastButtonConfigFile { get; set; } = "";

        /// <summary>Instance name of the last joystick selected in the axis visualizer.</summary>
        public string LastSelectedJoystickName { get; set; } = "";
        /// <summary>Axis last selected in the axis visualizer (e.g. "X", "RotationZ").</summary>
        public string LastSelectedAxis { get; set; } = "";

        /// <summary>
        /// Maximum number of keys that may be physically held down at the same time.
        /// 0 means unlimited.
        /// </summary>
        public int MaxConcurrentKeys { get; set; } = 0;

        /// <summary>
        /// Minimum interval in milliseconds between consecutive key presses sent from
        /// the press queue (SinglePress / DoublePress / …).  Lower = faster repeat.
        /// </summary>
        public int MaxKeypressIntervalMs { get; set; } = 60;

        /// <summary>
        /// Default key-hold duration in milliseconds for press types that do not
        /// specify one (e.g. "2Press").  Use "nPressMSm" to override per-binding.
        /// </summary>
        public int DefaultPressMs { get; set; } = 30;
    }

    // ─── Lever Config File ────────────────────────────────────────────────────

    public class LeverConfigFile
    {
        /// <summary>Filename (no path) of the button config to auto-load alongside this lever config.</summary>
        public string AssociatedButtonConfigFile { get; set; } = "";
        public List<LeverJoystickConfig> Joysticks { get; set; } = new();
    }

    public class LeverJoystickConfig
    {
        public string JoystickName { get; set; } = "";
        public List<AxisConfig> Axes { get; set; } = new();
    }

    // ─── Button Config File ───────────────────────────────────────────────────

    public class ButtonConfigFile
    {
        public List<ButtonJoystickConfig> Joysticks { get; set; } = new();
    }

    public class ButtonJoystickConfig
    {
        public string JoystickName { get; set; } = "";
        public List<ButtonConfig> Buttons { get; set; } = new();
    }

    // ─── Configuration Model ──────────────────────────────────────────────────

    public class JoystickConfig
    {
        public string JoystickName { get; set; } = "";
        public List<AxisConfig> Axes { get; set; } = new();
        public List<ButtonConfig> Buttons { get; set; } = new();
    }

    public class ButtonConfig
    {
        public int Button { get; set; }
        public string Key { get; set; } = "";
        public List<string> Modifiers { get; set; } = new();
    }

    public class AxisConfig
    {
        public string Axis { get; set; } = "";
        public List<AxisEvent> Events { get; set; } = new();
    }

    [JsonConverter(typeof(AxisEventConverter))]
    public class AxisEvent
    {
        /// <summary>For regular events: axis value(s) at which the event fires.</summary>
        public int[] Thresholds { get; set; } = Array.Empty<int>();

        /// <summary>For HOLD events: axis-value ranges that keep the key held.</summary>
        public HoldRange[]? HoldRanges { get; set; }

        public string Direction { get; set; } = "Both";
        public string PressType { get; set; } = "Hold";
        public string Key       { get; set; } = "";
    }

    /// <summary>An inclusive axis-value range used by HOLD events.</summary>
    public record HoldRange(int Min, int Max)
    {
        public bool Contains(int value) => value >= Min && value <= Max;
        public override string ToString() => $"{Min}-{Max}";
    }

    /// <summary>
    /// Reads and writes <see cref="AxisEvent"/> with a unified "Threshold" JSON field.
    /// <para>Reading: integer values → <see cref="AxisEvent.Thresholds"/> (regular events);
    /// range strings like "30-40" → <see cref="AxisEvent.HoldRanges"/> (HOLD events).</para>
    /// <para>Writing: always <c>"Threshold"</c> — integers for regular events, range strings for HOLD events.</para>
    /// </summary>
    public class AxisEventConverter : JsonConverter<AxisEvent>
    {
        private static HoldRange ParseRange(string s)
        {
            int dash = s.IndexOf('-', 1); // skip potential leading minus
            int min  = int.Parse(s[..dash].Trim());
            int max  = int.Parse(s[(dash + 1)..].Trim());
            return new HoldRange(Math.Min(min, max), Math.Max(min, max));
        }

        public override AxisEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            var evt  = new AxisEvent();

            foreach (var prop in root.EnumerateObject())
            {
                var val = prop.Value;
                switch (prop.Name.ToUpperInvariant())
                {
                    case "DIRECTION": evt.Direction = val.GetString() ?? "Both"; break;
                    case "PRESSTYPE": evt.PressType = val.GetString() ?? "Hold"; break;
                    case "KEY":       evt.Key       = val.GetString() ?? "";     break;
                    case "THRESHOLD":
                        if (val.ValueKind == JsonValueKind.Number)
                        {
                            evt.Thresholds = new[] { val.GetInt32() };
                        }
                        else if (val.ValueKind == JsonValueKind.String)
                        {
                            evt.HoldRanges = new[] { ParseRange(val.GetString()!) };
                        }
                        else if (val.ValueKind == JsonValueKind.Array)
                        {
                            var elements = val.EnumerateArray().ToArray();
                            if (elements.Length > 0 && elements[0].ValueKind == JsonValueKind.String)
                                evt.HoldRanges = elements.Select(e => ParseRange(e.GetString()!)).ToArray();
                            else
                                evt.Thresholds = elements.Select(e => e.GetInt32()).ToArray();
                        }
                        break;
                }
            }

            return evt;
        }

        public override void Write(Utf8JsonWriter writer, AxisEvent value, JsonSerializerOptions options)
        {
            bool isHold = value.PressType.Equals("HOLD", StringComparison.OrdinalIgnoreCase);

            writer.WriteStartObject();

            writer.WritePropertyName("ThresHold");
            if (isHold && value.HoldRanges is { Length: > 0 })
            {
                if (value.HoldRanges.Length == 1)
                    writer.WriteStringValue(value.HoldRanges[0].ToString());
                else
                {
                    writer.WriteStartArray();
                    foreach (var r in value.HoldRanges)
                        writer.WriteStringValue(r.ToString());
                    writer.WriteEndArray();
                }
            }
            else
            {
                if (value.Thresholds.Length == 1)
                    writer.WriteNumberValue(value.Thresholds[0]);
                else
                {
                    writer.WriteStartArray();
                    foreach (var v in value.Thresholds)
                        writer.WriteNumberValue(v);
                    writer.WriteEndArray();
                }
            }

            writer.WriteString("Direction", value.Direction);
            writer.WriteString("PressType", value.PressType);
            writer.WriteString("Key", value.Key);

            writer.WriteEndObject();
        }
    }

    public record ThresholdKey(int Threshold, string Direction, string Key, string PressType);

    // ─── Virtual Key Codes ────────────────────────────────────────────────────

    public enum VirtualKey : ushort
    {
        None        = 0x00,
        Back        = 0x08,
        Tab         = 0x09,
        Return      = 0x0D,
        Shift       = 0x10,
        Control     = 0x11,
        Alt         = 0x12,
        Escape      = 0x1B,
        Space       = 0x20,
        PageUp      = 0x21,
        PageDown    = 0x22,
        End         = 0x23,
        Home        = 0x24,
        Left        = 0x25,
        Up          = 0x26,
        Right       = 0x27,
        Down        = 0x28,
        Insert      = 0x2D,
        Delete      = 0x2E,
        LeftShift   = 0xA0,
        RightShift  = 0xA1,
        LeftControl = 0xA2,
        RightControl= 0xA3,
        LeftAlt     = 0xA4,
        RightAlt    = 0xA5,
        F1  = 0x70, F2  = 0x71, F3  = 0x72, F4  = 0x73,
        F5  = 0x74, F6  = 0x75, F7  = 0x76, F8  = 0x77,
        F9  = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,
        NumPad0 = 0x60, NumPad1 = 0x61, NumPad2 = 0x62, NumPad3 = 0x63,
        NumPad4 = 0x64, NumPad5 = 0x65, NumPad6 = 0x66, NumPad7 = 0x67,
        NumPad8 = 0x68, NumPad9 = 0x69,
        NumPadMultiply = 0x6A,
        NumPadAdd      = 0x6B,
        NumPadSubtract = 0x6D,
        NumPadDecimal  = 0x6E,
        NumPadDivide   = 0x6F
    }
}
