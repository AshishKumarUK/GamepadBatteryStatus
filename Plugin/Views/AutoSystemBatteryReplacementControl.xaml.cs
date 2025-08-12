using Playnite.SDK.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Reflection;
using DualSenseBattery;

namespace DualSense.Views
{
    public partial class AutoSystemBatteryReplacementControl : UserControl
    {
        private readonly string helperPath;
        private readonly System.Windows.Threading.DispatcherTimer timer;
        private static DualSensePowerStatus powerStatus;

        // UI state / throttling
        private int lastLevel = -1;
        private bool lastCharging = false, lastFull = false;
        private bool firstApplied = false;
        private int disconnectStrikes = 0;
        private const int disconnectThreshold = 2;
        private bool everConnected = false;
        
        // Fast initial connection detection
        private DateTime startTime = DateTime.Now;
        private bool isInInitialDetectionMode = true;
        private const int INITIAL_DETECTION_DURATION = 30000; // 30 seconds

        // Automatic system battery detection
        private bool isSystemBatteryEnabled = true;
        private readonly System.Windows.Threading.DispatcherTimer settingsCheckTimer;

        private volatile bool _polling; // prevent overlapping polls

        public AutoSystemBatteryReplacementControl()
        {
            InitializeComponent();

            var pluginDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            helperPath = Path.Combine(pluginDir ?? "", "Helper", "DualSenseBatteryHelper.exe");

            // Initialize shared power status if not already done
            if (powerStatus == null)
            {
                powerStatus = new DualSensePowerStatus();
            }

            // Background timer (UI thread tick; actual work is off-thread)
            timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5) // Fast initial detection (5 seconds)
            };
            timer.Tick += Timer_Tick;
            timer.Start();

            // Settings check timer - check if system battery is enabled/disabled
            settingsCheckTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10) // Check every 10 seconds
            };
            settingsCheckTimer.Tick += SettingsCheckTimer_Tick;
            settingsCheckTimer.Start();

            // Initial settings check
            CheckSystemBatterySetting();

            // Stagger first run so it never competes with first paint
            Task.Delay(700).ContinueWith(_ =>
            {
                try { Dispatcher.Invoke(() => Timer_Tick(null, EventArgs.Empty)); } catch { /* ignored */ }
            });
        }

        private void SettingsCheckTimer_Tick(object sender, EventArgs e)
        {
            CheckSystemBatterySetting();
        }

        private void CheckSystemBatterySetting()
        {
            try
            {
                // Simple heuristic: assume desktop PC if no battery is detected
                // This is a reasonable assumption for most desktop users
                bool hasSystemBattery = IsDesktopPC();
                
                // If desktop PC (no system battery), always show DualSense battery
                if (!hasSystemBattery)
                {
                    if (!isSystemBatteryEnabled)
                    {
                        isSystemBatteryEnabled = false;
                        ShowDualSenseBattery();
                    }
                    return;
                }

                // For laptops, try to detect if Playnite's battery setting is disabled
                bool shouldShowDualSense = !hasSystemBattery || IsSystemBatteryDisabled();
                
                if (shouldShowDualSense != isSystemBatteryEnabled)
                {
                    isSystemBatteryEnabled = !shouldShowDualSense;
                    if (shouldShowDualSense)
                    {
                        ShowDualSenseBattery();
                    }
                    else
                    {
                        HideDualSenseBattery();
                    }
                }
            }
            catch
            {
                // If we can't determine, assume system battery is enabled
                isSystemBatteryEnabled = true;
                HideDualSenseBattery();
            }
        }

        private bool IsDesktopPC()
        {
            try
            {
                // Simple heuristic: check if we're in fullscreen mode on a desktop
                // Most desktop users use fullscreen mode where battery settings apply
                var currentApp = System.Windows.Application.Current;
                if (currentApp != null)
                {
                    var mainWindow = currentApp.MainWindow;
                    if (mainWindow != null && mainWindow.WindowState == WindowState.Maximized)
                    {
                        // In fullscreen mode, assume desktop PC (no system battery)
                        return false; // false = no system battery = desktop PC
                    }
                }
                
                // Default: assume laptop (has system battery)
                return true;
            }
            catch
            {
                // Default: assume laptop
                return true;
            }
        }

        private bool IsSystemBatteryDisabled()
        {
            try
            {
                // Try to detect if Playnite's battery setting is disabled
                // This is a heuristic - we check if we're in fullscreen mode and system battery should be hidden
                var currentApp = System.Windows.Application.Current;
                if (currentApp != null)
                {
                    // Check if we're in fullscreen mode (where battery settings apply)
                    var mainWindow = currentApp.MainWindow;
                    if (mainWindow != null && mainWindow.WindowState == WindowState.Maximized)
                    {
                        // In fullscreen mode, assume battery might be disabled for desktop users
                        // This is a reasonable assumption for desktop PC users
                        return true;
                    }
                }
                
                // Default: assume system battery is enabled
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void ShowDualSenseBattery()
        {
            if (ContentRoot.Visibility != Visibility.Visible)
            {
                ContentRoot.Visibility = Visibility.Visible;
            }
        }

        private void HideDualSenseBattery()
        {
            if (ContentRoot.Visibility != Visibility.Collapsed)
            {
                ContentRoot.Visibility = Visibility.Collapsed;
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            // Only poll if we should show DualSense battery
            if (isSystemBatteryEnabled)
            {
                return;
            }

            // Check if we should switch from initial detection mode to optimized mode
            if (isInInitialDetectionMode)
            {
                var timeSinceStart = DateTime.Now - startTime;
                if (timeSinceStart.TotalMilliseconds > INITIAL_DETECTION_DURATION)
                {
                    isInInitialDetectionMode = false;
                    timer.Interval = TimeSpan.FromMinutes(5); // Switch to optimized 5-minute intervals
                }
            }
            
            if (_polling) return;
            _polling = true;

            Task.Run(() =>
            {
                try
                {
                    var reading = GetReading();
                    if (reading != null)
                    {
                        Dispatcher.Invoke(() => ApplyReading(reading));
                    }
                }
                catch
                {
                    // swallow; never crash UI
                }
                finally
                {
                    _polling = false;
                }
            });
        }

        private BatteryReading GetReading()
        {
            try
            {
                if (!File.Exists(helperPath))
                    return null;

                var psi = new ProcessStartInfo
                {
                    FileName = helperPath,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(helperPath) ?? ""
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                        return null;

                    string output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(1500); // hard cap; don't stall background thread

                    if (string.IsNullOrWhiteSpace(output))
                        return null;

                    return ParseReading(output);
                }
            }
            catch
            {
                return null;
            }
        }

        // Minimal parser for: {"connected":true,"level":85,"charging":false,"full":false}
        private BatteryReading ParseReading(string json)
        {
            try
            {
                var r = new BatteryReading
                {
                    Connected = json.IndexOf("\"connected\":true", StringComparison.OrdinalIgnoreCase) >= 0,
                    Charging = json.IndexOf("\"charging\":true", StringComparison.OrdinalIgnoreCase) >= 0,
                    Full = json.IndexOf("\"full\":true", StringComparison.OrdinalIgnoreCase) >= 0
                };

                r.Level = ExtractInt(json, "\"level\":");
                if (r.Level < 0) r.Level = 0;
                if (r.Level > 100) r.Level = 100;

                return r;
            }
            catch
            {
                return null;
            }
        }

        private int ExtractInt(string src, string key)
        {
            int i = src.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;
            i += key.Length;

            // read digits (and optional whitespace)
            int end = i;
            while (end < src.Length && (char.IsWhiteSpace(src[end]) || (src[end] >= '0' && src[end] <= '9')))
                end++;

            var num = src.Substring(i, end - i).Trim();
            int val;
            return int.TryParse(num, out val) ? val : 0;
        }

        private void ApplyReading(BatteryReading r)
        {
            // Only show if system battery is disabled
            if (isSystemBatteryEnabled)
            {
                return;
            }

            // Disconnected → hide after a couple misses (prevents flicker on brief blips)
            if (!r.Connected)
            {
                disconnectStrikes++;
                if (!everConnected || disconnectStrikes >= disconnectThreshold)
                {
                    ContentRoot.Visibility = Visibility.Collapsed;
                }
                return;
            }

            // Connected
            disconnectStrikes = 0;
            everConnected = true;

            // Ignore classic bogus first 0% (unless charging/full)
            if (!firstApplied && r.Level == 0 && !r.Charging && !r.Full)
            {
                ContentRoot.Visibility = Visibility.Collapsed;
                return;
            }

            // Show once we have a legit reading
            if (ContentRoot.Visibility != Visibility.Visible)
            {
                ContentRoot.Visibility = Visibility.Visible;
            }

            // No change → no repaint
            if (r.Level == lastLevel && r.Charging == lastCharging && r.Full == lastFull)
                return;

            lastLevel = r.Level;
            lastCharging = r.Charging;
            lastFull = r.Full;
            firstApplied = true;

            // Update UI - mimic Playnite's system battery display
            Txt.Text = r.Full ? "100%" : $"{r.Level}%";
            Bolt.Visibility = r.Charging ? Visibility.Visible : Visibility.Collapsed;

            // Fill bar inside 22x11 icon (margins: 1.2 left, 3.6 right)
            double maxFill = 22.0 - (1.2 + 3.6); // ~17.2 px
            BatteryFill.Width = Math.Max(0, maxFill * (r.Level / 100.0));
            BatteryFill.Opacity = r.Level > 0 ? 1.0 : 0.35;

            // Update shared power status
            if (powerStatus != null)
            {
                powerStatus.UpdateBatteryStatus(r.Connected, r.Level, r.Charging);
            }
        }

        private class BatteryReading
        {
            public bool Connected { get; set; }
            public int Level { get; set; }     // 0–100
            public bool Charging { get; set; }
            public bool Full { get; set; }
        }
    }
}
