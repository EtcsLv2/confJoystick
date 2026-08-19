using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SharpDX.DirectInput;
using DxEffect    = SharpDX.DirectInput.Effect;
using DxCondition = SharpDX.DirectInput.Condition;

namespace ConfJoystick
{
    public partial class MainWindow : Window
    {
        // DirectInput
        private DirectInput? _directInput;

        // Joystick for the UI selection (axis monitor)
        private Joystick? _selectedJoystick;
        private DeviceInstance? _selectedDevice;
        private readonly List<DeviceInstance> _joystickDevices = new();

        // Axis monitor
        private CancellationTokenSource? _monitorCts;
        private string _selectedAxis = "X";

        // Watches every device so that pressing a button selects the joystick it belongs to
        private CancellationTokenSource? _deviceWatchCts;

        // Emulation
        private bool _isEmulationActive = false;
        private CancellationTokenSource? _emulationCts;

        /// <summary>
        /// Set while the button-mapping window is open. Button emulation is suspended, so the
        /// buttons being mapped do not also fire their current keys into whatever window has the
        /// focus, and the device watch stands down because that window does its own selecting.
        /// </summary>
        private volatile bool _mappingWindowOpen;

        // Force feedback — own lifetime so it can be restarted when its config is reloaded
        private CancellationTokenSource? _ffbCts;
        private Task? _ffbTask;

        // Configuration (all detected joysticks)
        private readonly List<JoystickConfig> _configuration = new();

        // Runtime state for the emulation loop
        private readonly Dictionary<KeyToken, int> _heldKeyRefs = new();
        private readonly object _heldKeysLock = new();
        private readonly Dictionary<string, Dictionary<string, double>> _previousAxisValues = new();
        private readonly Dictionary<string, Dictionary<string, HashSet<ThresholdKey>>> _activeThresholds = new();

        // Rate-limited action queues, one per lever - see LeverId. Presses, holds and
        // releases all travel the same queue, so a lever's keystrokes leave in the order the
        // lever produced them: a hold can no longer overtake presses queued ahead of it and
        // strand its key up. A queue per lever keeps a busy lever's backlog from delaying an
        // idle one; the sender round-robins between them. Each entry carries the moment it was
        // queued, so a press that has been waiting longer than it is still meaningful is dropped.
        private readonly ConcurrentDictionary<LeverId, ConcurrentQueue<KeyAction>> _leverQueues = new();

        // Button state tracking for the emulation loop
        private readonly Dictionary<string, bool[]?> _previousButtonStates = new();

        // Tracks which hold-range event indices are currently holding their key
        // joystick name → axis name → set of event indices currently active
        private readonly Dictionary<string, Dictionary<string, HashSet<int>>> _activeHoldEventIndices = new();

        // Direction of travel per axis, used to cut the queued presses off when a lever reverses
        // joystick name → axis name → travel state
        private readonly Dictionary<string, Dictionary<string, AxisTravel>> _axisTravel = new();

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
        private string _currentFfbConfigFile = ""; // filename only, inside _configDir
        private FfbConfigFile? _ffbConfig;

        private IntPtr _windowHandle;

        /// <summary>
        /// False until the startup restore in <see cref="MainWindow_Loaded"/> has finished.
        /// Selecting a joystick saves the settings, and RefreshJoysticks selects one before the
        /// configs have been restored — so without this guard startup overwrote the remembered
        /// config filenames with the empty strings they still had, and the app forgot which
        /// configs to reopen every single time.
        /// </summary>
        private bool _startupComplete;

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
            _windowHandle = new WindowInteropHelper(this).Handle;

            // Order matters: the settings file supplies the joystick to restore, and the configs
            // are validated against the detected devices, so they must be loaded last.
            bool haveConfigDir = EnsureConfigDirectory();
            if (haveConfigDir) LoadSettings();

            RefreshJoysticks();

            if (haveConfigDir) AutoLoadConfigs();

            // From here on the in-memory state is the truth and may be written back.
            _startupComplete = true;
            SaveSettings();
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
            }
            catch { /* silently ignore corrupt settings */ }
        }

        /// <summary>
        /// Re-opens the configs that were in use when the app was last closed: the lever config,
        /// and with it the button and FFB configs it names as associations.
        /// Must run after the joysticks have been enumerated so config validation can see them.
        /// </summary>
        private void AutoLoadConfigs()
        {
            if (string.IsNullOrEmpty(_configDir)) return;

            try
            {
                // Seed the current filenames from the settings first, so keepUnassociatedConfigs
                // has something to fall back on. An association named by the lever config still
                // wins; this only covers a lever config saved before it had one, and a button or
                // FFB config that was last loaded on its own. Missing files are dropped here
                // rather than inside LoadLeverConfig, which would report them as a broken
                // association the user never made.
                _currentButtonConfigFile = ExistingConfigFile(_appSettings.LastButtonConfigFile);
                _currentFfbConfigFile    = ExistingConfigFile(_appSettings.LastFfbConfigFile);

                string leverFile = ExistingConfigFile(_appSettings.LastLeverConfigFile);

                if (!string.IsNullOrEmpty(leverFile))
                {
                    // Pulls in the associated button and FFB configs on its own.
                    LoadLeverConfig(Path.Combine(_configDir, leverFile), keepUnassociatedConfigs: true);
                    SyncConfigWithDetectedJoysticks();
                }
                else
                {
                    // No lever config to pull them in — restore the other two on their own.
                    if (!string.IsNullOrEmpty(_currentButtonConfigFile))
                    {
                        LoadButtonConfig(Path.Combine(_configDir, _currentButtonConfigFile));
                        SyncConfigWithDetectedJoysticks();
                    }

                    if (!string.IsNullOrEmpty(_currentFfbConfigFile))
                        LoadFfbConfig(Path.Combine(_configDir, _currentFfbConfigFile));
                }

                UpdateStatusBar();
            }
            catch (Exception ex)
            {
                SetStatus($"Could not auto-load the last config: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the filename if it names a file that is really there in the config directory,
        /// otherwise an empty string.
        /// </summary>
        private string ExistingConfigFile(string fileName) =>
            !string.IsNullOrEmpty(fileName) && File.Exists(Path.Combine(_configDir, fileName))
                ? fileName
                : "";

        private void SaveSettings()
        {
            if (string.IsNullOrEmpty(_configDir) || !_startupComplete) return;

            try
            {
                _appSettings.LastLeverConfigFile = _currentLeverConfigFile;
                _appSettings.LastButtonConfigFile = _currentButtonConfigFile;
                _appSettings.LastFfbConfigFile = _currentFfbConfigFile;
                _appSettings.LastSelectedJoystickName = _selectedDevice?.InstanceName ?? "";
                _appSettings.LastSelectedAxis = _selectedAxis;
                string json = JsonSerializer.Serialize(_appSettings, JsonOptions());
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }

        // ─── Lever Config ─────────────────────────────────────────────────────

        /// <param name="keepUnassociatedConfigs">
        /// Controls what happens to the button and FFB configs when this lever config does not
        /// name one. False (an explicit load) treats the file as authoritative and drops them —
        /// otherwise the previous config's mappings would survive into an unrelated lever config
        /// and be written back to its file on the next save. True (a reload) keeps whatever is
        /// loaded right now and re-reads it, so a config that was loaded standalone is not
        /// silently thrown away by reloading the lever config.
        /// </param>
        private void LoadLeverConfig(string filePath, bool keepUnassociatedConfigs = false)
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

            // Button config: the association if the file names one, otherwise the config that is
            // already loaded when this is a reload. Empty means the button mappings cleared above
            // are the live state, so the previously loaded filename has to go as well — otherwise
            // the next Save would overwrite an unrelated button config with an empty mapping set.
            string buttonFile = !string.IsNullOrEmpty(file.AssociatedButtonConfigFile)
                ? file.AssociatedButtonConfigFile
                : keepUnassociatedConfigs ? _currentButtonConfigFile : "";

            if (!string.IsNullOrEmpty(buttonFile))
            {
                string buttonPath = Path.Combine(_configDir, buttonFile);
                if (File.Exists(buttonPath))
                {
                    LoadButtonConfig(buttonPath);
                }
                else
                {
                    // The mappings are already gone; drop the filename too so the missing file
                    // is not resurrected as an association by the next save.
                    _currentButtonConfigFile = "";
                    MessageBox.Show(
                        $"Button config '{buttonFile}' was not found in the config directory.\n\nButton mappings are empty.",
                        "Button Config Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                _currentButtonConfigFile = "";
            }

            // FFB config — same reasoning.
            string ffbFile = !string.IsNullOrEmpty(file.AssociatedFfbConfigFile)
                ? file.AssociatedFfbConfigFile
                : keepUnassociatedConfigs ? _currentFfbConfigFile : "";

            if (!string.IsNullOrEmpty(ffbFile))
            {
                string ffbPath = Path.Combine(_configDir, ffbFile);
                if (File.Exists(ffbPath))
                {
                    LoadFfbConfig(ffbPath);
                }
                else
                {
                    // Unlike the button mappings, the FFB config is still live at this point, so
                    // it has to be cleared explicitly for the message below to be true.
                    ClearFfbConfig();
                    MessageBox.Show(
                        $"FFB config '{ffbFile}' was not found in the config directory.\n\nForce feedback is disabled.",
                        "FFB Config Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                ClearFfbConfig();
            }

            UpdateStatusBar();
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

        // ─── FFB Config ───────────────────────────────────────────────────────

        private void LoadFfbConfig(string filePath)
        {
            string json = File.ReadAllText(filePath);
            _ffbConfig = JsonSerializer.Deserialize<FfbConfigFile>(json, JsonOptions()) ?? new();
            _currentFfbConfigFile = Path.GetFileName(filePath);
            UpdateStatusBar();

            var warnings = ValidateFfbConfig(_ffbConfig);
            if (warnings.Count > 0)
                MessageBox.Show(
                    string.Join("\n\n", warnings),
                    "FFB Config Warnings", MessageBoxButton.OK, MessageBoxImage.Warning);

            // The FFB loop snapshots its devices and effects when it starts, so a config that is
            // (re)loaded during a running emulation only takes effect after a restart.
            RestartFfbIfRunning();
        }

        /// <summary>
        /// Checks an FFB config against the detected devices. Every problem listed here would
        /// otherwise show up only as force feedback silently doing nothing.
        /// </summary>
        private List<string> ValidateFfbConfig(FfbConfigFile file)
        {
            var warnings = new List<string>();

            foreach (var dev in file.Devices)
            {
                if (!_joystickDevices.Any(d =>
                        d.InstanceName.Equals(dev.DeviceName, StringComparison.OrdinalIgnoreCase)))
                {
                    string detected = _joystickDevices.Count == 0
                        ? "  (no devices detected)"
                        : string.Join("\n", _joystickDevices.Select(d => $"  \"{d.InstanceName}\""));
                    warnings.Add(
                        $"DeviceName \"{dev.DeviceName}\" does not match any detected device.\n" +
                        $"Detected devices:\n{detected}");
                    continue;
                }

                foreach (var axis in dev.Axes)
                {
                    if (GetAxisOffset(axis.Axis) < 0)
                        warnings.Add(
                            $"[{dev.DeviceName}] Unknown Axis \"{axis.Axis}\" — this axis is ignored.\n" +
                            $"  Valid values: {string.Join(", ", StandardAxes)}");

                    if (axis.Notches is { Enabled: true } n)
                    {
                        if (n.Positions.Count == 0)
                            warnings.Add($"[{dev.DeviceName}/{axis.Axis}] Notches are enabled but Positions is empty.");
                        if (n.Positions.Any(p => p < 0 || p > 100))
                            warnings.Add($"[{dev.DeviceName}/{axis.Axis}] Notch Positions must be within 0–100.");
                        if (n.Strength is < 0 or > DiMax)
                            warnings.Add($"[{dev.DeviceName}/{axis.Axis}] Notch Strength must be within 0–{DiMax}.");
                        else if (n.Strength == 0)
                            warnings.Add($"[{dev.DeviceName}/{axis.Axis}] Notches are enabled but Strength is 0 — they will not be felt.");
                        if (n.SnapZoneWidth <= 0)
                            warnings.Add($"[{dev.DeviceName}/{axis.Axis}] Notch SnapZoneWidth must be greater than 0 — notches would never engage.");

                        // Decimal positions make near-duplicates possible: two positions that round
                        // to the same DirectInput offset are one notch as far as the device is concerned.
                        var sorted = n.Positions.OrderBy(p => p).ToList();
                        for (int i = 1; i < sorted.Count; i++)
                        {
                            if (Math.Round((sorted[i] - sorted[i - 1]) * 200.0) < 1)
                            {
                                warnings.Add(
                                    $"[{dev.DeviceName}/{axis.Axis}] Notch positions {sorted[i - 1]} and {sorted[i]} " +
                                    "are closer than DirectInput can resolve and act as a single notch.");
                                break;
                            }
                        }
                    }

                    if (axis.Resistance is { Enabled: true } r)
                    {
                        if (r.Strength is < 0 or > DiMax)
                            warnings.Add($"[{dev.DeviceName}/{axis.Axis}] Resistance Strength must be within 0–{DiMax}.");
                        else if (r.Strength == 0)
                            warnings.Add(
                                $"[{dev.DeviceName}/{axis.Axis}] Resistance is enabled but Strength is 0 — it is treated as disabled.\n" +
                                "  Set Enabled to false to say so explicitly.");
                    }
                }
            }

            return warnings;
        }

        /// <summary>Drops the loaded FFB config and stops force feedback if it is running.</summary>
        private void ClearFfbConfig()
        {
            if (_ffbConfig == null && string.IsNullOrEmpty(_currentFfbConfigFile)) return;

            _ffbConfig = null;
            _currentFfbConfigFile = "";
            UpdateStatusBar();
            RestartFfbIfRunning();
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

            // Button config first. Saving it settles _currentButtonConfigFile, which the lever
            // config then writes out as its association; the other order writes the lever config
            // with the association it had beforehand, so on a first-ever save the pair came out
            // unlinked and only corrected itself on the next save.
            // A cancelled or failed button save is not fatal — the lever config is still worth
            // writing, it just records no button association.
            TrySaveButtonConfig();

            if (TrySaveLeverConfig())
                SetStatus("Saved lever and button configs");
        }

        /// <summary>
        /// Saves the lever config, prompting for a filename when it does not have one yet.
        /// Returns false if the user cancelled or the save failed (the error is already reported).
        /// </summary>
        private bool TrySaveLeverConfig()
        {
            string path;
            if (!string.IsNullOrEmpty(_currentLeverConfigFile))
            {
                path = Path.Combine(_configDir, _currentLeverConfigFile);
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
                if (dialog.ShowDialog() != true) return false;
                path = dialog.FileName;
            }

            try
            {
                SaveLeverConfigTo(path);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save lever config:\n{ex.Message}",
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Saves the button config, prompting for a filename when it does not have one yet.
        /// Returns false if the user cancelled or the save failed (the error is already reported).
        /// </summary>
        private bool TrySaveButtonConfig()
        {
            string path;
            if (!string.IsNullOrEmpty(_currentButtonConfigFile))
            {
                path = Path.Combine(_configDir, _currentButtonConfigFile);
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
                if (dialog.ShowDialog() != true) return false;
                path = dialog.FileName;
            }

            try
            {
                SaveButtonConfigTo(path);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save button config:\n{ex.Message}",
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // ─── Save helpers ─────────────────────────────────────────────────────

        private void SaveLeverConfigTo(string filePath)
        {
            var file = new LeverConfigFile
            {
                AssociatedButtonConfigFile = _currentButtonConfigFile,
                AssociatedFfbConfigFile    = _currentFfbConfigFile,
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

        private void LoadFfbConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureHasConfigDir()) return;

            var dialog = new OpenFileDialog
            {
                Filter = "FFB config (*.json)|*.json|All files (*.*)|*.*",
                Title = "Load FFB Configuration",
                InitialDirectory = _configDir
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                LoadFfbConfig(dialog.FileName);
                SaveSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load FFB config:\n{ex.Message}",
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ChangeConfigDir_Click(object sender, RoutedEventArgs e)
        {
            PromptForConfigDirectory(isFirstTime: false);
        }

        private void ReloadConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureHasConfigDir()) return;

            bool reloaded = false;
            bool haveLeverConfig = !string.IsNullOrEmpty(_currentLeverConfigFile);

            if (haveLeverConfig)
            {
                string path = Path.Combine(_configDir, _currentLeverConfigFile);
                if (!File.Exists(path))
                {
                    MessageBox.Show($"Lever config '{_currentLeverConfigFile}' not found in config directory.",
                        "Reload Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    // Re-reads the button and FFB configs too. keepUnassociatedConfigs makes that
                    // independent of whether this lever config names them: Reload re-reads what is
                    // loaded now, so an FFB config loaded standalone (or one loaded before the
                    // lever config was last saved) survives instead of being cleared.
                    LoadLeverConfig(path, keepUnassociatedConfigs: true);
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

            // FFB config with no lever config in play. When there is a lever config the branch
            // above has already re-read the FFB config, so doing it here too would tear force
            // feedback down and restart it twice.
            if (!haveLeverConfig && !string.IsNullOrEmpty(_currentFfbConfigFile))
            {
                string ffbPath = Path.Combine(_configDir, _currentFfbConfigFile);
                if (File.Exists(ffbPath))
                {
                    try { LoadFfbConfig(ffbPath); reloaded = true; }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to reload FFB config:\n{ex.Message}",
                            "Reload Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
            }

            SetStatus(reloaded ? "Config reloaded" : "No config loaded to reload");
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
            _deviceWatchCts?.Cancel();
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
            StartDeviceWatch();
        }

        // ─── Device watch: pressing a button selects that joystick ────────────

        private void StartDeviceWatch()
        {
            if (_directInput == null || _joystickDevices.Count == 0) return;

            var cts = new CancellationTokenSource();
            _deviceWatchCts = cts;

            // Snapshot the device list — the loop must not read the UI-thread collection.
            var devices = _joystickDevices.ToList();
            Task.Run(() => DeviceWatchLoop(devices, cts.Token));
        }

        /// <summary>
        /// Polls every attached joystick on its own non-exclusive handle and moves the joystick
        /// selection to whichever device the user presses a button on. Suppressed while emulation
        /// is running, where button presses are gameplay rather than a device choice.
        /// </summary>
        private async Task DeviceWatchLoop(List<DeviceInstance> devices, CancellationToken token)
        {
            if (_directInput == null) return;

            var handles = new List<(int index, Joystick js, bool[]? prev)>();

            for (int i = 0; i < devices.Count; i++)
            {
                try
                {
                    var js = new Joystick(_directInput, devices[i].InstanceGuid);
                    js.Properties.BufferSize = 128;
                    // Same cooperative level as the emulation loop: background so presses are seen
                    // when another window has the focus, non-exclusive so FFB can still go exclusive.
                    js.SetCooperativeLevel(_windowHandle,
                        CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                    js.Acquire();
                    handles.Add((i, js, null));
                }
                catch { }
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    for (int h = 0; h < handles.Count; h++)
                    {
                        var (index, js, prev) = handles[h];

                        bool[] buttons;
                        try
                        {
                            js.Poll();
                            buttons = js.GetCurrentState().Buttons;
                        }
                        catch
                        {
                            // Lost the device — drop the baseline and try to get it back.
                            handles[h] = (index, js, null);
                            try { js.Acquire(); } catch { }
                            continue;
                        }

                        if (buttons.Length == 0) continue;

                        handles[h] = (index, js, (bool[])buttons.Clone());
                        if (prev == null) continue; // first frame is only a baseline

                        // During emulation a button press is gameplay, not a device choice; while
                        // the mapping window is open that window does the selecting.
                        if (_isEmulationActive || _mappingWindowOpen) continue;

                        for (int b = 0; b < Math.Min(buttons.Length, prev.Length); b++)
                        {
                            if (!buttons[b] || prev[b]) continue; // want a rising edge

                            // Re-selecting the same device would needlessly restart the axis
                            // monitor and rewrite the settings file, so only switch on a change.
                            _ = Dispatcher.BeginInvoke(() =>
                            {
                                if (JoystickComboBox.SelectedIndex != index &&
                                    index < JoystickComboBox.Items.Count)
                                    JoystickComboBox.SelectedIndex = index;
                            });
                            break;
                        }
                    }

                    await Task.Delay(30, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                foreach (var (_, js, _) in handles)
                {
                    try { js.Unacquire(); js.Dispose(); } catch { }
                }
            }
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
            if (idx < 0 || idx >= _joystickDevices.Count || _directInput == null) return;

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
            if (_directInput == null)
            {
                MessageBox.Show("DirectInput is not available.",
                    "Configure Keys", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // While mapping, the buttons being pressed must not also trigger their current keys.
            _mappingWindowOpen = true;
            ReleaseAllHeldKeys();

            try
            {
                var win = new ButtonMappingWindow(_directInput, _joystickDevices, _configuration,
                                                  _selectedDevice?.InstanceName) { Owner = this };
                win.ShowDialog();
            }
            finally
            {
                _mappingWindowOpen = false;
            }
        }

        // ─── Axis Monitor ─────────────────────────────────────────────────────

        private void StartAxisMonitor()
        {
            var cts = new CancellationTokenSource();
            _monitorCts = cts;
            Task.Run(() => AxisMonitorLoop(cts.Token));
        }

        private async Task AxisMonitorLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var js = _selectedJoystick;
                    if (js != null)
                    {
                        try
                        {
                            js.Poll();
                            var state = js.GetCurrentState();
                            double value = NormalizeAxisValuePrecise(GetAxisValue(state, _selectedAxis));
                            // Two decimals: thresholds may be decimal, so the monitor has to be
                            // able to show a position you could actually write into a config.
                            string text = value.ToString("0.00", CultureInfo.InvariantCulture);
                            Dispatcher.Invoke(() => AxisValueText.Text = text);
                        }
                        catch { }
                    }

                    await Task.Delay(16, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }

            // Best effort — the dispatcher is already gone if the window has closed.
            try { Dispatcher.Invoke(() => AxisValueText.Text = "---"); } catch { }
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
            _axisTravel.Clear();
            lock (_heldKeysLock) { _heldKeyRefs.Clear(); }
            _leverQueues.Clear();

            ToggleEmulationButton.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            ToggleEmulationButton.Content = "STOP EMULATION";
            SetStatus("Emulation ACTIVE");

            Task.Run(() => EmulationLoop(_emulationCts.Token));
            Task.Run(() => KeySenderLoop(_emulationCts.Token));
            StartFfb();
        }

        private void StopEmulation()
        {
            _isEmulationActive = false;
            StopFfb();
            // Not disposed: the emulation loops may still be awaiting on this token.
            _emulationCts?.Cancel();
            _leverQueues.Clear();

            ReleaseAllHeldKeys();
            _activeHoldEventIndices.Clear();

            Dispatcher.Invoke(() =>
            {
                ToggleEmulationButton.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                ToggleEmulationButton.Content = "START EMULATION";
                SetStatus("Emulation stopped");
            });
        }

        /// <summary>Sends key-up for every key the emulation is currently holding down.</summary>
        private void ReleaseAllHeldKeys()
        {
            List<KeyToken> toRelease;
            lock (_heldKeysLock)
            {
                toRelease = _heldKeyRefs.Keys.ToList();
                _heldKeyRefs.Clear();
            }
            foreach (var key in toRelease) SendKeyUp(key);
        }

        private async Task EmulationLoop(CancellationToken token)
        {
            if (_directInput == null) return;

            var joysticks = new Dictionary<string, Joystick>();

            foreach (var device in _joystickDevices)
            {
                try
                {
                    var js = new Joystick(_directInput, device.InstanceGuid);
                    js.Properties.BufferSize = 128;
                    // Background so axes and buttons keep being read once the game has the focus;
                    // NonExclusive so the FFB loop can still take the device exclusively.
                    js.SetCooperativeLevel(_windowHandle,
                        CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                    js.Acquire();
                    joysticks[device.InstanceName] = js;
                    _previousAxisValues[device.InstanceName] = new Dictionary<string, double>();
                    _activeThresholds[device.InstanceName] = new Dictionary<string, HashSet<ThresholdKey>>();
                    _activeHoldEventIndices[device.InstanceName] = new Dictionary<string, HashSet<int>>();
                    _axisTravel[device.InstanceName] = new Dictionary<string, AxisTravel>();
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

        /// <summary>
        /// The single consumer of every lever queue. It takes one action from each lever per
        /// pass, so a lever with a long backlog never blocks another lever - while the order
        /// within any one lever stays exactly as that lever produced it.
        /// <para>One sender, never one per lever: <c>SendInput</c> calls from concurrent
        /// senders would interleave, and a stroke that needs a modifier could be split so the
        /// target window reads the wrong character.</para>
        /// </summary>
        private async Task KeySenderLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                bool sentAny = false;

                // Snapshotted: a lever that first fires mid-pass joins on the next one rather
                // than mutating the walk.
                foreach (var queue in _leverQueues.Values.ToArray())
                {
                    if (token.IsCancellationRequested) break;
                    if (!queue.TryDequeue(out var action)) continue;

                    sentAny = true;

                    // A dropped action is not paced, so a stale backlog drains in one go
                    // rather than one entry per interval.
                    if (!ExecuteKeyAction(action)) continue;

                    int interval = Math.Max(1, _appSettings.MaxKeypressIntervalMs);
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }

                if (!sentAny) await Task.Delay(5, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Performs one queued action. Returns false when the action was discarded instead.
        /// <para>Only a press may be dropped for age. A hold or a release runs however long it
        /// waited: discarding a release would leave its key physically down with nothing left
        /// in the queue to lift it.</para>
        /// </summary>
        private bool ExecuteKeyAction(KeyAction action)
        {
            if (action.Kind == KeyActionKind.Press)
            {
                // A press that has waited this long belongs to a lever movement the user
                // finished seconds ago; sending it now is a stray keystroke in whatever they
                // are doing instead.
                int maxAge = _appSettings.PressQueueMaxAgeMs;
                if (maxAge > 0 && Environment.TickCount64 - action.QueuedAt > maxAge) return false;
            }

            switch (action.Kind)
            {
                case KeyActionKind.Hold:    TryHoldStroke(action.Stroke);              break;
                case KeyActionKind.Release: ReleaseStroke(action.Stroke);              break;
                default:                    SendKeyPress(action.Stroke, action.HoldMs); break;
            }
            return true;
        }

        /// <summary>Appends one action to a lever's queue, creating the queue on first use.</summary>
        private void EnqueueKeyAction(LeverId lever, KeyActionKind kind, KeyStroke stroke, int holdMs = 0)
        {
            var queue = _leverQueues.GetOrAdd(lever, _ => new ConcurrentQueue<KeyAction>());
            queue.Enqueue(new KeyAction(kind, stroke, holdMs, Environment.TickCount64));
        }

        // ─── Force Feedback ───────────────────────────────────────────────────

        /// <summary>Starts the force feedback loop. No-op when no FFB config is loaded.</summary>
        private void StartFfb()
        {
            if (_ffbConfig == null || _ffbConfig.Devices.Count == 0) return;

            // Captured locally so a concurrent StopFfb clearing the field cannot race the task start.
            var cts  = new CancellationTokenSource();
            _ffbCts  = cts;
            _ffbTask = Task.Run(() => FfbLoop(cts.Token));
        }

        /// <summary>
        /// Stops the force feedback loop and waits for it to release its exclusive device
        /// handles, so a subsequent <see cref="StartFfb"/> can re-acquire them.
        /// </summary>
        private void StopFfb()
        {
            _ffbCts?.Cancel();

            // FfbLoop awaits with ConfigureAwait(false) throughout, so waiting here from the
            // UI thread cannot deadlock on the dispatcher.
            bool stopped = true;
            try { stopped = _ffbTask?.Wait(TimeSpan.FromSeconds(2)) ?? true; } catch { }

            // On a timeout the old loop still holds its devices exclusively, so the StartFfb that
            // follows a reload cannot re-acquire them. Unreported, that reads as "reloading the
            // FFB config randomly stops force feedback", so it gets said out loud.
            if (!stopped)
                SetStatus("FFB: the previous force-feedback loop did not stop in time — restart emulation");

            _ffbTask = null;
            _ffbCts  = null;   // not disposed: the loop may still hold the token briefly
        }

        /// <summary>Re-applies a changed FFB config to a running emulation.</summary>
        private void RestartFfbIfRunning()
        {
            if (!_isEmulationActive) return;
            StopFfb();
            StartFfb();
        }

        /// <summary>
        /// Acquires a device for force feedback output.
        /// <para>Two ordering rules from DirectInput are load-bearing here:</para>
        /// <list type="bullet">
        /// <item>Exclusive access is mandatory for FFB output.</item>
        /// <item><c>DIPROP_AUTOCENTER</c> can only be written while the device is <b>un</b>acquired,
        /// so it must be set before <see cref="Joystick.Acquire"/> — setting it afterwards fails
        /// with DIERR_ACQUIRED and takes the whole device down with it.</item>
        /// </list>
        /// Background is preferred so effects survive the game taking the focus, but not every
        /// driver grants exclusive background access, so foreground is tried as a fallback.
        /// </summary>
        private static void AcquireForFfb(Joystick js, IntPtr windowHandle)
        {
            try
            {
                js.SetCooperativeLevel(windowHandle, CooperativeLevel.Exclusive | CooperativeLevel.Background);
                js.Properties.AutoCenter = false;
                js.Acquire();
                return;
            }
            catch
            {
                try { js.Unacquire(); } catch { }
            }

            js.SetCooperativeLevel(windowHandle, CooperativeLevel.Exclusive | CooperativeLevel.Foreground);
            js.Properties.AutoCenter = false;
            js.Acquire();
        }

        private async Task FfbLoop(CancellationToken token)
        {
            var ffbConfig = _ffbConfig;
            if (_directInput == null || ffbConfig == null || ffbConfig.Devices.Count == 0) return;

            var ffbDevices = new List<FfbDeviceRuntime>();
            var ffbErrors  = new List<string>();

            // Kept apart from ffbErrors so a real failure always wins the single status-bar slot.
            var rangeWarnings = new List<string>();
            int drivenAxes    = 0;

            foreach (var devCfg in ffbConfig.Devices)
            {
                var deviceInstance = _joystickDevices.FirstOrDefault(d =>
                    d.InstanceName.Equals(devCfg.DeviceName, StringComparison.OrdinalIgnoreCase));
                if (deviceInstance == null) continue;

                // A device listed twice would leak the first exclusive handle.
                if (ffbDevices.Any(d => d.Name.Equals(devCfg.DeviceName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Joystick? js = null;
                try
                {
                    js = new Joystick(_directInput, deviceInstance.InstanceGuid);
                    js.Properties.BufferSize = 128;
                    AcquireForFfb(js, _windowHandle);
                }
                catch (Exception ex)
                {
                    // No force feedback support, or another application holds the device exclusively.
                    ffbErrors.Add($"{devCfg.DeviceName}: could not acquire for FFB — {ex.Message}");
                    try { js?.Unacquire(); } catch { }
                    try { js?.Dispose(); } catch { }
                    continue;
                }

                var axisRuntimes = new List<FfbAxisRuntime>();
                foreach (var axisCfg in devCfg.Axes)
                {
                    int offset = GetAxisOffset(axisCfg.Axis);
                    if (offset < 0) continue;

                    if (CheckAxisRange(js, $"{devCfg.DeviceName}/{axisCfg.Axis}", offset) is string rangeWarning)
                        rangeWarnings.Add(rangeWarning);

                    var runtime = new FfbAxisRuntime { Config = axisCfg };

                    if (axisCfg.Notches?.Enabled == true && axisCfg.Notches.Positions.Count > 0)
                    {
                        if (!SupportsEffect(js, EffectGuid.Spring))
                        {
                            ffbErrors.Add($"{devCfg.DeviceName}/{axisCfg.Axis}: the device has no Spring effect — notches cannot be driven");
                        }
                        else
                        {
                            var (effect, ep) = TryCreateNotchSpringEffect(js, offset);
                            runtime.NotchEffect = effect;
                            runtime.NotchEp     = ep;
                            if (effect == null)
                                ffbErrors.Add($"{devCfg.DeviceName}/{axisCfg.Axis}: device rejected the Spring (notch) effect");
                        }
                    }

                    // A friction effect with a zero coefficient produces no force, so an enabled
                    // resistance with Strength 0 means "off". Asking the device for it anyway can
                    // only take up an effect slot or fail, and on a device with no Friction effect
                    // at all that failure was reported as if force feedback were broken.
                    int resistance = axisCfg.Resistance?.Enabled == true
                        ? Clamp(axisCfg.Resistance.Strength, 0, DiMax)
                        : 0;

                    if (resistance > 0)
                    {
                        if (!SupportsEffect(js, EffectGuid.Friction))
                        {
                            ffbErrors.Add($"{devCfg.DeviceName}/{axisCfg.Axis}: the device has no Friction effect — resistance is ignored");
                        }
                        else
                        {
                            runtime.FrictionEffect = TryCreateFrictionEffect(js, offset, resistance);
                            if (runtime.FrictionEffect == null)
                                ffbErrors.Add($"{devCfg.DeviceName}/{axisCfg.Axis}: device rejected the Friction (resistance) effect");
                        }
                    }

                    if (runtime.NotchEffect == null && runtime.FrictionEffect == null) continue;

                    if (StartEffects(runtime)) drivenAxes++;
                    axisRuntimes.Add(runtime);
                }

                if (axisRuntimes.Count == 0)
                {
                    // Nothing to drive on this device — don't keep it exclusively acquired.
                    ReleaseFfbDevice(js);
                    continue;
                }

                ffbDevices.Add(new FfbDeviceRuntime(devCfg.DeviceName, js, axisRuntimes));
            }

            // Appended last so an outright effect failure is reported ahead of a scaling advisory.
            ffbErrors.AddRange(rangeWarnings);

            // What is running comes first, always. A config that drives one axis and asks for one
            // effect the hardware does not have is the normal case, and reporting only the failure
            // made a working force-feedback loop look like a dead one.
            string ffbState = ffbDevices.Count > 0
                ? $"Emulation ACTIVE — FFB on {ffbDevices.Count} device(s), {drivenAxes} axis/axes"
                : "Emulation ACTIVE — no FFB-capable device matched the config";

            SetStatus(ffbErrors.Count switch
            {
                0 => ffbState,
                1 => $"{ffbState} — {ffbErrors[0]}",
                _ => $"{ffbState} — {ffbErrors.Count} issues, first: {ffbErrors[0]}"
            });

            if (ffbDevices.Count == 0) return;

            try
            {
                string? reportedError = null;

                while (!token.IsCancellationRequested)
                {
                    foreach (var device in ffbDevices)
                        UpdateFfbDevice(device);

                    // Runtime failures would otherwise be invisible — the effect simply stops
                    // responding. Report each distinct one once.
                    string? error = ffbDevices
                        .SelectMany(d => d.Axes)
                        .Select(a => a.LastError)
                        .FirstOrDefault(e => e != null);

                    if (error != reportedError)
                    {
                        reportedError = error;
                        if (error != null) SetStatus("FFB: " + error);
                    }

                    await Task.Delay(10, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                foreach (var device in ffbDevices)
                {
                    foreach (var runtime in device.Axes)
                    {
                        try { runtime.NotchEffect?.Stop();    runtime.NotchEffect?.Dispose(); }    catch { }
                        try { runtime.FrictionEffect?.Stop(); runtime.FrictionEffect?.Dispose(); } catch { }
                    }
                    ReleaseFfbDevice(device.Joystick);
                }
            }
        }

        /// <summary>
        /// Releases an FFB device, restoring auto-centering on the way out.
        /// The unacquire must come first: auto-centering cannot be written while acquired, so
        /// the reverse order silently leaves the device limp after emulation stops.
        /// </summary>
        private static void ReleaseFfbDevice(Joystick js)
        {
            try { js.Unacquire(); } catch { }
            try { js.Properties.AutoCenter = true; } catch { }
            try { js.Dispose(); } catch { }
        }

        /// <summary>
        /// Polls one FFB device and retunes its notch springs to the current axis position.
        /// Recovers the acquisition (and restarts the effects) if the device was lost.
        /// </summary>
        private static void UpdateFfbDevice(FfbDeviceRuntime device)
        {
            JoystickState state;
            try
            {
                device.Joystick.Poll();
                state = device.Joystick.GetCurrentState();
            }
            catch
            {
                // Device lost — e.g. unplugged, or a power-management wake. Re-acquiring drops
                // all downloaded effects, so they have to be started again.
                try
                {
                    device.Joystick.Unacquire();          // AutoCenter is only writable when unacquired
                    device.Joystick.Properties.AutoCenter = false;
                    device.Joystick.Acquire();
                    foreach (var runtime in device.Axes) StartEffects(runtime);
                }
                catch { }
                return;
            }

            foreach (var runtime in device.Axes)
            {
                var notches = runtime.Config.Notches;
                if (runtime.NotchEffect == null || runtime.NotchEp == null || notches == null) continue;

                double normalized = NormalizeAxisValuePrecise(GetAxisValue(state, runtime.Config.Axis));
                var (diOffset, coeff) = ComputeNotchSpringParams(normalized, notches);

                // Skip the round trip to the device when nothing changed — SetParameters is
                // relatively expensive and this runs every 10 ms.
                if (diOffset == runtime.LastNotchOffset && coeff == runtime.LastNotchCoefficient) continue;

                try
                {
                    runtime.NotchEp.Parameters = new ConditionSet
                    {
                        Conditions = new[] { new DxCondition
                        {
                            Offset = diOffset,
                            PositiveCoefficient = coeff, NegativeCoefficient = coeff,
                            PositiveSaturation  = DiMax, NegativeSaturation  = DiMax, DeadBand = 0
                        }}
                    };

                    // Only the condition block may be re-sent. The single-argument SetParameters
                    // overload passes EffectParameterFlags.All, which includes Axes and Direction —
                    // DirectInput rejects changing those on a downloaded effect, so the update
                    // would fail every time and the notch would never move.
                    runtime.NotchEffect.SetParameters(runtime.NotchEp,
                        EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);

                    runtime.LastNotchOffset      = diOffset;
                    runtime.LastNotchCoefficient = coeff;
                    runtime.LastError            = null;
                }
                catch (Exception ex)
                {
                    runtime.LastError = $"{runtime.Config.Axis} notch update failed: {ex.Message}";
                }
            }
        }

        /// <summary>Starts an axis' effects. Returns true if at least one is now running.</summary>
        private static bool StartEffects(FfbAxisRuntime runtime)
        {
            bool any = false;
            try { runtime.NotchEffect?.Start();    any |= runtime.NotchEffect    != null; } catch { }
            try { runtime.FrictionEffect?.Start(); any |= runtime.FrictionEffect != null; } catch { }

            // Force a re-send of the spring parameters after a restart.
            runtime.LastNotchOffset      = int.MinValue;
            runtime.LastNotchCoefficient = int.MinValue;
            return any;
        }

        /// <summary>
        /// True when the device advertises the given effect type. This separates a config asking
        /// for an effect the hardware simply does not have — a config mismatch, worth naming as
        /// such — from an effect the device claims and then refuses, which is a real failure.
        /// <para>Unknown counts as supported, so a device that will not enumerate its effects
        /// still gets the creation attempt and the old behaviour.</para>
        /// <para>Note this is per device, not per axis: a wheel that has Spring only on the
        /// steering axis still advertises Spring, so creating it on another axis can still be
        /// rejected below.</para>
        /// </summary>
        private static bool SupportsEffect(Joystick js, Guid effectGuid)
        {
            try { return js.GetEffects().Any(e => e.Guid == effectGuid); }
            catch { return true; }
        }

        private static (DxEffect? effect, EffectParameters? ep) TryCreateNotchSpringEffect(Joystick js, int axisOffset)
        {
            try
            {
                var ep = new EffectParameters
                {
                    Duration = -1,   // INFINITE
                    SamplePeriod = 0,
                    Gain = 10000,
                    TriggerButton = -1,
                    TriggerRepeatInterval = 0,
                    Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                    Axes = new[] { axisOffset },
                    Directions = new[] { 0 },
                    Parameters = new ConditionSet
                    {
                        Conditions = new[] { new DxCondition
                        {
                            Offset = 0, PositiveCoefficient = 0, NegativeCoefficient = 0,
                            PositiveSaturation = DiMax, NegativeSaturation = DiMax, DeadBand = 0
                        }}
                    }
                };
                return (new DxEffect(js, EffectGuid.Spring, ep), ep);
            }
            catch { return (null, null); }
        }

        private static DxEffect? TryCreateFrictionEffect(Joystick js, int axisOffset, int strength)
        {
            try
            {
                var ep = new EffectParameters
                {
                    Duration = -1,   // INFINITE
                    SamplePeriod = 0,
                    Gain = 10000,
                    TriggerButton = -1,
                    TriggerRepeatInterval = 0,
                    Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                    Axes = new[] { axisOffset },
                    Directions = new[] { 0 },
                    Parameters = new ConditionSet
                    {
                        Conditions = new[]
                        {
                            new DxCondition
                            {
                                Offset = 0,
                                PositiveCoefficient = strength,
                                NegativeCoefficient = strength,
                                PositiveSaturation = DiMax,
                                NegativeSaturation = DiMax,
                                DeadBand = 0
                            }
                        }
                    }
                };
                return new DxEffect(js, EffectGuid.Friction, ep);
            }
            catch { return null; }
        }

        /// <summary>DI_FFNOMINALMAX — the device-rated maximum for DirectInput force units.</summary>
        private const int DiMax = 10000;

        /// <summary>
        /// Picks the notch nearest to the current position and returns the spring parameters for it.
        /// The spring centre is expressed in DirectInput's normalized axis space (-10000..+10000),
        /// so our 0–100 scale maps as <c>(pos - 50) * 200</c>.
        /// Outside every notch's pull range the coefficient is zero, i.e. the axis moves freely.
        /// </summary>
        private static (int diOffset, int coefficient) ComputeNotchSpringParams(double normalized, FfbNotchConfig cfg)
        {
            if (cfg.Positions.Count == 0) return (0, 0);

            double bestDelta = double.PositiveInfinity;
            double bestPos   = 0;

            foreach (double pos in cfg.Positions)
            {
                double delta = normalized - pos;
                if (Math.Abs(delta) < Math.Abs(bestDelta)) { bestDelta = delta; bestPos = pos; }
            }

            // Picking the nearest notch guarantees |bestDelta| <= half the gap to the neighbour
            // on this side, so this single test yields the per-gap pull rule for free: gaps
            // narrower than 2 * SnapZoneWidth can never fail it (the axis is pulled to whichever
            // notch is nearest, with no free travel), while wider gaps leave the middle free.
            // Because the gap is whatever the local neighbour distance is, unevenly spaced
            // notches behave correctly without any extra bookkeeping.
            if (Math.Abs(bestDelta) > cfg.SnapZoneWidth) return (0, 0);

            // Out-of-range values make SetParameters fail, which would silently kill the notch.
            int offset = Clamp((int)Math.Round((Clamp(bestPos, 0.0, 100.0) - 50.0) * 200.0), -DiMax, DiMax);
            return (offset, Clamp(cfg.Strength, 0, DiMax));
        }

        private static int Clamp(int value, int min, int max) =>
            value < min ? min : value > max ? max : value;

        private static double Clamp(double value, double min, double max) =>
            value < min ? min : value > max ? max : value;

        /// <summary>
        /// Returns a warning if an axis does not report the raw 0–65535 range that
        /// <see cref="NormalizeAxisValuePrecise"/> assumes, otherwise null. Purely diagnostic —
        /// the range is never written, so this cannot disturb a working device. It matters far
        /// more with dense notches: at ~1.19 unit spacing a range mismatch shifts and stretches
        /// every detent, which is hard to recognise as a scaling problem on the hardware.
        /// </summary>
        private static string? CheckAxisRange(Joystick js, string axisName, int axisOffset)
        {
            try
            {
                var obj = js.GetObjects(DeviceObjectTypeFlags.Axis)
                            .FirstOrDefault(o => o.Offset == axisOffset);
                if (obj == null) return null;

                var range = js.GetObjectPropertiesById(obj.ObjectId).Range;
                if (range.Minimum == 0 && range.Maximum == 65535) return null;

                return $"{axisName} reports raw range {range.Minimum}..{range.Maximum}, not 0..65535 — " +
                       "notch positions will be misplaced.";
            }
            catch { return null; }   // diagnostics must never break FFB startup
        }

        private static int GetAxisOffset(string axisName) =>
            axisName.ToUpperInvariant() switch
            {
                "X"                     => (int)JoystickOffset.X,
                "Y"                     => (int)JoystickOffset.Y,
                "Z"                     => (int)JoystickOffset.Z,
                "ROTATIONX" or "RX"     => (int)JoystickOffset.RotationX,
                "ROTATIONY" or "RY"     => (int)JoystickOffset.RotationY,
                "ROTATIONZ" or "RZ"     => (int)JoystickOffset.RotationZ,
                "SLIDER0"   or "SLIDER" => (int)JoystickOffset.Sliders0,
                "SLIDER1"               => (int)JoystickOffset.Sliders1,
                _                       => -1
            };

        /// <summary>One exclusively acquired FFB device and the axes being driven on it.</summary>
        private sealed record FfbDeviceRuntime(string Name, Joystick Joystick, List<FfbAxisRuntime> Axes);

        private sealed class FfbAxisRuntime
        {
            public required FfbAxisConfig Config { get; init; }
            public DxEffect?         NotchEffect    { get; set; }
            public EffectParameters? NotchEp        { get; set; }
            public DxEffect?         FrictionEffect { get; set; }

            /// <summary>Last spring parameters sent to the device, so unchanged ones can be skipped.</summary>
            public int LastNotchOffset      { get; set; } = int.MinValue;
            public int LastNotchCoefficient { get; set; } = int.MinValue;

            /// <summary>Most recent runtime failure, surfaced in the status line. Null when healthy.</summary>
            public string? LastError { get; set; }
        }

        // ─── Button State Processing ──────────────────────────────────────────

        private void ProcessButtonStates(string joystickName, JoystickState state, JoystickConfig config)
        {
            var current = state.Buttons;

            if (_mappingWindowOpen)
            {
                // Mapping window is open — keep tracking state, but emit nothing.
                _previousButtonStates[joystickName] = (bool[])current.Clone();
                return;
            }

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

                var stroke = ResolveKeyStroke(mapping.Key);
                if (stroke.IsNone) continue;

                var mods = (mapping.Modifiers ?? new List<string>())
                    .Select(ParseKey).Where(k => k != VirtualKey.None)
                    .Select(k => new KeyToken(k)).ToList();

                if (current[i])
                {
                    foreach (var m in mods) TryHoldKey(m);
                    TryHoldStroke(stroke);
                }
                else
                {
                    ReleaseStroke(stroke);
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

            if (!_axisTravel.TryGetValue(joystickName, out var travelMap))
            {
                travelMap = new Dictionary<string, AxisTravel>();
                _axisTravel[joystickName] = travelMap;
            }

            foreach (var axisConfig in config.Axes)
            {
                double current = NormalizeAxisValuePrecise(GetAxisValue(state, axisConfig.Axis));
                var    lever   = new LeverId(joystickName, axisConfig.Axis);

                if (!prevValues.TryGetValue(axisConfig.Axis, out double previous))
                {
                    prevValues[axisConfig.Axis] = current;
                    activeThresh[axisConfig.Axis] = new HashSet<ThresholdKey>();
                    holdAxisMap[axisConfig.Axis] = new HashSet<int>();
                    travelMap[axisConfig.Axis] = new AxisTravel { Extreme = current };
                    continue;
                }

                // Ahead of this poll's events, so a reversal clears the backlog that the lever
                // built up on the way out but keeps the presses the reversal itself produces.
                if (travelMap.TryGetValue(axisConfig.Axis, out var travel))
                    TrackAxisTravel(lever, travel, current);

                var active = activeThresh[axisConfig.Axis];

                if (!holdAxisMap.TryGetValue(axisConfig.Axis, out var activeHoldIndices))
                {
                    activeHoldIndices = new HashSet<int>();
                    holdAxisMap[axisConfig.Axis] = activeHoldIndices;
                }

                for (int i = 0; i < axisConfig.Events.Count; i++)
                {
                    var evt = axisConfig.Events[i];
                    bool ranged = evt.PressType.Equals("HOLD", StringComparison.OrdinalIgnoreCase) &&
                                  evt.HoldRanges is { Length: > 0 };

                    if (ranged)
                        ProcessHoldRangeEvent(lever, i, evt, current, previous, activeHoldIndices);

                    // An event may carry both forms — e.g. "Threshold": ["30-40", 55] — so the
                    // plain values are still handled when ranges were processed above.
                    if (!ranged || evt.Thresholds.Length > 0)
                        ProcessAxisEvent(lever, evt, current, previous, active);
                }

                prevValues[axisConfig.Axis] = current;
            }
        }

        /// <summary>
        /// Follows one axis and cuts the queued presses off when the lever reverses.
        /// <para><see cref="AxisTravel.Extreme"/> is the furthest point reached in the current
        /// direction, so the test is "how far has the lever come back from where it turned
        /// around" rather than "how far did it move since the last poll": a slow sweep in
        /// sub-unit steps is never mistaken for a reversal, and jitter at the end of travel does
        /// not become one either.</para>
        /// <para>A lever swept across a dense set of thresholds queues presses faster than
        /// MaxKeypressIntervalMs can send them. Sweeping back then used to replay that whole
        /// backlog in the old direction before the new presses were reached — the keys ran on
        /// after the lever had stopped, and in the wrong direction. Dropping the backlog is the
        /// right answer because those presses no longer describe where the lever is.</para>
        /// </summary>
        private void TrackAxisTravel(LeverId lever, AxisTravel travel, double current)
        {
            double units = _appSettings.DirectionChangeUnits;
            if (units <= 0) return;   // cut-off disabled

            double delta = current - travel.Extreme;
            int    sign  = delta > 0 ? 1 : delta < 0 ? -1 : 0;

            if (sign == 0) return;

            if (sign == travel.Direction)
            {
                travel.Extreme = current;   // still going the same way — move the mark out
                return;
            }

            if (Math.Abs(delta) < units) return;   // not far enough back to count as a reversal

            // Direction 0 is the first movement of the run: it establishes a direction, there is
            // no earlier one for it to reverse.
            bool reversed    = travel.Direction != 0;
            travel.Direction = sign;
            travel.Extreme   = current;

            if (reversed) DiscardQueuedPresses(lever, "lever direction changed");
        }

        /// <summary>
        /// Throws away the presses still waiting on one lever, and says so — silently dropping
        /// keystrokes would be indistinguishable from the emulation missing them.
        /// <para>Holds and releases are put back in the order they were queued. Discarding a
        /// release would leave its key physically down with nothing left in the queue to lift
        /// it, so the queue is never simply emptied.</para>
        /// </summary>
        private void DiscardQueuedPresses(LeverId lever, string reason)
        {
            if (!_leverQueues.TryGetValue(lever, out var queue) || queue.IsEmpty) return;

            var keep    = new List<KeyAction>();
            int dropped = 0;

            while (queue.TryDequeue(out var action))
            {
                if (action.Kind == KeyActionKind.Press) dropped++;
                else keep.Add(action);
            }
            foreach (var action in keep) queue.Enqueue(action);

            if (dropped > 0)
                SetStatus($"Dropped {dropped} queued keypress(es) — {reason}");
        }

        /// <summary>Identifies one lever — a single axis of a single joystick.</summary>
        private readonly record struct LeverId(string Joystick, string Axis);

        /// <summary>What a queued keyboard action does when the sender reaches it.</summary>
        private enum KeyActionKind
        {
            /// <summary>A complete press: key down, hold, key up.</summary>
            Press,
            /// <summary>Takes a reference on the key and puts it down if it was not already.</summary>
            Hold,
            /// <summary>Drops a reference and lifts the key once none are left.</summary>
            Release
        }

        /// <summary>
        /// One keyboard action waiting its turn on a lever's queue. Holds and releases queue
        /// alongside presses so they cannot overtake each other.
        /// </summary>
        /// <param name="HoldMs">How long a <see cref="KeyActionKind.Press"/> stays down. Unused
        /// by holds and releases.</param>
        /// <param name="QueuedAt">Tick count when the lever asked for this, used to age presses out.</param>
        private readonly record struct KeyAction(
            KeyActionKind Kind, KeyStroke Stroke, int HoldMs, long QueuedAt);

        /// <summary>Direction of travel of one axis, and the furthest point reached in it.</summary>
        private sealed class AxisTravel
        {
            /// <summary>-1 falling, +1 rising, 0 until the axis has moved far enough to have one.</summary>
            public int    Direction { get; set; }
            public double Extreme   { get; set; }
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
        private void ProcessHoldRangeEvent(LeverId lever, int evtIndex, AxisEvent evt, double current,
            double previous, HashSet<int> activeHoldIndices)
        {
            if (evt.HoldRanges == null || evt.HoldRanges.Length == 0) return;

            var stroke = ResolveKeyStroke(evt.Key);
            if (stroke.IsNone) return;

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

                // Marked active on queueing rather than on the hold succeeding: the hold has
                // not run yet, so its MaxConcurrentKeys verdict is not known here. With the
                // default unlimited setting it cannot fail; under a limit, a hold refused at
                // send time is simply not re-attempted until the lever leaves the range.
                if (enteredCorrectly)
                {
                    EnqueueKeyAction(lever, KeyActionKind.Hold, stroke);
                    activeHoldIndices.Add(evtIndex);
                }
            }
            else if (!inRange && isActive)
            {
                activeHoldIndices.Remove(evtIndex);
                EnqueueKeyAction(lever, KeyActionKind.Release, stroke);
            }
        }

        private void ProcessAxisEvent(LeverId lever, AxisEvent evt, double current, double previous,
            HashSet<ThresholdKey> active)
        {
            var stroke = ResolveKeyStroke(evt.Key);
            if (stroke.IsNone) return;

            foreach (double t in evt.Thresholds)
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
                        EnqueueKeyAction(lever, KeyActionKind.Hold, stroke);
                        active.Add(tkId);
                    }
                    else if (!shouldHold && isActive)
                    {
                        EnqueueKeyAction(lever, KeyActionKind.Release, stroke);
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
                            if (!active.Contains(tkId))
                            {
                                EnqueueKeyAction(lever, KeyActionKind.Hold, stroke);
                                active.Add(tkId);
                            }
                            break;

                        case "KEYUP":
                        {
                            // Queued unconditionally: the matching hold may still be waiting its
                            // turn, so the physical key state is not the thing to test here.
                            // ReleaseHeldKey is a no-op when nothing holds the key, so an
                            // unpaired KEYUP costs nothing.
                            EnqueueKeyAction(lever, KeyActionKind.Release, stroke);

                            // Clear paired KEYDOWN entries so they can re-trigger next crossing
                            string keyUpper = evt.Key.ToUpperInvariant();
                            foreach (var entry in active.Where(tk => tk.Key == keyUpper && tk.PressType == "KEYDOWN").ToList())
                                active.Remove(entry);
                            active.Remove(tkId);
                            break;
                        }

                        default:
                        {
                            var (count, holdMs) = ParsePressType(evt.PressType);
                            int ms = holdMs >= 0 ? holdMs : _appSettings.DefaultPressMs;
                            for (int i = 0; i < count; i++)
                                EnqueueKeyAction(lever, KeyActionKind.Press, stroke, ms);
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

                        // Key — an unrecognised name makes the event silently do nothing at
                        // runtime. "None" does nothing too, but that is the point of it.
                        if (ResolveKeyStroke(evt.Key).IsNone && !IsExplicitNoKey(evt.Key))
                        {
                            int ln = FindFieldLineNumber(lines, "Key", evt.Key);
                            warnings.Add(
                                $"Line {ln}: Unknown Key \"{evt.Key}\" — this event will never fire.\n" +
                                $"  Valid values: A-Z, 0-9, F1-F24, SPACE, ENTER, TAB, ESC, BACKSPACE,\n" +
                                $"               DELETE, INSERT, HOME, END, PAGEUP, PAGEDOWN,\n" +
                                $"               UP, DOWN, LEFT, RIGHT, SHIFT/CTRL/ALT (+ L/R variants),\n" +
                                $"               NUMPAD0-9, NUMPAD+ - * / .,\n" +
                                $"               CAPSLOCK, NUMLOCK, SCROLLLOCK, PRINTSCREEN, PAUSE,\n" +
                                $"               APPS, LWIN, RWIN, CLEAR, HELP, CANCEL,\n" +
                                $"               any single character (! ? : % ~ & ^ ...), or U+XXXX,\n" +
                                $"               NONE to disable the event without deleting it");
                        }
                    }
                }
            }

            return warnings;
        }

        /// <summary>
        /// Locates the line holding <c>"fieldName": "value"</c>. The field name and the
        /// value must be matched as a pair — testing for them independently would report
        /// the wrong line whenever the value also occurs as some other field's value
        /// earlier in the file (e.g. an invalid PressType "d" matching a line whose Key
        /// happens to be "d").
        /// </summary>
        private static int FindFieldLineNumber(string[] lines, string fieldName, string value)
        {
            var pattern = new Regex(
                "\"" + Regex.Escape(fieldName) + "\"\\s*:\\s*\"" + Regex.Escape(value) + "\"",
                RegexOptions.IgnoreCase);

            for (int i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
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
        private bool TryHoldKey(KeyToken key)
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
        private void ReleaseHeldKey(KeyToken key)
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

        private bool IsKeyHeld(KeyToken key)
        {
            lock (_heldKeysLock)
                return _heldKeyRefs.TryGetValue(key, out int c) && c > 0;
        }

        /// <summary>
        /// Holds a resolved key together with the modifiers its character needs. Modifiers go
        /// down first: a window that reads the key-down would otherwise see the unshifted key
        /// and act on the wrong character. If the key itself cannot be held - MaxConcurrentKeys -
        /// the modifiers are rolled back rather than left stuck down.
        /// </summary>
        private bool TryHoldStroke(KeyStroke stroke)
        {
            var mods = StrokeModifiers(stroke);
            foreach (var m in mods) TryHoldKey(m);

            if (TryHoldKey(stroke.Token)) return true;

            for (int i = mods.Count - 1; i >= 0; i--) ReleaseHeldKey(mods[i]);
            return false;
        }

        /// <summary>Releases a stroke, key before modifiers - the mirror of TryHoldStroke.</summary>
        private void ReleaseStroke(KeyStroke stroke)
        {
            ReleaseHeldKey(stroke.Token);
            var mods = StrokeModifiers(stroke);
            for (int i = mods.Count - 1; i >= 0; i--) ReleaseHeldKey(mods[i]);
        }

        /// <summary>
        /// The modifier keys a stroke needs held, in press order.
        /// <para>Ctrl+Alt together is how a keyboard layout spells AltGr, so it is sent as the
        /// right Alt key: Windows raises the Ctrl itself, and a layout that puts a character on
        /// AltGr does not react to a literal left-Ctrl plus left-Alt.</para>
        /// </summary>
        private static List<KeyToken> StrokeModifiers(KeyStroke stroke)
        {
            var mods = new List<KeyToken>(3);

            if (stroke.Ctrl && stroke.Alt)
            {
                mods.Add(new KeyToken(VirtualKey.RightAlt));
            }
            else
            {
                if (stroke.Ctrl) mods.Add(new KeyToken(VirtualKey.LeftControl));
                if (stroke.Alt)  mods.Add(new KeyToken(VirtualKey.LeftAlt));
            }

            if (stroke.Shift) mods.Add(new KeyToken(VirtualKey.LeftShift));
            return mods;
        }

        /// <summary>
        /// Updates the status line from any thread. Background callers marshal asynchronously:
        /// the UI thread blocks on the FFB loop in <see cref="StopFfb"/>, so a background thread
        /// that waited for the dispatcher here would deadlock against it.
        /// </summary>
        private void SetStatus(string message)
        {
            if (StatusText.CheckAccess())
                StatusText.Text = message;
            else
                Dispatcher.BeginInvoke(() => StatusText.Text = message);
        }

        private void UpdateStatusBar()
        {
            string lever  = string.IsNullOrEmpty(_currentLeverConfigFile)  ? "—" : _currentLeverConfigFile;
            string button = string.IsNullOrEmpty(_currentButtonConfigFile) ? "—" : _currentButtonConfigFile;
            string ffb    = string.IsNullOrEmpty(_currentFfbConfigFile)    ? "—" : _currentFfbConfigFile;

            if (LeverFileText.CheckAccess())
            {
                LeverFileText.Text  = lever;
                ButtonFileText.Text = button;
                FfbFileText.Text    = ffb;
            }
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    LeverFileText.Text  = lever;
                    ButtonFileText.Text = button;
                    FfbFileText.Text    = ffb;
                });
            }
        }

        /// <summary>
        /// Axis position on the 0–100 scale without rounding. Everything that reads an axis uses
        /// this: force feedback needs the sub-unit resolution (43 notches across 0–50 sit ~1.19
        /// units apart, which whole numbers cannot express), and lever thresholds may themselves
        /// be decimal, so rounding the position first would quantise them back to whole units.
        /// Assumes the device reports a raw 0–65535 range — see <see cref="CheckAxisRange"/>.
        /// </summary>
        private static double NormalizeAxisValuePrecise(int raw) =>
            raw * 100.0 / 65535.0;

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
            _deviceWatchCts?.Cancel();
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
            ReadCommentHandling = JsonCommentHandling.Skip,

            // The default encoder escapes everything outside a conservative HTML-safe set, so
            // key names came out of the serializer mangled: a key named NUM+ was written
            // as NUM\u002B, and the same happened to < > & ` and the apostrophe. All of those are legal
            // JSON and read back correctly, but the config files are meant to be hand-edited,
            // and a key you cannot recognise in the file is a key you cannot edit.
            // The relaxed encoder still escapes the quote and the backslash, which is all JSON requires.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// Post-processes indented lever-config JSON into a more human-friendly compact form:
        ///   • Threshold number arrays  → single line: [10, 25.5, 40]
        ///   • Threshold string arrays  → single line: ["30-40", "50.5-60"]
        ///   • Direction / PressType / Key → same line
        /// </summary>
        private static string FormatLeverConfig(string json)
        {
            // Compact arrays of scalars — numbers (whole or decimal), range strings, or a mix
            // of the two, which is what an event carrying both threshold forms writes out.
            const string scalar = @"(?:-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?|""[^""\n]+"")";
            json = Regex.Replace(json,
                @"\[\s*\n(\s*" + scalar + @",?\s*\n)+\s*\]",
                m => "[" + string.Join(", ",
                    Regex.Matches(m.Value, scalar).Select(x => x.Value)) + "]");

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

        /// <summary>
        /// Low byte: the virtual key that carries the character on the given layout.
        /// High byte: the modifiers needed for it (1 = Shift, 2 = Ctrl, 4 = Alt). -1 = not on it.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

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
        private const uint KEYEVENTF_UNICODE     = 0x0004;
        private const uint KEYEVENTF_SCANCODE    = 0x0008;

        private static readonly HashSet<VirtualKey> ExtendedKeys = new()
        {
            VirtualKey.Insert,   VirtualKey.Delete,
            VirtualKey.Home,     VirtualKey.End,
            VirtualKey.PageUp,   VirtualKey.PageDown,
            VirtualKey.Up,       VirtualKey.Down,
            VirtualKey.Left,     VirtualKey.Right,
            VirtualKey.RightControl, VirtualKey.RightAlt,
            VirtualKey.NumPadDivide,
            VirtualKey.PrintScreen, VirtualKey.NumLock,
            VirtualKey.LeftWindows, VirtualKey.RightWindows, VirtualKey.Apps
        };

        private static INPUT BuildKeyInput(KeyToken token, bool keyUp)
        {
            uint   flags;
            ushort scan;

            if (token.IsUnicode)
            {
                // The character is not on the current layout at all, so there is no key to press
                // for it - KEYEVENTF_UNICODE hands the character straight to the focused window.
                // Text fields take it; anything reading raw scan codes (most games) will not see
                // it, which is the unavoidable trade for a character the keyboard cannot produce.
                flags = KEYEVENTF_UNICODE;
                scan  = token.Unicode;
            }
            else
            {
                flags = KEYEVENTF_SCANCODE;
                scan  = (ushort)MapVirtualKey((uint)token.Key, MAPVK_VK_TO_VSC);
                if (ExtendedKeys.Contains(token.Key)) flags |= KEYEVENTF_EXTENDEDKEY;
            }

            if (keyUp) flags |= KEYEVENTF_KEYUP;

            return new INPUT
            {
                Type  = INPUT_KEYBOARD,
                Union = new INPUTUNION
                {
                    Keyboard = new KEYBDINPUT { ScanCode = scan, Flags = flags }
                }
            };
        }

        private static void SendKeyDown(KeyToken token) =>
            SendInput(1, new[] { BuildKeyInput(token, false) }, Marshal.SizeOf<INPUT>());

        private static void SendKeyUp(KeyToken token) =>
            SendInput(1, new[] { BuildKeyInput(token, true) }, Marshal.SizeOf<INPUT>());

        /// <summary>
        /// Sends one complete press of a stroke, modifiers included. Queued presses do not go
        /// through the held-key ref counts, so the modifiers are pressed and released here.
        /// <para>A press can land on a key a HOLD still owns — a threshold that sits inside an
        /// active hold range fires while the hold is legitimately live, so ordering cannot
        /// separate them. The key-up above would drop that key with nothing left to put it
        /// back, so anything the ref counts still show as held is re-asserted afterwards, in
        /// the same modifiers-first order <see cref="TryHoldStroke"/> uses.</para>
        /// </summary>
        private void SendKeyPress(KeyStroke stroke, int holdMs)
        {
            var mods = StrokeModifiers(stroke);

            foreach (var m in mods) SendKeyDown(m);
            SendKeyDown(stroke.Token);
            Thread.Sleep(holdMs);
            SendKeyUp(stroke.Token);
            for (int i = mods.Count - 1; i >= 0; i--) SendKeyUp(mods[i]);

            foreach (var m in mods) if (IsKeyHeld(m)) SendKeyDown(m);
            if (IsKeyHeld(stroke.Token)) SendKeyDown(stroke.Token);
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
                "F13" => VirtualKey.F13, "F14" => VirtualKey.F14,
                "F15" => VirtualKey.F15, "F16" => VirtualKey.F16,
                "F17" => VirtualKey.F17, "F18" => VirtualKey.F18,
                "F19" => VirtualKey.F19, "F20" => VirtualKey.F20,
                "F21" => VirtualKey.F21, "F22" => VirtualKey.F22,
                "F23" => VirtualKey.F23, "F24" => VirtualKey.F24,

                // Lock, system and Windows keys.
                "CAPSLOCK"    or "CAPS"                    => VirtualKey.CapsLock,
                "NUMLOCK"                                  => VirtualKey.NumLock,
                "SCROLLLOCK"  or "SCROLL"                  => VirtualKey.ScrollLock,
                "PRINTSCREEN" or "PRTSC" or "SNAPSHOT"     => VirtualKey.PrintScreen,
                "PAUSE"       or "BREAK"                   => VirtualKey.Pause,
                "APPS"        or "CONTEXTMENU"             => VirtualKey.Apps,
                "LWIN"        or "LEFTWIN"  or "WIN"       => VirtualKey.LeftWindows,
                "RWIN"        or "RIGHTWIN"                => VirtualKey.RightWindows,
                "CLEAR"                                    => VirtualKey.Clear,
                "HELP"                                     => VirtualKey.Help,
                "CANCEL"                                   => VirtualKey.Cancel,
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

                // Punctuation — the symbol as printed on a US layout, plus a spelled-out alias.
                "." or "PERIOD" or "DOT"        => VirtualKey.OemPeriod,
                "," or "COMMA"                  => VirtualKey.OemComma,
                "-" or "MINUS"                  => VirtualKey.OemMinus,
                "=" or "EQUALS"                 => VirtualKey.OemPlus,
                ";" or "SEMICOLON"              => VirtualKey.OemSemicolon,
                "/" or "SLASH"                  => VirtualKey.OemQuestion,
                "`" or "TILDE" or "BACKTICK"    => VirtualKey.OemTilde,
                "[" or "LBRACKET"               => VirtualKey.OemOpenBrackets,
                "]" or "RBRACKET"               => VirtualKey.OemCloseBrackets,
                "\\" or "BACKSLASH"             => VirtualKey.OemPipe,
                "'" or "QUOTE" or "APOSTROPHE"  => VirtualKey.OemQuotes,
                "OEM102"                        => VirtualKey.OemBackslash,

                // Deliberately unbound — see IsExplicitNoKey. Listed so the intent is visible
                // here, even though the fallthrough would produce the same value.
                "NONE"                          => VirtualKey.None,

                _                   => VirtualKey.None
            };
        }

        /// <summary>
        /// Turns a Key field into something that can actually be sent.
        /// <list type="number">
        /// <item>A name from <see cref="ParseKey"/> ("SPACE", "NUMPAD+", "F13", ...) wins, so
        /// every existing config keeps behaving exactly as before.</item>
        /// <item>Anything else that is a single character is looked up on the <b>current</b>
        /// keyboard layout, which yields both the key position and the modifiers that make it
        /// print that character. This is what covers the shifted symbols and the accented
        /// letters without naming any of them here: "!", "?", ":", "%", "~", "&amp;",
        /// "^" and their equivalents all resolve on whatever layout is active.</item>
        /// <item>A "U+XXXX" escape names a character that is awkward to write into JSON.</item>
        /// <item>A character the layout cannot produce at all falls back to a Unicode injection
        /// - see <see cref="BuildKeyInput"/> for what that costs.</item>
        /// </list>
        /// </summary>
        private static KeyStroke ResolveKeyStroke(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString)) return KeyStroke.None;

            string s = keyString.Trim();

            var named = ParseKey(s);
            if (named != VirtualKey.None) return new KeyStroke(new KeyToken(named));

            // "NONE" switches a binding off; it is never a character to type.
            if (IsExplicitNoKey(s)) return KeyStroke.None;

            if (TryParseUnicodeEscape(s, out char escaped)) return ResolveCharacter(escaped);

            // A longer string is a misspelled key name, not a character. Reporting it as unknown
            // is the whole point of the config validation, so it must not resolve to anything.
            return s.Length == 1 ? ResolveCharacter(s[0]) : KeyStroke.None;
        }

        /// <summary>Reads a "U+20AC" style escape. Case-insensitive, up to four hex digits.</summary>
        private static bool TryParseUnicodeEscape(string s, out char value)
        {
            value = default;

            if (s.Length < 3 || (s[0] != 'U' && s[0] != 'u') || s[1] != '+') return false;
            if (!int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                return false;

            // Only the BMP: a KEYBDINPUT carries one 16-bit code unit, so anything above it
            // would need a surrogate pair and cannot be expressed as a single token.
            if (cp <= 0 || cp > 0xFFFF) return false;

            value = (char)cp;
            return true;
        }

        // A character resolves the same way until the keyboard layout changes, and
        // ProcessAxisEvent asks for every event on every poll — so the answers are kept and the
        // whole cache is dropped when the layout is no longer the one they were found on.
        // Named keys never get here, so an ordinary config never touches any of this.
        private static readonly Dictionary<char, KeyStroke> CharacterCache = new();
        private static IntPtr     _characterCacheLayout;
        private static readonly object CharacterCacheLock = new();

        /// <summary>
        /// Finds the key and modifiers that print a character on the layout in use right now.
        /// The layout is re-checked on every call, so switching layouts while the app runs is
        /// picked up without a reload.
        /// </summary>
        private static KeyStroke ResolveCharacter(char c)
        {
            IntPtr layout = GetKeyboardLayout(0);

            lock (CharacterCacheLock)
            {
                if (layout != _characterCacheLayout)
                {
                    CharacterCache.Clear();
                    _characterCacheLayout = layout;
                }
                else if (CharacterCache.TryGetValue(c, out var cached))
                {
                    return cached;
                }
            }

            var stroke = ScanCharacter(c, layout);

            lock (CharacterCacheLock)
            {
                // Only cache against the layout the scan was actually made on: another thread
                // may have switched layouts and cleared the cache in between.
                if (layout == _characterCacheLayout) CharacterCache[c] = stroke;
            }

            return stroke;
        }

        /// <summary>Asks the layout which key and modifiers produce a character.</summary>
        private static KeyStroke ScanCharacter(char c, IntPtr layout)
        {
            short scan = VkKeyScanEx(c, layout);

            if (scan != -1)
            {
                var vk    = (VirtualKey)(byte)(scan & 0xFF);
                int state = (scan >> 8) & 0xFF;

                if (vk != VirtualKey.None)
                    return new KeyStroke(new KeyToken(vk),
                                         Shift: (state & 1) != 0,
                                         Ctrl:  (state & 2) != 0,
                                         Alt:   (state & 4) != 0);
            }

            // Not on this layout at all — carried as the character itself.
            return new KeyStroke(new KeyToken(c));
        }

        /// <summary>
        /// True when a Key field is unbound on purpose rather than misspelled.
        /// <para>Both produce <see cref="VirtualKey.None"/> and are inert at runtime, but only a
        /// misspelling is worth warning about: writing <c>"Key": "None"</c> is how an event or
        /// button is switched off while its thresholds are kept around for testing.</para>
        /// </summary>
        private static bool IsExplicitNoKey(string keyString) =>
            string.IsNullOrWhiteSpace(keyString) ||
            keyString.Trim().Equals("None", StringComparison.OrdinalIgnoreCase);
    }

    // ─── App Settings ─────────────────────────────────────────────────────────

    public class AppSettings
    {
        /// <summary>Filename (no path) of the last loaded lever config, inside the config directory.</summary>
        public string LastLeverConfigFile { get; set; } = "";
        /// <summary>Filename (no path) of the last loaded button config, inside the config directory.</summary>
        public string LastButtonConfigFile { get; set; } = "";
        /// <summary>Filename (no path) of the last loaded FFB config, inside the config directory.</summary>
        public string LastFfbConfigFile { get; set; } = "";

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

        /// <summary>
        /// How far a lever has to travel back, in 0–100 axis units, before the movement counts
        /// as a reversal and the presses still queued from the old direction are dropped.
        /// 0 disables the cut-off, and is the default: a keystroke the lever asked for should
        /// be delayed rather than thrown away, and <see cref="PressQueueMaxAgeMs"/> already
        /// bounds how long a backlog can run on for. Set it above zero to trade keystrokes for
        /// a lever that stops sooner after a hard reversal.
        /// </summary>
        public double DirectionChangeUnits { get; set; } = 0;

        /// <summary>
        /// How long a press may sit in the queue before it is dropped instead of sent (ms).
        /// 0 disables the cut-off. Holds and releases are never dropped for age — only presses.
        /// </summary>
        public int PressQueueMaxAgeMs { get; set; } = 10000;
    }

    // ─── Lever Config File ────────────────────────────────────────────────────

    public class LeverConfigFile
    {
        /// <summary>Filename (no path) of the button config to auto-load alongside this lever config.</summary>
        public string AssociatedButtonConfigFile { get; set; } = "";
        /// <summary>Filename (no path) of the FFB config to auto-load alongside this lever config.</summary>
        public string AssociatedFfbConfigFile { get; set; } = "";
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
        /// <summary>For regular events: axis value(s) at which the event fires. Decimals allowed.</summary>
        public double[] Thresholds { get; set; } = Array.Empty<double>();

        /// <summary>For HOLD events: axis-value ranges that keep the key held.</summary>
        public HoldRange[]? HoldRanges { get; set; }

        public string Direction { get; set; } = "Both";
        public string PressType { get; set; } = "Hold";
        public string Key       { get; set; } = "";
    }

    /// <summary>An inclusive axis-value range used by HOLD events. Bounds may be decimal.</summary>
    public record HoldRange(double Min, double Max)
    {
        public bool Contains(double value) => value >= Min && value <= Max;

        /// <summary>
        /// Round-trips through <see cref="AxisEventConverter.ParseThresholdString"/>, so the bounds are
        /// always written with an invariant decimal point regardless of the system locale.
        /// </summary>
        public override string ToString() =>
            $"{Min.ToString("R", CultureInfo.InvariantCulture)}-{Max.ToString("R", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Reads and writes <see cref="AxisEvent"/> with a unified "Threshold" JSON field.
    /// <para>Reading: numeric values → <see cref="AxisEvent.Thresholds"/> (regular events);
    /// range strings like "30-40" → <see cref="AxisEvent.HoldRanges"/> (HOLD events).</para>
    /// <para>Writing: always <c>"Threshold"</c> — numbers for regular events, range strings for HOLD events.</para>
    /// <para>Both forms accept decimals (<c>62.5</c>, <c>"30.25-70.75"</c>). Range strings are
    /// parsed with the invariant culture, so a config written on a comma-decimal system still
    /// reads back correctly.</para>
    /// </summary>
    public class AxisEventConverter : JsonConverter<AxisEvent>
    {
        /// <summary>
        /// Classifies one quoted "Threshold" entry: <c>"30-70"</c> is a HOLD range, while a bare
        /// quoted number (<c>"62.5"</c>) is taken as a plain threshold — reading it as a
        /// zero-width range would produce an event that can never fire.
        /// </summary>
        internal static (double? Value, HoldRange? Range) ParseThresholdString(string s)
        {
            s = s.Trim();

            // Find the separating dash: skip a leading minus and the dash of an exponent
            // ("1e-3") so decimal bounds survive.
            int dash = -1;
            for (int i = 1; i < s.Length; i++)
            {
                if (s[i] != '-') continue;
                if (s[i - 1] is 'e' or 'E') continue;
                dash = i;
                break;
            }

            if (dash < 0)
                return (double.Parse(s, CultureInfo.InvariantCulture), null);

            double min = double.Parse(s[..dash].Trim(),      CultureInfo.InvariantCulture);
            double max = double.Parse(s[(dash + 1)..].Trim(), CultureInfo.InvariantCulture);
            return (null, new HoldRange(Math.Min(min, max), Math.Max(min, max)));
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
                            evt.Thresholds = new[] { val.GetDouble() };
                        }
                        else if (val.ValueKind == JsonValueKind.String)
                        {
                            var (v, r) = ParseThresholdString(val.GetString()!);
                            if (r != null) evt.HoldRanges = new[] { r };
                            else           evt.Thresholds = new[] { v!.Value };
                        }
                        else if (val.ValueKind == JsonValueKind.Array)
                        {
                            // A list may mix range strings and numbers, so each element is
                            // classified on its own and lands in the matching bucket.
                            var ranges = new List<HoldRange>();
                            var values = new List<double>();

                            foreach (var e in val.EnumerateArray())
                            {
                                if (e.ValueKind == JsonValueKind.String)
                                {
                                    var (v, r) = ParseThresholdString(e.GetString()!);
                                    if (r != null) ranges.Add(r);
                                    else           values.Add(v!.Value);
                                }
                                else if (e.ValueKind == JsonValueKind.Number)
                                {
                                    values.Add(e.GetDouble());
                                }
                            }

                            if (ranges.Count > 0) evt.HoldRanges = ranges.ToArray();
                            if (values.Count > 0) evt.Thresholds = values.ToArray();
                        }
                        break;
                }
            }

            return evt;
        }

        public override void Write(Utf8JsonWriter writer, AxisEvent value, JsonSerializerOptions options)
        {
            var ranges     = value.HoldRanges ?? Array.Empty<HoldRange>();
            int entryCount = ranges.Length + value.Thresholds.Length;

            writer.WriteStartObject();

            writer.WritePropertyName("ThresHold");
            // A single entry stays a scalar; anything else becomes an array. Ranges are written
            // as strings and plain thresholds as numbers, so an event carrying both round-trips.
            if (entryCount != 1) writer.WriteStartArray();

            foreach (var r in ranges)          writer.WriteStringValue(r.ToString());
            foreach (var v in value.Thresholds) writer.WriteNumberValue(v);

            if (entryCount != 1) writer.WriteEndArray();

            writer.WriteString("Direction", value.Direction);
            writer.WriteString("PressType", value.PressType);
            writer.WriteString("Key", value.Key);

            writer.WriteEndObject();
        }
    }

    public record ThresholdKey(double Threshold, string Direction, string Key, string PressType);

    /// <summary>
    /// One thing that can be pressed and released, and the identity the held-key ref counts are
    /// kept under. Normally a virtual key; a character that the current keyboard layout cannot
    /// produce at all is carried as <see cref="Unicode"/> instead and injected directly.
    /// </summary>
    public readonly record struct KeyToken(VirtualKey Key, char Unicode)
    {
        public KeyToken(VirtualKey key)  : this(key, (char)0) { }
        public KeyToken(char character)  : this(VirtualKey.None, character) { }

        public static readonly KeyToken None = new(VirtualKey.None, (char)0);

        public bool IsUnicode => Unicode != (char)0;
        public bool IsNone    => Key == VirtualKey.None && Unicode == (char)0;
    }

    /// <summary>
    /// A resolved key binding: the token to press, plus the modifiers the keyboard layout needs
    /// held for it to produce the requested character. A plain named key needs none of them;
    /// "!" needs Shift on a US layout, and a character on AltGr needs Ctrl and Alt.
    /// </summary>
    public readonly record struct KeyStroke(KeyToken Token, bool Shift = false, bool Ctrl = false, bool Alt = false)
    {
        public static readonly KeyStroke None = new(KeyToken.None);

        public bool IsNone => Token.IsNone;
    }

    // ─── Virtual Key Codes ────────────────────────────────────────────────────

    public enum VirtualKey : ushort
    {
        None        = 0x00,
        Cancel      = 0x03,
        Back        = 0x08,
        Tab         = 0x09,
        Clear       = 0x0C,
        Return      = 0x0D,
        Shift       = 0x10,
        Control     = 0x11,
        Alt         = 0x12,
        Pause       = 0x13,
        CapsLock    = 0x14,
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
        PrintScreen = 0x2C,   // VK_SNAPSHOT
        Insert      = 0x2D,
        Delete      = 0x2E,
        Help        = 0x2F,
        LeftWindows = 0x5B,
        RightWindows= 0x5C,
        Apps        = 0x5D,   // context-menu key
        NumLock     = 0x90,
        ScrollLock  = 0x91,
        LeftShift   = 0xA0,
        RightShift  = 0xA1,
        LeftControl = 0xA2,
        RightControl= 0xA3,
        LeftAlt     = 0xA4,
        RightAlt    = 0xA5,
        F1  = 0x70, F2  = 0x71, F3  = 0x72, F4  = 0x73,
        F5  = 0x74, F6  = 0x75, F7  = 0x76, F8  = 0x77,
        F9  = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,
        F13 = 0x7C, F14 = 0x7D, F15 = 0x7E, F16 = 0x7F,
        F17 = 0x80, F18 = 0x81, F19 = 0x82, F20 = 0x83,
        F21 = 0x84, F22 = 0x85, F23 = 0x86, F24 = 0x87,
        NumPad0 = 0x60, NumPad1 = 0x61, NumPad2 = 0x62, NumPad3 = 0x63,
        NumPad4 = 0x64, NumPad5 = 0x65, NumPad6 = 0x66, NumPad7 = 0x67,
        NumPad8 = 0x68, NumPad9 = 0x69,
        NumPadMultiply = 0x6A,
        NumPadAdd      = 0x6B,
        NumPadSubtract = 0x6D,
        NumPadDecimal  = 0x6E,
        NumPadDivide   = 0x6F,

        // Punctuation. These VK codes identify a physical key position; the character it prints
        // depends on the keyboard layout. SendKey emits the scan code, so a mapping always
        // reproduces the key that was recorded.
        OemSemicolon     = 0xBA, // ;  :
        OemPlus          = 0xBB, // =  +
        OemComma         = 0xBC, // ,  <
        OemMinus         = 0xBD, // -  _
        OemPeriod        = 0xBE, // .  >
        OemQuestion      = 0xBF, // /  ?
        OemTilde         = 0xC0, // `  ~
        OemOpenBrackets  = 0xDB, // [  {
        OemPipe          = 0xDC, // \  |
        OemCloseBrackets = 0xDD, // ]  }
        OemQuotes        = 0xDE, // '  "
        OemBackslash     = 0xE2  // <  >  (extra key on ISO keyboards)
    }
}
