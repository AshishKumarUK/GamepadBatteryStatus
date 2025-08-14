# PS5 DualSense Controller Battery Monitor for Playnite

Shows your PS5 DualSense controller battery level in Playnite Fullscreen theme, exactly where the system battery appears, with theme-accurate visuals. Optimized for fast connect/disconnect.

## How it works

- `Helper/` is a small **.NET 8** console that reads DualSense battery and prints JSON.
  - Prioritizes DS4Windows DSU/UDP (Cemuhook) for DualSense-as-DS4 scenarios
  - Falls back to native DualSense HID when available
- `Plugin/` is a Playnite **GenericPlugin** (**.NET Framework 4.6.2**).
  - Starts the helper, parses JSON, and exposes a `PowerStatus` object (Percent/Charge/Charging/Available)
  - In Fullscreen, it binds the theme’s battery elements to our `PowerStatus` so the theme renders the icon/percentage with its own styles
  - Respects theme toggles (Show battery status / Show battery percentage) and hides when the controller is off

## Features

- Theme-accurate battery icon: Uses the theme’s own glyphs/styles (e.g., PS5 Reborn `Media.xaml` resources)
- Exact placement: Renders in the theme’s system battery slot (no overlaps with the clock)
- Fast connect/disconnect: ~1s to appear/disappear when the controller turns on/off
- Percentage source: When “Show battery percentage” is ON, the percentage is bound to DualSense battery (not system/laptop)
- Toggle support: “Show battery status” and “Show battery percentage” control visibility; we ignore system/laptop battery entirely
- DS4Windows compatible: Reads DualSense via DSU/UDP even when it appears as a PS4 controller

## Performance

- Initial detection: 1s polling for 60s after startup
- Connected: 5s polling (3s if charging)
- Disconnected: 1s polling for quick re-connect detection (with a 1.5s rapid probe window)
- UI scan to attach/bind theme elements runs every 1s

## Quick Start (GitHub Build)

### 1. Download from GitHub Actions
1. Go to the **Actions** tab in this repository
2. Click on the latest successful workflow run
3. Download the `DualSenseBattery` artifact (`.pext` file)
4. In Playnite: **Add-ons → Install from file →** select the downloaded `.pext`

### 2. DS4Windows Setup (Recommended)
If you use DS4Windows to make your DualSense appear as a PS4 controller:

1. **Install DS4Windows** (if not already installed):
   - Download from [DS4Windows GitHub](https://github.com/Ryochan7/DS4Windows)
   - Install and run DS4Windows

2. **Enable UDP Server** in DS4Windows:
   - Open DS4Windows
   - Go to **Settings** → **UDP Server**
   - Check **"Enable UDP server"**
   - Set **Port** to `26760` (default)
   - Click **Save**

3. **Connect your DualSense**:
   - Connect your DualSense controller (USB or Bluetooth)
   - DS4Windows should detect it as a PS4 controller
   - The plugin will automatically use DS4Windows' UDP server to read battery status

**Note**: The plugin automatically detects DS4Windows and uses its UDP protocol. If DS4Windows is not running, it falls back to direct DualSense HID communication.

### 3. Add to Your Theme (Optional)
If you prefer explicit placement, add our automatic element (not required for most themes):

```xml
<ContentControl
    playnite:PluginHostElement.SourceName="DualSenseSystemBattery"
    playnite:PluginHostElement.ElementName="AutoSystemBatteryReplacement"
    HorizontalAlignment="Right"
    VerticalAlignment="Center"/>
```

## Use in your theme

### Automatic system battery replacement (Recommended)
In most themes, nothing is needed. The plugin finds the battery slot and binds `PowerStatus` so the theme draws the icon/percent itself.

If your theme needs an explicit host, add:

```xml
<ContentControl
    playnite:PluginHostElement.SourceName="DualSenseSystemBattery"
    playnite:PluginHostElement.ElementName="AutoSystemBatteryReplacement"
    HorizontalAlignment="Right"
    VerticalAlignment="Center"/>
```

**How it works:**
1. Desktop PCs: System battery is ignored; DualSense drives the icon/percent
2. Laptops: If the theme shows system battery, our data still powers the icon (no laptop battery influence)
3. Toggle aware: Theme’s “Show battery status/percentage” buttons control visibility
4. Seamless visuals: Uses the theme’s glyphs/fonts/margins (e.g., PS5 Reborn)

### Example: Fullscreen Theme Integration
In a fullscreen theme’s main view (only if needed), add the automatic host and a percent text:

```xml
<!-- Automatic DualSense system battery replacement -->
<ContentControl
    playnite:PluginHostElement.SourceName="DualSenseSystemBattery"
    playnite:PluginHostElement.ElementName="AutoSystemBatteryReplacement"
    Focusable="False" 
    FontSize="42"
    VerticalAlignment="Center" 
    Margin="10,0,10,0"
    Grid.Column="2"/>

<!-- Battery percentage text (optional) -->
<TextBlock x:Name="PART_TextBatteryPercentage" 
           Style="{DynamicResource TextBlockBaseStyle}"
           VerticalAlignment="Center" 
           Margin="0,0,20,0"
           Grid.Column="3"/>
```

Note: In most themes (including PS5 Reborn), you don’t need to hide the original battery. We bind its DataContext to our `PowerStatus` and hide it when the controller disconnects.

### Manual system battery replacement (Alternative)
If a theme doesn’t expose the standard battery controls, you can place our element manually and hide the theme’s:
```xml
<ContentControl
    playnite:PluginHostElement.SourceName="DualSenseSystemBattery"
    playnite:PluginHostElement.ElementName="SystemBatteryReplacement"
    HorizontalAlignment="Right"
    VerticalAlignment="Center"/>
```

## Benefits

- Looks identical to the theme’s system battery (icon, size, margins)
- Appears/disappears within ~1s when the controller turns on/off
- Percentage is from DualSense (not system battery)
- Respects the theme’s Show battery status/percentage toggles
- DS4Windows / DualShock 4 compatibility via DSU/UDP
- Low CPU: modest polling when connected, faster only when needed

## GitHub Upload Instructions

### 1. Create GitHub Repository
1. Go to [GitHub](https://github.com) and sign in
2. Click **"New repository"**
3. Name it: `PlayNite-DualSense-Battery-Status`
4. Make it **Public** (so others can download)
5. **Don't** initialize with README (we already have one)
6. Click **"Create repository"**

### 2. Upload Your Code
1. **Clone the repository** to your local machine:
   ```bash
   git clone https://github.com/YOUR_USERNAME/PlayNite-DualSense-Battery-Status.git
   cd PlayNite-DualSense-Battery-Status
   ```

2. **Copy all project files** into this directory:
   - `Plugin/` folder
   - `Helper/` folder
   - `extension.yaml`
   - `README.md`
   - `.github/workflows/build.yml`

3. **Commit and push**:
   ```bash
   git add .
   git commit -m "Initial commit: DualSense Battery Plugin for Playnite"
   git push origin main
   ```

### 3. Automatic Build
- GitHub Actions will automatically build the plugin when you push
- Go to **Actions** tab to see the build progress
- Download the `.pext` file from the completed workflow

## Local build (optional)

- Requires .NET 8 SDK and **.NET Framework 4.6.2 Developer Pack**
- Build helper: `dotnet build Helper -c Release`
- Build plugin: `dotnet build Plugin -c Release`
* Pack structure:

```
DualSenseBattery/
  extension.yaml
  DualSenseBattery.dll
  Helper/
    DualSenseBatteryHelper.exe
```

Zip `DualSenseBattery/` as `DualSenseBattery.pext`.

## About

Displays the battery status and percentage of PS5 DualSense controller connected via bluetooth or usb, with the ability to replace Playnite's system battery indicator for desktop PC users.
