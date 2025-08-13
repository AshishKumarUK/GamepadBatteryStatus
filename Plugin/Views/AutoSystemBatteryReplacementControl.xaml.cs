using Playnite.SDK.Controls;
using Playnite.SDK;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Reflection;
using DualSenseBattery;

namespace DualSenseBattery.Views
{
    public partial class AutoSystemBatteryReplacementControl : PluginUserControl
    {
        public bool ForceShow { get; set; } = false;
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

        // Track whether theme's system battery+percentage are shown (controlled by overlay manager)
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

            // Match theme icon sizing by borrowing the same resource as the system battery
            TryBindThemeIcon(BatteryChargeLevel.Critical); // initial placeholder

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

        public void ApplyReferenceVisual(FrameworkElement reference)
        {
            try
            {
                if (reference == null)
                {
                    return;
                }

                // Copy placement-related properties
                try { this.Margin = reference.Margin; } catch { }
                try { this.HorizontalAlignment = reference.HorizontalAlignment; } catch { }
                try { this.VerticalAlignment = reference.VerticalAlignment; } catch { }

                // Copy transforms cautiously: avoid applying extreme scales to glyph
                try
                {
                    if (reference.RenderTransform is Transform rt)
                    {
                        if (rt is ScaleTransform st && (st.ScaleX < 0.5 || st.ScaleY < 0.5))
                        {
                            // Skip extreme downscales on icon; keep it readable
                        }
                        else
                        {
                            Icon.RenderTransform = rt;
                        }
                    }
                }
                catch { }

                try
                {
                    if (reference.LayoutTransform is Transform lt)
                    {
                        Icon.LayoutTransform = lt;
                    }
                }
                catch { }

                // If reference has explicit size, mirror it
                try
                {
                    if (!double.IsNaN(reference.Width) && reference.Width > 0)
                    {
                        this.Width = reference.Width;
                    }
                    if (!double.IsNaN(reference.Height) && reference.Height > 0)
                    {
                        this.Height = reference.Height;
                    }
                }
                catch { }
            }
            catch { }
        }

        private void SettingsCheckTimer_Tick(object sender, EventArgs e)
        {
            CheckSystemBatterySetting();
        }

        private void CheckSystemBatterySetting()
        {
            try
            {
                // Overlay manager decides visibility based on actual theme toggle state.
                // Here we keep logic minimal: respect external toggle via ForceShow or when system battery is disabled.
                if (!isSystemBatteryEnabled || ForceShow)
                {
                    ShowDualSenseBattery();
                }
                else
                {
                    HideDualSenseBattery();
                }
            }
            catch
            {
                // If we can't determine, assume system battery is enabled
                isSystemBatteryEnabled = true;
                HideDualSenseBattery();
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
            if (isSystemBatteryEnabled && !ForceShow)
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
                    Full = json.IndexOf("\"full\":true", StringComparison.OrdinalIgnoreCase) >= 0,
                    Bluetooth = json.IndexOf("\"bt\":true", StringComparison.OrdinalIgnoreCase) >= 0
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
            // Only show if system battery is disabled (unless forced by overlay)
            if (isSystemBatteryEnabled && !ForceShow)
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
            ContentRoot.Visibility = Visibility.Visible;

            // No change → no repaint
            if (r.Level == lastLevel && r.Charging == lastCharging && r.Full == lastFull)
                return;

            lastLevel = r.Level;
            lastCharging = r.Charging;
            lastFull = r.Full;
            firstApplied = true;

            // Update icon to the same glyph the theme uses
            var charge = r.Full ? BatteryChargeLevel.High : GetLevel(r.Level);
            SetThemeIcon(charge, r.Charging);

            // Update shared power status
            if (powerStatus != null)
            {
                powerStatus.UpdateBatteryStatus(r.Connected, r.Level, r.Charging);
            }
        }

        private BatteryChargeLevel GetLevel(int percent)
        {
            if (percent > 85) return BatteryChargeLevel.High;
            if (percent > 40) return BatteryChargeLevel.Medium;
            if (percent > 10) return BatteryChargeLevel.Low;
            return BatteryChargeLevel.Critical;
        }

        private void TryBindThemeIcon(BatteryChargeLevel level, bool charging = false)
        {
            try
            {
                SetThemeIcon(level, charging);
            }
            catch { }
        }

        private void SetThemeIcon(BatteryChargeLevel level, bool charging)
        {
            // PS5 Reborn defines TextBlock resources in Media.xaml for each state
            string key = charging ? "BatteryStatusCharging"
                                  : (level == BatteryChargeLevel.High ? "BatteryStatusHigh"
                                    : level == BatteryChargeLevel.Medium ? "BatteryStatusMedium"
                                    : level == BatteryChargeLevel.Low ? "BatteryStatusLow"
                                    : "BatteryStatusCritical");

            // Prefer global theme resource lookup via Playnite's ResourceProvider
            TextBlock res = null;
            try
            {
                var fromProvider = ResourceProvider.GetResource(key);
                res = fromProvider as TextBlock;
            }
            catch { }
            if (res == null)
            {
                res = TryFindResource(key) as TextBlock;
            }
            if (res != null)
            {
                Icon.Text = res.Text;
                Icon.FontFamily = res.FontFamily;
                Icon.FontSize = res.FontSize > 0 ? res.FontSize : 42;
                if (res.Foreground != null) { Icon.Foreground = res.Foreground; }
                Icon.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 5, ShadowDepth = 0 };
                Icon.Margin = res.Margin;
                return;
            }

            // PS5 Reborn fallback using Segoe Fluent Icons glyphs
            if (res == null)
            {
                // Try using FontIcoFont with theme glyph codes
                string glyph = charging ? "\ueed4"
                              : (level == BatteryChargeLevel.High ? "\ueeb2"
                                : level == BatteryChargeLevel.Medium ? "\ueeb3"
                                : level == BatteryChargeLevel.Low ? "\ueeb4"
                                : "\ueeb1");
                Icon.Text = glyph;
                try
                {
                    var fontRes = ResourceProvider.GetResource("FontIcoFont") as FontFamily;
                    Icon.FontFamily = fontRes ?? new FontFamily("FontIcoFont");
                }
                catch
                {
                    Icon.FontFamily = new FontFamily("FontIcoFont");
                }
                Icon.FontSize = 42;
                Icon.Foreground = Brushes.White;
                Icon.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 5, ShadowDepth = 0 };
                return;
            }
            // Keep bar hidden by default (we only need glyph for PS5 Reborn)
        }

        private class BatteryReading
        {
            public bool Connected { get; set; }
            public int Level { get; set; }     // 0–100
            public bool Charging { get; set; }
            public bool Full { get; set; }
            public bool Bluetooth { get; set; }
        }
    }
}
