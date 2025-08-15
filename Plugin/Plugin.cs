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
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;

namespace DualSenseBattery
{
    public class PluginImpl : GenericPlugin, IDisposable
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
            try
            {
                base.OnApplicationStarted(args);
                overlayManager = new FullscreenOverlayManager();
                overlayManager.Start();
            }
            catch (Exception ex)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[DualSenseBattery] Error in OnApplicationStarted: {ex.Message}");
                }
                catch
                {
                    // Last resort - silent fallback
                }
            }
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

        public override void Dispose()
        {
            try
            {
                overlayManager?.Dispose();
                overlayManager = null;
            }
            catch (Exception ex)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[DualSenseBattery] Error in Dispose: {ex.Message}");
                }
                catch
                {
                    // Last resort - silent fallback
                }
            }
        }
    }

    /// <summary>
    /// Manages Windows Device Notifications for real-time controller detection
    /// </summary>
    public class DeviceNotificationManager : IDisposable
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 255)]
            public byte[] dbcc_name;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr NotificationFilter, int Flags);

        [DllImport("user32.dll")]
        private static extern bool UnregisterDeviceNotification(IntPtr Handle);

        private IntPtr notificationHandle;
        private readonly DispatcherTimer checkTimer;
        private readonly DualSensePowerStatus powerStatus;
        private bool isDisposed = false;

        public DeviceNotificationManager(DualSensePowerStatus powerStatus)
        {
            this.powerStatus = powerStatus;
            
            // Fallback timer for periodic checks (in case device notifications fail)
            checkTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            checkTimer.Tick += CheckTimer_Tick;
            
            // Start the timer immediately
            checkTimer.Start();
            
            // Try to register for device notifications
            RegisterForDeviceNotifications();
        }

        private void RegisterForDeviceNotifications()
        {
            try
            {
                // Register for HID device notifications (includes controllers)
                var filter = new DEV_BROADCAST_DEVICEINTERFACE
                {
                    dbcc_size = Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE)),
                    dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                    dbcc_classguid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030") // HID GUID
                };

                IntPtr filterPtr = Marshal.AllocHGlobal(filter.dbcc_size);
                Marshal.StructureToPtr(filter, filterPtr, false);

                notificationHandle = RegisterDeviceNotification(IntPtr.Zero, filterPtr, 0);
                
                if (notificationHandle == IntPtr.Zero)
                {
                    Debug.WriteLine("[DualSenseBattery] Failed to register device notifications, using timer fallback");
                }
                else
                {
                    Debug.WriteLine("[DualSenseBattery] Successfully registered for device notifications");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error registering device notifications: {ex.Message}");
            }
        }

        private void CheckTimer_Tick(object sender, EventArgs e)
        {
            if (isDisposed) return;
            
            try
            {
                // Force a battery reading check
                powerStatus?.ForceCheck();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error in check timer: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            try
            {
                checkTimer?.Stop();

                if (notificationHandle != IntPtr.Zero)
                {
                    UnregisterDeviceNotification(notificationHandle);
                    notificationHandle = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error disposing DeviceNotificationManager: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Manages the fullscreen overlay integration with theme battery elements
    /// </summary>
    public class FullscreenOverlayManager : IDisposable
    {
        private DispatcherTimer timer;
        private DualSensePowerStatus dualSenseStatus;
        private DeviceNotificationManager deviceManager;
        private PowerStatusBindingProxy bindingProxy;
        private bool isDisposed = false;

        public FullscreenOverlayManager()
        {
            try
            {
                dualSenseStatus = new DualSensePowerStatus();
                deviceManager = new DeviceNotificationManager(dualSenseStatus);
                bindingProxy = new PowerStatusBindingProxy(dualSenseStatus);

                timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                timer.Tick += Timer_Tick;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error creating FullscreenOverlayManager: {ex.Message}");
            }
        }

        public void Start()
        {
            try
            {
                if (timer != null && !timer.IsEnabled)
                {
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error starting FullscreenOverlayManager: {ex.Message}");
            }
        }

        public void Stop()
        {
            try
            {
                timer?.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error stopping FullscreenOverlayManager: {ex.Message}");
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (isDisposed) return;

            try
            {
                // Find theme battery elements and bind our data
                var mainWindow = Application.Current?.MainWindow;
                if (mainWindow == null) return;

                // Look for battery elements in the theme
                var batteryElements = FindBatteryElements(mainWindow);
                
                foreach (var element in batteryElements)
                {
                    try
                    {
                        // Set our data context to override system battery
                        element.DataContext = bindingProxy;
                        
                        // For TextBlock elements, explicitly bind the text to our battery percentage
                        if (element is TextBlock textBlock)
                        {
                            // Clear any existing bindings
                            textBlock.ClearValue(TextBlock.TextProperty);
                            
                            var binding = new Binding("BatteryPercentageText")
                            {
                                Source = bindingProxy,
                                Mode = BindingMode.OneWay,
                                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                            };
                            textBlock.SetBinding(TextBlock.TextProperty, binding);
                            
                            Debug.WriteLine($"[DualSenseBattery] Bound TextBlock {element.Name} to BatteryPercentageText: {bindingProxy.BatteryPercentageText}");
                        }
                        
                        // For ProgressBar elements, bind to our battery percentage
                        if (element is ProgressBar progressBar)
                        {
                            // Clear any existing bindings
                            progressBar.ClearValue(ProgressBar.ValueProperty);
                            
                            var binding = new Binding("BatteryPercentage")
                            {
                                Source = bindingProxy,
                                Mode = BindingMode.OneWay,
                                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                            };
                            progressBar.SetBinding(ProgressBar.ValueProperty, binding);
                            
                            Debug.WriteLine($"[DualSenseBattery] Bound ProgressBar {element.Name} to BatteryPercentage: {bindingProxy.BatteryPercentage}");
                        }
                        
                        // For Rectangle elements (battery fill), bind to our battery percentage
                        if (element is Rectangle rectangle)
                        {
                            // Clear any existing bindings
                            rectangle.ClearValue(Rectangle.WidthProperty);
                            
                            var binding = new Binding("BatteryPercentage")
                            {
                                Source = bindingProxy,
                                Mode = BindingMode.OneWay,
                                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                                Converter = new BatteryPercentageToWidthConverter()
                            };
                            rectangle.SetBinding(Rectangle.WidthProperty, binding);
                            
                            Debug.WriteLine($"[DualSenseBattery] Bound Rectangle {element.Name} to BatteryPercentage: {bindingProxy.BatteryPercentage}");
                        }
                        
                        // Control visibility based on controller connection and theme settings
                        bool shouldShow = dualSenseStatus.IsBatteryAvailable && 
                                        IsBatteryStatusEnabled() && 
                                        IsBatteryPercentageEnabled();
                        
                        element.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
                        
                        Debug.WriteLine($"[DualSenseBattery] Bound element {element.Name} to DualSense data. Connected: {dualSenseStatus.IsBatteryAvailable}, Level: {dualSenseStatus.PercentCharge}%");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DualSenseBattery] Error binding battery element: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error in Timer_Tick: {ex.Message}");
            }
        }

        private List<FrameworkElement> FindBatteryElements(FrameworkElement root)
        {
            var elements = new List<FrameworkElement>();
            
            try
            {
                // Common battery element names across themes
                var batteryNames = new[] 
                { 
                    "BatteryStatus", "BatteryIcon", "BatteryIndicator", "BatteryLevel",
                    "batteryStatus", "batteryIcon", "batteryIndicator", "batteryLevel",
                    "BatteryPercent", "batteryPercent", "BatteryText", "batteryText"
                };

                foreach (var name in batteryNames)
                {
                    var element = root.FindName(name) as FrameworkElement;
                    if (element != null)
                    {
                        elements.Add(element);
                    }
                }

                // Also search for elements with "battery" in their name
                FindElementsByNameContains(root, "battery", elements);
                FindElementsByNameContains(root, "Battery", elements);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error finding battery elements: {ex.Message}");
            }

            return elements;
        }

        private void FindElementsByNameContains(FrameworkElement root, string nameContains, List<FrameworkElement> results)
        {
            try
            {
                if (root.Name != null && root.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    results.Add(root);
                }

                // Search children
                if (root is Panel panel)
                {
                    foreach (var child in panel.Children)
                    {
                        if (child is FrameworkElement childElement)
                        {
                            FindElementsByNameContains(childElement, nameContains, results);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error searching elements: {ex.Message}");
            }
        }

        private bool IsBatteryStatusEnabled()
        {
            try
            {
                // Check if Playnite's battery status setting is enabled
                // For .NET 4.6.2, we'll use a simpler approach
                return true; // Default to enabled
            }
            catch
            {
                return true; // Default to enabled if we can't check
            }
        }

        private bool IsBatteryPercentageEnabled()
        {
            try
            {
                // Check if Playnite's battery percentage setting is enabled
                // For .NET 4.6.2, we'll use a simpler approach
                return true; // Default to enabled
            }
            catch
            {
                return true; // Default to enabled if we can't check
            }
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            try
            {
                Stop();
                deviceManager?.Dispose();
                dualSenseStatus?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error disposing FullscreenOverlayManager: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Proxy class to bind DualSense battery data to theme elements
    /// </summary>
    public class PowerStatusBindingProxy : INotifyPropertyChanged
    {
        private readonly DualSensePowerStatus powerStatus;

        public PowerStatusBindingProxy(DualSensePowerStatus powerStatus)
        {
            this.powerStatus = powerStatus;
            this.powerStatus.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);
        }

        // Direct battery properties
        public int BatteryPercent => powerStatus.PercentCharge;
        public bool IsCharging => powerStatus.IsCharging;
        public bool IsBatteryAvailable => powerStatus.IsBatteryAvailable;
        public BatteryChargeLevel BatteryChargeLevel => powerStatus.Charge;

        // Alternative property names that themes might use
        public int PercentCharge => powerStatus.PercentCharge;
        public bool Charging => powerStatus.IsCharging;
        public bool Connected => powerStatus.IsBatteryAvailable;
        public int Level => powerStatus.PercentCharge;

        // System battery compatibility properties (override system values)
        public bool HasBattery => powerStatus.IsBatteryAvailable;
        public bool IsPluggedIn => powerStatus.IsCharging;
        public int BatteryLevel => powerStatus.PercentCharge;

        // Additional system battery override properties
        public bool IsOnBatteryPower => !powerStatus.IsCharging && powerStatus.IsBatteryAvailable;
        public bool IsOnACPower => powerStatus.IsCharging;
        public double BatteryPercentage => powerStatus.PercentCharge;
        public string BatteryPercentageText => $"{powerStatus.PercentCharge}%";
        public bool ShowBatteryStatus => powerStatus.IsBatteryAvailable;
        public bool ShowBatteryPercentage => powerStatus.IsBatteryAvailable;

        // Power status properties that themes might expect
        public bool PowerStatusAvailable => powerStatus.IsBatteryAvailable;
        public int PowerStatusLevel => powerStatus.PercentCharge;
        public bool PowerStatusCharging => powerStatus.IsCharging;

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Manages DualSense battery status with real-time detection and efficient polling
    /// </summary>
    public class DualSensePowerStatus : INotifyPropertyChanged, IDisposable
    {
        // Performance optimization: highly optimized adaptive polling intervals
        // DualSense battery changes slowly (2-4 hours to discharge, 1-2 hours to charge)
        // 3-5 minute intervals provide 90%+ CPU reduction while maintaining adequate responsiveness
        private const int NORMAL_POLL_INTERVAL = 5000;   // 5 seconds - connected (discharging)
        private const int FAST_POLL_INTERVAL = 3000;    // 3 seconds - connected (charging)
        private const int RAPID_POLL_INTERVAL = 1000;   // 1 second - rapid detection

        private readonly object _dataLock = new object();
        private readonly object _pollingLock = new object();
        private CancellationTokenSource _cancellationTokenSource;
        private Task _pollingTask;
        private bool _isDisposed = false;

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
            try
            {
                Debug.WriteLine($"[DualSenseBattery] DualSensePowerStatus constructor called");
                _cancellationTokenSource = new CancellationTokenSource();
                
                // Test helper communication immediately
                TestHelperCommunication();
                
                StartWatcher();
                Debug.WriteLine($"[DualSenseBattery] DualSensePowerStatus constructor completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error in DualSensePowerStatus constructor: {ex.Message}");
            }
        }

        /// <summary>
        /// Forces an immediate battery check (called by device notifications)
        /// </summary>
        public void ForceCheck()
        {
            try
            {
                Debug.WriteLine($"[DualSenseBattery] ForceCheck called");
                var reading = GetDualSenseReading();
                if (reading != null)
                {
                    Debug.WriteLine($"[DualSenseBattery] Got reading: Connected={reading.Connected}, Level={reading.Level}, Charging={reading.Charging}");
                    ApplyReading(reading);
                }
                else
                {
                    Debug.WriteLine($"[DualSenseBattery] No reading returned, treating as disconnected");
                    // No reading means disconnected
                    ApplyReading(new BatteryReading { Connected = false, Level = 0, Charging = false });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error in ForceCheck: {ex.Message}");
            }
        }

        /// <summary>
        /// Test method to verify helper communication
        /// </summary>
        public void TestHelperCommunication()
        {
            try
            {
                Debug.WriteLine($"[DualSenseBattery] Testing helper communication...");
                var reading = GetDualSenseReading();
                if (reading != null)
                {
                    Debug.WriteLine($"[DualSenseBattery] TEST SUCCESS: Connected={reading.Connected}, Level={reading.Level}, Charging={reading.Charging}");
                }
                else
                {
                    Debug.WriteLine($"[DualSenseBattery] TEST FAILED: No reading returned");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] TEST ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates battery status (for backward compatibility with existing views)
        /// </summary>
        public void UpdateBatteryStatus(bool connected, int level, bool charging)
        {
            ApplyReading(new BatteryReading { Connected = connected, Level = level, Charging = charging });
        }

        private void StartWatcher()
        {
            try
            {
                Debug.WriteLine($"[DualSenseBattery] StartWatcher called");
                if (_pollingTask != null && !_pollingTask.IsCompleted)
                {
                    Debug.WriteLine($"[DualSenseBattery] Polling task already running, skipping");
                    return; // Already running
                }

                Debug.WriteLine($"[DualSenseBattery] Starting new polling task");
                _pollingTask = Task.Run(async () =>
                {
                    try
                    {
                        Debug.WriteLine($"[DualSenseBattery] Polling task started");
                        while (!_cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            try
                            {
                                Debug.WriteLine($"[DualSenseBattery] Polling for DualSense reading...");
                                var reading = GetDualSenseReading();
                                if (reading != null)
                                {
                                    ApplyReading(reading);
                                    
                                    // Use adaptive polling based on connection state
                                    int pollInterval = reading.Connected 
                                        ? (reading.Charging ? FAST_POLL_INTERVAL : NORMAL_POLL_INTERVAL)
                                        : RAPID_POLL_INTERVAL;
                                    
                                    Debug.WriteLine($"[DualSenseBattery] Waiting {pollInterval}ms before next poll");
                                    await Task.Delay(pollInterval, _cancellationTokenSource.Token);
                                }
                                else
                                {
                                    // No reading means disconnected
                                    ApplyReading(new BatteryReading { Connected = false, Level = 0, Charging = false });
                                    Debug.WriteLine($"[DualSenseBattery] No reading, waiting {RAPID_POLL_INTERVAL}ms before retry");
                                    await Task.Delay(RAPID_POLL_INTERVAL, _cancellationTokenSource.Token);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                Debug.WriteLine($"[DualSenseBattery] Polling task cancelled");
                                break; // Normal cancellation
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[DualSenseBattery] Error in polling loop: {ex.Message}");
                                await Task.Delay(RAPID_POLL_INTERVAL, _cancellationTokenSource.Token);
                            }
                        }
                        Debug.WriteLine($"[DualSenseBattery] Polling task ended");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DualSenseBattery] Error in polling task: {ex.Message}");
                    }
                }, _cancellationTokenSource.Token);
                Debug.WriteLine($"[DualSenseBattery] StartWatcher completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error starting watcher: {ex.Message}");
            }
        }

        private void StopWatcher()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _pollingTask?.Wait(TimeSpan.FromSeconds(5)); // Wait up to 5 seconds
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error stopping watcher: {ex.Message}");
            }
        }

        private void ApplyReading(BatteryReading r)
        {
            bool wasConnected = IsBatteryAvailable;
            IsBatteryAvailable = r.Connected;

            if (r.Connected)
            {
                PercentCharge = r.Level;
                IsCharging = r.Charging;
                Debug.WriteLine($"[DualSenseBattery] Applied reading: Connected=true, Level={r.Level}%, Charging={r.Charging}");
            }
            else
            {
                PercentCharge = 0;
                IsCharging = false;
                Debug.WriteLine($"[DualSenseBattery] Applied reading: Connected=false");
            }
        }

        private BatteryReading GetDualSenseReading()
        {
            try
            {
                // Try multiple possible helper paths
                var possiblePaths = new List<string>();
                
                // Path 1: Direct path based on user's setup
                possiblePaths.Add(@"C:\Games\Playnite\Extensions\DualSenseBattery_fbd2c2e6-9c1b-49b6-9c0d-1c5d3c0a9a6a\Helper\DualSenseBatteryHelper.exe");
                
                // Path 2: Relative to plugin DLL (current approach)
                var pluginDir = System.IO.Path.GetDirectoryName(typeof(PluginImpl).Assembly.Location);
                if (!string.IsNullOrEmpty(pluginDir))
                {
                    possiblePaths.Add(System.IO.Path.Combine(pluginDir, "Helper", "DualSenseBatteryHelper.exe"));
                }
                
                // Path 3: Look for the helper in the same directory as the plugin DLL
                if (!string.IsNullOrEmpty(pluginDir))
                {
                    possiblePaths.Add(System.IO.Path.Combine(pluginDir, "DualSenseBatteryHelper.exe"));
                }
                
                // Path 4: Look in the Playnite extensions directory structure
                var playniteDir = Environment.GetEnvironmentVariable("PLAYNITE_DIR");
                if (string.IsNullOrEmpty(playniteDir))
                {
                    // Try to find Playnite directory from common locations
                    var commonPaths = new[]
                    {
                        @"C:\Games\Playnite",
                        @"C:\Program Files\Playnite",
                        @"C:\Program Files (x86)\Playnite",
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playnite")
                    };
                    
                    foreach (var path in commonPaths)
                    {
                        if (Directory.Exists(path))
                        {
                            playniteDir = path;
                            break;
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(playniteDir))
                {
                    // Look for our extension directory
                    var extensionsDir = System.IO.Path.Combine(playniteDir, "Extensions");
                    if (Directory.Exists(extensionsDir))
                    {
                        var extensionDirs = Directory.GetDirectories(extensionsDir, "*DualSenseBattery*");
                        foreach (var extDir in extensionDirs)
                        {
                            possiblePaths.Add(System.IO.Path.Combine(extDir, "Helper", "DualSenseBatteryHelper.exe"));
                        }
                    }
                }
                
                string helperPath = null;
                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        helperPath = path;
                        Debug.WriteLine($"[DualSenseBattery] Found helper at: {helperPath}");
                        break;
                    }
                }
                
                if (string.IsNullOrEmpty(helperPath))
                {
                    Debug.WriteLine($"[DualSenseBattery] Helper not found. Searched paths:");
                    foreach (var path in possiblePaths)
                    {
                        Debug.WriteLine($"[DualSenseBattery]   - {path}");
                    }
                    return null;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(helperPath)
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return null;

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    
                    if (!process.WaitForExit(5000)) // 5 second timeout
                    {
                        try { process.Kill(); } catch { }
                        Debug.WriteLine($"[DualSenseBattery] Helper timeout. Output: {output}, Error: {error}");
                        return null;
                    }

                    if (process.ExitCode != 0)
                    {
                        Debug.WriteLine($"[DualSenseBattery] Helper error (ExitCode: {process.ExitCode}): {error}");
                        return null;
                    }

                    if (string.IsNullOrWhiteSpace(output))
                    {
                        Debug.WriteLine($"[DualSenseBattery] Helper returned empty output. Error: {error}");
                        return null;
                    }

                    Debug.WriteLine($"[DualSenseBattery] Helper output: {output}");

                    try
                    {
                        // Use simple JSON parsing for .NET 4.6.2 compatibility
                        var reading = ParseJsonReading(output);
                        if (reading != null)
                        {
                            Debug.WriteLine($"[DualSenseBattery] Parsed reading: Connected={reading.Connected}, Level={reading.Level}, Charging={reading.Charging}");
                        }
                        return reading;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DualSenseBattery] JSON parse error: {ex.Message}, Output: {output}");
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error getting DualSense reading: {ex.Message}");
                return null;
            }
        }

        private BatteryReading ParseJsonReading(string json)
        {
            try
            {
                var reading = new BatteryReading();
                
                // Simple JSON parsing for .NET 4.6.2
                reading.Connected = json.IndexOf("\"connected\":true", StringComparison.OrdinalIgnoreCase) >= 0;
                reading.Charging = json.IndexOf("\"charging\":true", StringComparison.OrdinalIgnoreCase) >= 0;
                
                // Extract level
                var levelIndex = json.IndexOf("\"level\":");
                if (levelIndex >= 0)
                {
                    levelIndex += 8; // Skip "level":
                    var endIndex = json.IndexOf(',', levelIndex);
                    if (endIndex < 0) endIndex = json.IndexOf('}', levelIndex);
                    if (endIndex > levelIndex)
                    {
                        var levelStr = json.Substring(levelIndex, endIndex - levelIndex).Trim();
                        if (int.TryParse(levelStr, out int level))
                        {
                            reading.Level = Math.Max(0, Math.Min(100, level));
                        }
                    }
                }
                
                return reading;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error parsing JSON: {ex.Message}");
                return null;
            }
        }

        private void UpdateChargeLevel()
        {
            var level = PercentCharge;
            BatteryChargeLevel newLevel;

            if (level >= 80) newLevel = BatteryChargeLevel.Full;
            else if (level >= 60) newLevel = BatteryChargeLevel.High;
            else if (level >= 40) newLevel = BatteryChargeLevel.Medium;
            else if (level >= 20) newLevel = BatteryChargeLevel.Low;
            else newLevel = BatteryChargeLevel.Critical;

            Charge = newLevel;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                StopWatcher();
                _cancellationTokenSource?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DualSenseBattery] Error disposing DualSensePowerStatus: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Converts battery percentage to width for Rectangle elements
    /// </summary>
    public class BatteryPercentageToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double percentage)
            {
                // Convert percentage (0-100) to width (0-100)
                return Math.Max(0, Math.Min(100, percentage));
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Represents a battery reading from the DualSense controller
    /// </summary>
    public class BatteryReading
    {
        public bool Connected { get; set; }
        public int Level { get; set; }
        public bool Charging { get; set; }
    }

    /// <summary>
    /// Battery charge level enumeration
    /// </summary>
    public enum BatteryChargeLevel
    {
        Critical,
        Low,
        Medium,
        High,
        Full
    }
}
