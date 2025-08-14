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
using System.Windows.Data;

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
        private FrameworkElement batteryPercent; // theme battery percentage text
		private FrameworkElement injected;
		private PowerStatusBindingProxy bindingProxy;
		private DualSensePowerStatus dualSenseStatus;
        private readonly object _elementLock = new object(); // Thread safety for UI elements

        public void Start()
        {
            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += Timer_Tick;
            timer.Start();

			// Initialize DualSense power status and binding proxy for default-theme rendering
			dualSenseStatus = new DualSensePowerStatus();
			bindingProxy = new PowerStatusBindingProxy(dualSenseStatus);
        }

        private void LogError(string operation, Exception ex)
        {
            try
            {
                // Use Playnite's logging if available, otherwise fallback to debug output
                System.Diagnostics.Debug.WriteLine($"[DualSenseBattery] Error in {operation}: {ex.Message}");
            }
            catch
            {
                // Last resort - silent fallback to prevent logging errors from causing issues
            }
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
                lock (_elementLock)
                {
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

                        // Also capture outer root when present (PS5 Reborn uses an outer 'Battery' grid)
                        batteryRoot = FindByName(main, "Battery");
                    }

				    // Find percentage element by common names from default theme
				    if (batteryPercent == null)
				    {
					    batteryPercent = FindByName(main, "PART_TextBatteryPercentage")
									 ?? FindByName(main, "TextBatteryPercentage")
									 ?? FindByName(main, "BatteryPercentage");
				    }

                    // Also cache the outer container (PS5 Reborn uses Grid x:Name="Battery")
                    if (batteryRoot == null)
                    {
                        batteryRoot = FindByName(main, "Battery") ?? FindByName(main, "BatteryContainer");
                    }
                }

                // If neither host nor percentage element is present, skip this tick
                if (batteryHost == null && batteryPercent == null)
                {
                    RemoveInjected();
                    return;
                }

				// New approach: Feed our PowerStatus to the theme battery controls and avoid overlays
				RemoveInjected();
				var targets = new List<FrameworkElement>();
				lock (_elementLock)
				{
				    if (batteryHost != null) targets.Add(batteryHost);
				    if (batteryRoot != null) targets.Add(batteryRoot);
				    if (batteryPercent != null) targets.Add(batteryPercent);
				}
				
				foreach (var t in targets)
				{
					try { t.DataContext = bindingProxy; } 
					catch (Exception ex) { LogError("SetDataContext", ex); }
				}

				// Force percentage text to bind to our PowerStatus.PercentCharge so it doesn't use system battery
				try
				{
					if (batteryPercent is TextBlock percentText)
					{
						var b = new Binding("PowerStatus.PercentCharge")
						{
							Source = bindingProxy,
							Mode = BindingMode.OneWay,
							StringFormat = "{0}%"
						};
						percentText.SetBinding(TextBlock.TextProperty, b);
					}
				}
				catch (Exception ex) { LogError("SetPercentageBinding", ex); }

				// Respect theme toggles: only hide when controller is disconnected; do not force-show (theme toggle controls it)
				bool connected = false;
				try { connected = dualSenseStatus?.IsBatteryAvailable == true; } 
				catch (Exception ex) { LogError("CheckBatteryAvailable", ex); }
				
				if (!connected)
				{
					try
					{
						FrameworkElement customBattery = null;
						lock (_elementLock)
						{
						    customBattery = batteryRoot != null ? (FindByName(batteryRoot, "CustomBattery") as FrameworkElement) : null;
						}
						
						if (customBattery != null)
						{
							// Use SetCurrentValue so theme triggers (ShowBattery) can take effect when reconnected
							customBattery.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
						}
						else if (batteryHost is UIElement uh)
						{
							uh.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
						}
						if (batteryPercent is UIElement up)
						{
							up.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
						}
					}
					catch (Exception ex) { LogError("HideBatteryElements", ex); }
				}
            }
            catch (Exception ex)
            {
                LogError("Timer_Tick", ex);
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

            // Position by copying layout and attached properties from reference for 1:1 overlay
            if (reference != null)
            {
                CopyAttachedLayout(control, reference);
                CopyLayoutProperties(control, reference);
                // Avoid inheriting theme's scale transforms (e.g., PS5 Reborn BatteryStatus ScaleX=0.28)
                try
                {
                    if (reference.RenderTransform is ScaleTransform st && (Math.Abs(st.ScaleX - 1) > 0.01 || Math.Abs(st.ScaleY - 1) > 0.01))
                    {
                        control.RenderTransform = Transform.Identity;
                    }
                }
                catch { }
                try
                {
                    if (reference.LayoutTransform is ScaleTransform st2 && (Math.Abs(st2.ScaleX - 1) > 0.01 || Math.Abs(st2.ScaleY - 1) > 0.01))
                    {
                        control.LayoutTransform = Transform.Identity;
                    }
                }
                catch { }
                try { Panel.SetZIndex(control, Panel.GetZIndex(reference) + 1); } catch { }
            }

            parent.Children.Add(control);
            injected = control;
        }

        private void EnsureInjectedInside(Panel parent, FrameworkElement reference)
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

            if (reference != null)
            {
                CopyAttachedLayout(control, reference);
                CopyLayoutProperties(control, reference);
                try { Panel.SetZIndex(control, Panel.GetZIndex(reference) + 1); } catch { }
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

        private void RemoveInjected()
        {
            if (injected == null) return;
            var parent = VisualTreeHelper.GetParent(injected) as Panel;
            try { parent?.Children.Remove(injected); } catch { }
            injected = null;
        }

        /// <summary>
        /// Clears cached UI element references to prevent memory leaks
        /// </summary>
        private void ClearCachedElements()
        {
            lock (_elementLock)
            {
                batteryHost = null;
                batteryRoot = null;
                batteryPercent = null;
            }
        }

        /// <summary>
        /// Disposes resources and cleans up memory
        /// </summary>
        public void Dispose()
        {
            try
            {
                timer?.Stop();
                timer = null;
                
                RemoveInjected();
                ClearCachedElements();
                
                dualSenseStatus?.Dispose();
                dualSenseStatus = null;
                
                bindingProxy = null;
            }
            catch (Exception ex)
            {
                LogError("Dispose", ex);
            }
        }

        private void CopyAttachedLayout(FrameworkElement dest, FrameworkElement src)
        {
            // Grid
            try { Grid.SetRow(dest, Grid.GetRow(src)); } catch { }
            try { Grid.SetColumn(dest, Grid.GetColumn(src)); } catch { }
            try { Grid.SetRowSpan(dest, Grid.GetRowSpan(src)); } catch { }
            try { Grid.SetColumnSpan(dest, Grid.GetColumnSpan(src)); } catch { }

            // DockPanel
            try { DockPanel.SetDock(dest, DockPanel.GetDock(src)); } catch { }

            // Canvas
            try { Canvas.SetLeft(dest, Canvas.GetLeft(src)); } catch { }
            try { Canvas.SetTop(dest, Canvas.GetTop(src)); } catch { }
            try { Canvas.SetRight(dest, Canvas.GetRight(src)); } catch { }
            try { Canvas.SetBottom(dest, Canvas.GetBottom(src)); } catch { }

            // ZIndex
            try { Panel.SetZIndex(dest, Panel.GetZIndex(src)); } catch { }
        }

        private void CopyLayoutProperties(FrameworkElement dest, FrameworkElement src)
        {
            try { dest.Margin = src.Margin; } catch { }
            try { dest.HorizontalAlignment = src.HorizontalAlignment; } catch { }
            try { dest.VerticalAlignment = src.VerticalAlignment; } catch { }
            try { dest.Width = src.Width; } catch { }
            try { dest.Height = src.Height; } catch { }
            try { dest.MinWidth = src.MinWidth; } catch { }
            try { dest.MinHeight = src.MinHeight; } catch { }
            try { dest.MaxWidth = src.MaxWidth; } catch { }
            try { dest.MaxHeight = src.MaxHeight; } catch { }
            try { dest.FlowDirection = src.FlowDirection; } catch { }
            try { dest.UseLayoutRounding = src.UseLayoutRounding; } catch { }
            try { dest.SnapsToDevicePixels = src.SnapsToDevicePixels; } catch { }
            try { dest.ClipToBounds = src.ClipToBounds; } catch { }
            try { dest.RenderTransformOrigin = src.RenderTransformOrigin; } catch { }

            try
            {
                if (src.LayoutTransform is System.Windows.Media.Transform lt)
                {
                    dest.LayoutTransform = TryCloneTransform(lt) ?? lt;
                }
            }
            catch { }

            try
            {
                if (src.RenderTransform is System.Windows.Media.Transform rt)
                {
                    dest.RenderTransform = TryCloneTransform(rt) ?? rt;
                }
            }
            catch { }
        }

        private System.Windows.Media.Transform TryCloneTransform(System.Windows.Media.Transform t)
        {
            try
            {
                if (t is System.Windows.Freezable f)
                {
                    var clone = f.CloneCurrentValue() as System.Windows.Media.Transform;
                    return clone;
                }
            }
            catch { }
            return null;
        }

        private bool IsEffectivelyVisible(UIElement elem)
        {
            if (elem == null) return false;
            if (elem.Visibility != Visibility.Visible) return false;
            if (!elem.IsVisible) return false;

            // Check opacity on element and ancestors
            double opacity = 1.0;
            DependencyObject cur = elem;
            while (cur is UIElement ui)
            {
                opacity *= ui.Opacity;
                if (opacity <= 0.01) return false;
                cur = VisualTreeHelper.GetParent(cur);
            }

            // If transformed scale is ~0, treat as hidden
            try
            {
                if (elem is FrameworkElement fe && fe.RenderTransform is ScaleTransform st)
                {
                    if (Math.Abs(st.ScaleX) < 0.05 || Math.Abs(st.ScaleY) < 0.05)
                        return false;
                }
            }
            catch { }

            // Ensure it has size when possible
            if (elem is FrameworkElement fe2)
            {
                if (fe2.ActualWidth < 1 || fe2.ActualHeight < 1) return false;
            }

            return true;
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

    internal class PowerStatusBindingProxy
    {
        public DualSensePowerStatus PowerStatus { get; }
        public PowerStatusBindingProxy(DualSensePowerStatus status)
        {
            PowerStatus = status;
        }
    }

    // Custom power status implementation that reads DualSense battery
    public class DualSensePowerStatus : INotifyPropertyChanged, IDisposable
    {
        private readonly string helperPath;
        private readonly SynchronizationContext context;
        private CancellationTokenSource watcherToken;
        private Task currentTask;
        private readonly object _pollingLock = new object(); // Thread safety for polling state
        private readonly object _dataLock = new object(); // Thread safety for battery data

        // Performance optimization: highly optimized adaptive polling intervals
        // DualSense battery changes slowly (2-4 hours to discharge, 1-2 hours to charge)
        // 3-5 minute intervals provide 90%+ CPU reduction while maintaining adequate responsiveness
        private const int NORMAL_POLL_INTERVAL = 5000;   // 5 seconds - connected (discharging)
        private const int FAST_POLL_INTERVAL = 3000;    // 3 seconds - connected (charging)
        private const int SLOW_POLL_INTERVAL = 1000;    // 1 second - disconnected
        private const int INITIAL_DETECTION_INTERVAL = 1000; // 1 second - initial detection
        private const int INITIAL_DETECTION_DURATION = 60000; // 60 seconds - initial fast window
        private const int RAPID_RETRY_INTERVAL = 1500; // 1.5 seconds - brief rapid probe after disconnect
        private const int RAPID_RETRY_DURATION = 5000; // 5 seconds - rapid probe window
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
            get 
            { 
                lock (_dataLock) return _percentCharge; 
            }
            private set
            {
                lock (_dataLock)
                {
                    if (_percentCharge != value)
                    {
                        _percentCharge = value;
                        OnPropertyChanged();
                        UpdateChargeLevel();
                    }
                }
            }
        }

        public BatteryChargeLevel Charge
        {
            get 
            { 
                lock (_dataLock) return _charge; 
            }
            private set
            {
                lock (_dataLock)
                {
                    if (_charge != value)
                    {
                        _charge = value;
                        OnPropertyChanged();
                    }
                }
            }
        }

        public bool IsCharging
        {
            get 
            { 
                lock (_dataLock) return _isCharging; 
            }
            private set
            {
                lock (_dataLock)
                {
                    if (_isCharging != value)
                    {
                        _isCharging = value;
                        OnPropertyChanged();
                    }
                }
            }
        }

        public bool IsBatteryAvailable
        {
            get 
            { 
                lock (_dataLock) return _isBatteryAvailable; 
            }
            private set
            {
                lock (_dataLock)
                {
                    if (_isBatteryAvailable != value)
                    {
                        _isBatteryAvailable = value;
                        OnPropertyChanged();
                    }
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
            lock (_pollingLock)
            {
                watcherToken?.Cancel();
            }
            
            if (currentTask != null)
            {
                await currentTask;
            }

            lock (_pollingLock)
            {
                watcherToken = new CancellationTokenSource();
                currentTask = Task.Run(async () =>
                {
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    while (true)
                    {
                        CancellationToken token;
                        lock (_pollingLock)
                        {
                            if (watcherToken.IsCancellationRequested)
                            {
                                return;
                            }
                            token = watcherToken.Token;
                        }

                        try
                        {
                            var reading = GetDualSenseReading();
                            if (reading != null)
                            {
                                if (dispatcher != null)
                                {
                                    try { dispatcher.Invoke(() => ApplyReading(reading)); } 
                                    catch (Exception ex) { LogError("DispatcherInvoke", ex); }
                                }
                                else if (context != null)
                                {
                                    try { context.Post((a) => ApplyReading(reading), null); } 
                                    catch (Exception ex) { LogError("ContextPost", ex); }
                                }
                                else
                                {
                                    ApplyReading(reading);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError("WatcherLoop", ex);
                        }

                        // Use adaptive polling interval based on battery state
                        int interval;
                        lock (_pollingLock)
                        {
                            interval = currentPollInterval;
                        }
                        await Task.Delay(interval, token);
                    }
                }, watcherToken.Token);
            }
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
            catch (Exception ex)
            {
                LogError("GetDualSenseReading", ex);
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
            catch (Exception ex)
            {
                LogError("ParseReading", ex);
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
                
                // Brief rapid probe window after disconnect for quicker re-connect detection
                if (currentPollInterval != RAPID_RETRY_INTERVAL && currentPollInterval != SLOW_POLL_INTERVAL)
                {
                    currentPollInterval = RAPID_RETRY_INTERVAL;
                    StartWatcher();
                    return;
                }

                // After rapid probe duration, fall back to slow polling
                if (currentPollInterval == RAPID_RETRY_INTERVAL)
                {
                    // Use lastConnectionTime as baseline for rapid retry timing
                    var since = DateTime.Now - lastConnectionTime;
                    if (since.TotalMilliseconds >= RAPID_RETRY_DURATION)
                    {
                        currentPollInterval = SLOW_POLL_INTERVAL;
                        StartWatcher();
                    }
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
            lock (_pollingLock)
            {
                watcherToken?.Cancel();
            }
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

        private void LogError(string operation, Exception ex)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[DualSensePowerStatus] Error in {operation}: {ex.Message}");
            }
            catch
            {
                // Last resort - silent fallback
            }
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
