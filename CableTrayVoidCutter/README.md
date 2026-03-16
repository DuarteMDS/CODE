# CableTray Void Cutter — Revit 2025 Plugin

A Revit 2025 addin that automatically detects collisions between **Cable Trays** and **Walls / Structural Beams** (in both the host model and all linked models), then places void families to create openings at each intersection.

---

## Features

| Feature | Details |
|---------|---------|
| **Clash detection** | Cable Trays vs. Walls & Structural Beams |
| **Linked models** | Scans all loaded Revit Link instances |
| **Configurable margin** | Extra clearance around each cable tray (mm) |
| **Family picker** | Choose separate void families for walls and beams |
| **Persistent settings** | Selected families + margin saved between sessions |
| **Custom ribbon tab** | "Cable Tray Tools" tab with panel and buttons |

---

## Requirements

- **Revit 2025** (uses .NET 8 API)
- Windows 10/11 x64

---

## Build

```powershell
# From the project folder
dotnet build -c Release
```

> The Revit 2025 API DLLs must be present at:
> `C:\Program Files\Autodesk\Revit 2025\RevitAPI.dll`
> or set `$env:RevitAPIPath` to your installation path.

---

## Installation

### Automatic (recommended)

```powershell
.\Install-Addin.ps1
```

This copies the compiled output and the `.addin` manifest to:
```
%APPDATA%\Autodesk\Revit\Addins\2025\
```

### Manual

1. Build the project (`dotnet build -c Release`)
2. Copy the following files to `%APPDATA%\Autodesk\Revit\Addins\2025\`:
   - `CableTrayVoidCutter.dll`
   - `CableTrayVoidCutter.addin`
3. Restart Revit 2025.

---

## Usage

### Step 1 – Configure families

1. Open Revit 2025 with a MEP project.
2. Go to **Cable Tray Tools** tab → click **Settings**.
3. Add your void `.rfa` family files (one for walls, one for beams, or the same for both).
4. Set the **Target** column to `Wall`, `Beam`, or `Both`.
5. Click **OK** – settings are saved automatically.

> **Void family requirements:** The family must contain parameters named
> `Width`, `Height`, and `Depth` (in feet) so the placer can set their values.

### Step 2 – Detect & place voids

1. Click **Create Voids** in the ribbon.
2. Set the **clearance margin** (default: 25 mm).
3. Click **⟳ Detect Clashes** – the table populates with all collisions found.
4. Review the list; uncheck any you want to skip.
5. Select the appropriate **void family** for walls and beams.
6. Click **Apply Selected Voids**.

### Linked model behaviour

| Scenario | Result |
|----------|--------|
| Wall/Beam in **host** model | Actual opening (wall) or void cut (beam) is created |
| Wall/Beam in **linked** model | A reservation void family is placed in the host at the clash location, tagged with the link name |

> Linked models cannot be modified from the host. Share the reservation markers with the structural/architectural team to add the real openings in their model.

---

## Project structure

```
CableTrayVoidCutter/
├── App.cs                          # IExternalApplication – ribbon setup
├── CableTrayVoidCutter.addin       # Revit manifest
├── CableTrayVoidCutter.csproj      # .NET 8 / WPF project
├── Install-Addin.ps1               # One-click install script
│
├── Commands/
│   ├── CreateVoidsCommand.cs       # Main ribbon command
│   └── SettingsCommand.cs          # Settings ribbon command
│
├── Core/
│   ├── ClashDetector.cs            # Host + linked clash detection
│   ├── VoidPlacer.cs               # Opening / family placement
│   └── GeometryHelper.cs           # Solid extraction & boolean ops
│
├── Models/
│   ├── ClashResult.cs              # Clash data model
│   └── AppSettings.cs              # Persistent settings (JSON)
│
└── UI/
    ├── MainWindow.xaml / .cs       # Main WPF dialog
    ├── MainViewModel.cs            # MVVM view model
    ├── RelayCommand.cs             # ICommand helper
    ├── SettingsWindow.xaml / .cs   # Settings dialog
    └── Resources/                  # Optional button icons (PNG)
```

---

## Settings file location

```
%APPDATA%\CableTrayVoidCutter\settings.json
```

---

## License

MIT
