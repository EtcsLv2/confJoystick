using System.Collections.Generic;

namespace ConfJoystick
{
    public class FfbConfigFile
    {
        public List<FfbDeviceConfig> Devices { get; set; } = new();
    }

    public class FfbDeviceConfig
    {
        /// <summary>Must match the joystick's InstanceName as shown in the device combo box.</summary>
        public string DeviceName { get; set; } = "";
        public List<FfbAxisConfig> Axes { get; set; } = new();
    }

    public class FfbAxisConfig
    {
        /// <summary>Axis name: "X", "Y", "Z", "RotationX", "RotationY", "RotationZ", "Slider0", "Slider1".</summary>
        public string Axis { get; set; } = "";
        public FfbNotchConfig? Notches { get; set; }
        public FfbResistanceConfig? Resistance { get; set; }
    }

    public class FfbNotchConfig
    {
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Notch positions on the normalized 0–100 axis scale. Decimals are allowed: one unit
        /// is 200 DirectInput offset units, so positions ~0.01 apart are still distinguishable.
        /// Order does not matter — the nearest notch always wins.
        /// </summary>
        public List<double> Positions { get; set; } = new();

        /// <summary>
        /// Maximum distance, in 0–100 units, over which a notch pulls the axis.
        /// Where two notches are closer together than twice this value the axis is pulled to
        /// whichever is nearest, with no free travel between them; where they are further apart
        /// each notch gets a ±SnapZoneWidth zone and the axis is free in between.
        /// Evaluated per gap, so unevenly spaced notches behave correctly.
        /// </summary>
        public double SnapZoneWidth { get; set; } = 5;

        /// <summary>Peak force magnitude in DirectInput units (0–10000).</summary>
        public int Strength { get; set; } = 5000;
    }

    public class FfbResistanceConfig
    {
        public bool Enabled { get; set; } = true;
        /// <summary>Friction coefficient in DirectInput units (0–10000).</summary>
        public int Strength { get; set; } = 3000;
    }
}
