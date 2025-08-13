using Playnite.SDK;
using Playnite.SDK.Plugins;
using Playnite.SDK.Events;
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;

namespace DualSenseBattery
{
    public class PluginImpl : GenericPlugin
    {
        private FullscreenOverlayManager overlayManager;
        public override Guid Id => new Guid("fbd2c2e6-9c1b-49b6-9c0d-1c5d3c0a9a6a");

        public PluginImpl(IPlayniteAPI api) : base(api)
        {
            // Keep the original custom element support for backward compatibility
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = "DualSenseBattery",
                ElementList = new List<string> { "Bar" }
            });

            // Add custom element for automatic system battery replacement
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = "DualSenseSystemBattery",
                ElementList = new List<string> { "SystemBatteryReplacement", "AutoSystemBatteryReplacement" }
            });
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);
            overlayManager = new FullscreenOverlayManager();
            overlayManager.Start();
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            if (args.Name == "Bar")
            {
                return new Views.BatteryBarControl();
            }
            else if (args.Name == "SystemBatteryReplacement")
            {
                return new Views.SystemBatteryReplacementControl();
            }
            else if (args.Name == "AutoSystemBatteryReplacement")
            {
                return new Views.AutoSystemBatteryReplacementControl();
            }
            return null;
        }

        public override IEnumerable<TopPanelItem> GetTopPanelItems()
        {
            // Provide an automatic top panel item so no theme editing is required.
            // The internal control handles its own visibility (desktop vs laptop, system battery setting, etc.).
            yield return new TopPanelItem
            {
                Title = "Controller Battery",
                Icon = new Views.AutoSystemBatteryReplacementControl(),
                Visible = true
            };
        }
    }

    internal class FullscreenOverlayManager
    {
        private DispatcherTimer timer;
        private FrameworkElement batteryHost; // theme battery content (e.g., CustomBattery/BatteryStatus)
        private FrameworkElement batteryRoot; // theme battery container (e.g., Battery)
        private FrameworkElement injected;

        public void Start()
        {
            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += Timer_Tick;
            timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                var main = Application.Current?.MainWindow;
                if (main == null)
                {
                    RemoveInjected();
                    return;
                }

                // Detect fullscreen app by window type name containing "FullscreenApp"
                var isFullscreen = main.GetType().FullName?.IndexOf("FullscreenApp", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isFullscreen)
                {
                    RemoveInjected();
                    return;
                }

                // Find built-in battery slot by common names (default + popular themes)
                if (batteryHost == null)
                {
                    // Prefer PS5 Reborn specific names first so visibility reflects its own toggle
                    batteryHost = FindByName(main, "CustomBattery")
                                  ?? FindByName(main, "BatteryStatus")
                                  ?? FindByName(main, "PART_ElemBatteryStatus")
                                  ?? FindByName(main, "ElemBatteryStatus")
                                  ?? FindByName(main, "PART_BatteryStatus")
                                  ?? FindByName(main, "PART_PS_Battery")
                                  ?? FindByName(main, "PSBattery")
                                  ?? FindByName(main, "PS5_Battery")
                                  ?? FindByName(main, "PS5Battery")
                                  ?? FindByName(main, "Battery");
                }

                // Also cache the outer container (PS5 Reborn uses Grid x:Name="Battery")
                if (batteryRoot == null)
                {
                    batteryRoot = FindByName(main, "Battery") ?? FindByName(main, "BatteryContainer");
                }
                if (batteryHost == null)
                {
                    RemoveInjected();
                    return;
                }

                // Determine visibility based on theme's battery host (handles opacity/scale animations too)
                var hostVisible = IsEffectivelyVisible(batteryHost);

                // Inject as sibling under the outer battery container so our control doesn't inherit host's Collapsed state
                var parentPanel = (batteryRoot as Panel) ?? GetParentPanel(batteryHost);
                EnsureInjectedAsSibling(parentPanel, batteryHost);

                // Show ours only when the built-in one is hidden
                injected.Visibility = hostVisible ? Visibility.Collapsed : Visibility.Visible;
                if (injected is Views.AutoSystemBatteryReplacementControl ctrl)
                {
                    ctrl.ForceShow = injected.Visibility == Visibility.Visible;
                }
            }
            catch
            {
                // ignore
            }
        }

        private void EnsureInjectedAsSibling(Panel parent, FrameworkElement reference)
        {
            if (injected != null) return;
            if (parent == null) return;

            var control = new Views.AutoSystemBatteryReplacementControl
            {
                IsHitTestVisible = false,
                Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0)
            };

            // Position by copying grid placement from reference
            if (reference != null)
            {
                try { Grid.SetRow(control, Grid.GetRow(reference)); } catch { }
                try { Grid.SetColumn(control, Grid.GetColumn(reference)); } catch { }
                try { Grid.SetRowSpan(control, Grid.GetRowSpan(reference)); } catch { }
                try { Grid.SetColumnSpan(control, Grid.GetColumnSpan(reference)); } catch { }

                // If reference has RenderTransform (e.g., ScaleTransform), reuse it for consistent sizing
                try { control.RenderTransform = reference.RenderTransform; } catch { }
                try { control.Margin = reference.Margin; } catch { }
                try { control.HorizontalAlignment = reference.HorizontalAlignment; } catch { }
                try { control.VerticalAlignment = reference.VerticalAlignment; } catch { }
            }

            parent.Children.Add(control);
            injected = control;
        }

        private Panel GetParentPanel(FrameworkElement elem)
        {
            if (elem == null) return null;
            DependencyObject current = elem;
            while (current != null)
            {
                current = VisualTreeHelper.GetParent(current);
                if (current is Panel p)
                {
                    return p;
                }
            }
            return null;
        }

        private bool IsEffectivelyVisible(FrameworkElement elem)
        {
            if (elem == null) return false;
            if (elem.Visibility != Visibility.Visible) return false;
            if (!elem.IsVisible) return false;
            if (elem.Opacity <= 0.01) return false;
            // If transformed scale is ~0, treat as hidden
            try
            {
                if (elem.RenderTransform is ScaleTransform st)
                {
                    if (Math.Abs(st.ScaleX) < 0.05 || Math.Abs(st.ScaleY) < 0.05)
                        return false;
                }
            }
            catch { }
            // Ensure it has size
            if (elem.ActualWidth < 1 || elem.ActualHeight < 1) return false;
            return true;
        }

        private void RemoveInjected()
        {
            if (injected == null) return;
            var parent = VisualTreeHelper.GetParent(injected) as Panel;
            try { parent?.Children.Remove(injected); } catch { }
            injected = null;
        }

        private FrameworkElement FindByName(DependencyObject root, string name)
        {
            if (root is FrameworkElement fe && fe.Name == name)
            {
                return fe;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = FindByName(child, name);
                if (found != null) return found;
            }

            return null;
        }
    }

    // Custom power status implementation that reads DualSense battery
    public class DualSensePowerStatus : INotifyPropertyChanged, IDisposable
    {
        private readonly string helperPath;
        private readonly SynchronizationContext context;
        private CancellationTokenSource watcherToken;
        private Task currentTask;

        // Performance optimization: highly optimized adaptive polling intervals
        // DualSense battery changes slowly (2-4 hours to discharge, 1-2 hours to charge)
        // 3-5 minute intervals provide 90%+ CPU reduction while maintaining adequate responsiveness
        private const int NORMAL_POLL_INTERVAL = 300000; // 5 minutes (300 seconds) - discharging
        private const int FAST_POLL_INTERVAL = 180000;   // 3 minutes (180 seconds) - charging  
        private const int SLOW_POLL_INTERVAL = 600000;   // 10 minutes (600 seconds) - disconnected
        private const int INITIAL_DETECTION_INTERVAL = 5000; // 5 seconds - for initial connection detection
        private const int INITIAL_DETECTION_DURATION = 30000; // 30 seconds - how long to use fast detection
        private int currentPollInterval = INITIAL_DETECTION_INTERVAL;
        
        // Fast initial connection detection
        private DateTime lastConnectionTime = DateTime.MinValue;
        private bool isInInitialDetectionMode = true;

        private int _percentCharge = 0;
        private bool _isCharging = false;
        private bool _isBatteryAvailable = false;
        private BatteryChargeLevel _charge = BatteryChargeLevel.Critical;

        public int PercentCharge
        {
            get => _percentCharge;
            private set
            {
                if (_percentCharge != value)
                {
                    _percentCharge = value;
                    OnPropertyChanged();
                    UpdateChargeLevel();
                }
            }
        }

        public BatteryChargeLevel Charge
        {
            get => _charge;
            private set
            {
                if (_charge != value)
                {
                    _charge = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsCharging
        {
            get => _isCharging;
            private set
            {
                if (_isCharging != value)
                {
                    _isCharging = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsBatteryAvailable
        {
            get => _isBatteryAvailable;
            private set
            {
                if (_isBatteryAvailable != value)
                {
                    _isBatteryAvailable = value;
                    OnPropertyChanged();
                }
            }
        }

        public DualSensePowerStatus()
        {
            context = SynchronizationContext.Current;
            var pluginDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            helperPath = Path.Combine(pluginDir ?? "", "Helper", "DualSenseBatteryHelper.exe");
            
            StartWatcher();
        }

        public async void StartWatcher()
        {
            watcherToken?.Cancel();
            if (currentTask != null)
            {
                await currentTask;
            }

            watcherToken = new CancellationTokenSource();
            currentTask = Task.Run(async () =>
            {
                while (true)
                {
                    if (watcherToken.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        var reading = GetDualSenseReading();
                        if (reading != null)
                        {
                            context.Post((a) => ApplyReading(reading), null);
                        }
                    }
                    catch
                    {
                        // Ignore errors, don't crash the watcher
                    }

                    // Use adaptive polling interval based on battery state
                    await Task.Delay(currentPollInterval);
                }
            }, watcherToken.Token);
        }

        private BatteryReading GetDualSenseReading()
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
                    proc.WaitForExit(1500);

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

            int end = i;
            while (end < src.Length && (char.IsWhiteSpace(src[end]) || (src[end] >= '0' && src[end] <= '9')))
                end++;

            var num = src.Substring(i, end - i).Trim();
            int val;
            return int.TryParse(num, out val) ? val : 0;
        }

        private void ApplyReading(BatteryReading r)
        {
            IsBatteryAvailable = r.Connected;
            
            if (r.Connected)
            {
                PercentCharge = r.Level;
                IsCharging = r.Charging;
                
                // Update connection time for fast detection mode
                lastConnectionTime = DateTime.Now;
                
                // If we're in initial detection mode and found a connection, switch to normal polling
                if (isInInitialDetectionMode)
                {
                    isInInitialDetectionMode = false;
                    int newInterval = r.Charging ? FAST_POLL_INTERVAL : NORMAL_POLL_INTERVAL;
                    if (newInterval != currentPollInterval)
                    {
                        currentPollInterval = newInterval;
                        StartWatcher(); // Restart with optimized interval
                    }
                    return;
                }
                
                // Normal adaptive polling: faster when charging, normal when discharging
                int adaptiveInterval = r.Charging ? FAST_POLL_INTERVAL : NORMAL_POLL_INTERVAL;
                if (adaptiveInterval != currentPollInterval)
                {
                    currentPollInterval = adaptiveInterval;
                    StartWatcher(); // Restart with new interval
                }
            }
            else
            {
                PercentCharge = 0;
                IsCharging = false;
                
                // If we're in initial detection mode, check if we should exit it
                if (isInInitialDetectionMode)
                {
                    var timeSinceStart = DateTime.Now - lastConnectionTime;
                    if (timeSinceStart.TotalMilliseconds > INITIAL_DETECTION_DURATION)
                    {
                        isInInitialDetectionMode = false;
                        currentPollInterval = SLOW_POLL_INTERVAL;
                        StartWatcher(); // Switch to slow polling for disconnected state
                    }
                    return;
                }
                
                // Slower polling when disconnected to save resources
                if (currentPollInterval != SLOW_POLL_INTERVAL)
                {
                    currentPollInterval = SLOW_POLL_INTERVAL;
                    StartWatcher();
                }
            }
        }

        public void UpdateBatteryStatus(bool connected, int level, bool charging)
        {
            IsBatteryAvailable = connected;
            
            if (connected)
            {
                PercentCharge = level;
                IsCharging = charging;
            }
            else
            {
                PercentCharge = 0;
                IsCharging = false;
            }
        }

        private void UpdateChargeLevel()
        {
            var charge = PercentCharge;
            if (charge > 85)
            {
                Charge = BatteryChargeLevel.High;
            }
            else if (charge > 40)
            {
                Charge = BatteryChargeLevel.Medium;
            }
            else if (charge > 10)
            {
                Charge = BatteryChargeLevel.Low;
            }
            else
            {
                Charge = BatteryChargeLevel.Critical;
            }
        }

        public async void StopWatcher()
        {
            watcherToken?.Cancel();
            if (currentTask != null)
            {
                await currentTask;
            }
        }

        public void Dispose()
        {
            StopWatcher();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private class BatteryReading
        {
            public bool Connected { get; set; }
            public int Level { get; set; }
            public bool Charging { get; set; }
            public bool Full { get; set; }
        }
    }

    public enum BatteryChargeLevel
    {
        Critical,
        Low,
        Medium,
        High
    }
}
