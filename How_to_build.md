# How to Build

Tested on Windows 11 with Visual Studio 2026.

| Setting | Value |
|---|---|
| Target Framework | `net8.0-windows` |
| Target Runtime | Portable |
| Output type | WinExe (WPF) |

Top-level NuGet dependencies:

| Package | Version | Used for |
|---|---|---|
| `SharpDX` | 4.2.0 | DirectInput interop core |
| `SharpDX.DirectInput` | 4.2.0 | Joystick enumeration, axis/button polling, force feedback effects |

JSON is handled by the built-in `System.Text.Json`; there is no third-party JSON dependency.

From the command line:

```
dotnet build ConfJoystick.sln
```
