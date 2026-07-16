// Quickgauge
//
// A lightweight, always-on-top desktop overlay showing live system stats.
//
// Temperature sources (priority order):
//   - LibreHardwareMonitor's web server (Options > Remote Web Server, port 8085) first.
//   - nvidia-smi as a no-install GPU fallback.
//   - The "Thermal Zone Information" perf counter as a no-admin motherboard fallback.
// There is no reliable no-admin source for real CPU die temperature without LHM.
//
// Usage metrics (CPU%, RAM%, Disk%, GPU%) use plain Win32/.NET APIs, no LHM needed.
//
// Drag the overlay with the left mouse button to reposition (clamped to stay on
// some monitor). Hover over it to reveal a header bar (vertical dots on the left,
// logo on the right); the bar pushes the metric rows down rather than overlapping
// them. Click the dots to open Settings: which rows to show, colors for every part
// of the UI, text/overlay size, header customization, a global hotkey to toggle
// visibility, and a "start with Windows" switch. Everything persists to
// %APPDATA%\Quickgauge\settings.json.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Quickgauge
{
    // Fixed, non-editable app metadata shown on the Settings page footer.
    public static class AppInfo
    {
        public const string Version = "1.0.0";
        public const string Developer = "Nazeeh Pullat";
        public const string DeveloperUrl = "https://github.com/nazeeh-pullat";
    }

    public class Reading
    {
        public bool Ok;
        public double Value;
        public string Unit = "C";
        public string Source = "";
        public string RawText; // full formatted value as LHM reports it, e.g. "1234 RPM" -- used for custom sensors
    }

    // One flattened entry from LibreHardwareMonitor's sensor tree, used to
    // populate the "add a sensor" browser in Settings.
    public class SensorEntry
    {
        public string SensorId;
        public string Path; // breadcrumb, e.g. "LEGION-NAZEEH > Intel Core i9-14900HX > Temperatures > CPU Package"
        public string Value; // raw formatted value string, e.g. "62.0 °C"
    }

    public static class Sensors
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static readonly PerformanceCounter CpuUsageCounter =
            new PerformanceCounter("Processor", "% Processor Time", "_Total", true);

        class SensorHit
        {
            public string Id;
            public string Text;
            public double Value;
        }

        static async Task<Dictionary<string, object>> FetchLhmAsync(int port)
        {
            string text = await Http.GetStringAsync("http://127.0.0.1:" + port + "/data.json");
            return (Dictionary<string, object>)Json.DeserializeObject(text);
        }

        public static async Task<bool> IsLhmAvailableAsync(int port)
        {
            try
            {
                await FetchLhmAsync(port);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Flattens the whole sensor tree (every type -- temperature, voltage, fan,
        // load, power, clock, data...) for the "add a sensor" browser in Settings.
        public static async Task<List<SensorEntry>> ListAllSensorsAsync(int port)
        {
            var tree = await FetchLhmAsync(port);
            var list = new List<SensorEntry>();
            FlattenSensors(tree, "", list);
            return list;
        }

        static void FlattenSensors(object node, string path, List<SensorEntry> list)
        {
            var dict = node as Dictionary<string, object>;
            if (dict == null) return;

            object textObj, idObj, valObj, childrenObj;
            dict.TryGetValue("Text", out textObj);
            string text = Convert.ToString(textObj);
            string newPath = string.IsNullOrEmpty(path) ? text : path + " > " + text;

            dict.TryGetValue("SensorId", out idObj);
            string id = idObj as string;
            if (!string.IsNullOrEmpty(id))
            {
                dict.TryGetValue("Value", out valObj);
                list.Add(new SensorEntry { SensorId = id, Path = newPath, Value = Convert.ToString(valObj) });
            }

            dict.TryGetValue("Children", out childrenObj);
            var children = childrenObj as object[];
            if (children != null)
            {
                foreach (var c in children) FlattenSensors(c, newPath, list);
            }
        }

        static object FindNodeById(object node, string id)
        {
            var dict = node as Dictionary<string, object>;
            if (dict == null) return null;

            object idObj;
            dict.TryGetValue("SensorId", out idObj);
            if (id.Equals(idObj as string)) return dict;

            object childrenObj;
            dict.TryGetValue("Children", out childrenObj);
            var children = childrenObj as object[];
            if (children != null)
            {
                foreach (var c in children)
                {
                    var found = FindNodeById(c, id);
                    if (found != null) return found;
                }
            }
            return null;
        }

        // Reads one user-picked sensor by its LHM SensorId. Since custom sensors can
        // be any type (RPM, volts, watts, MHz...), the raw formatted string from LHM
        // is shown as-is rather than re-interpreting units.
        public static async Task<Reading> ReadCustomAsync(int port, string sensorId)
        {
            try
            {
                var tree = await FetchLhmAsync(port);
                var node = FindNodeById(tree, sensorId) as Dictionary<string, object>;
                if (node == null) return new Reading { Ok = false, Source = "sensor not found" };
                object valObj;
                node.TryGetValue("Value", out valObj);
                string raw = Convert.ToString(valObj);
                var m = Regex.Match(raw, @"-?\d+(\.\d+)?");
                double numeric = m.Success ? double.Parse(m.Value) : double.NaN;
                return new Reading { Ok = true, Value = numeric, RawText = raw, Source = "LibreHardwareMonitor" };
            }
            catch
            {
                return new Reading { Ok = false, Source = "unavailable" };
            }
        }

        static void FindTempSensors(object node, List<SensorHit> list)
        {
            var dict = node as Dictionary<string, object>;
            if (dict == null) return;

            object idObj, valObj, textObj, childrenObj;
            dict.TryGetValue("SensorId", out idObj);
            string id = idObj as string;
            if (id != null && id.IndexOf("/temperature/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                dict.TryGetValue("Value", out valObj);
                dict.TryGetValue("Text", out textObj);
                var m = Regex.Match(Convert.ToString(valObj), @"-?\d+(\.\d+)?");
                if (m.Success)
                {
                    list.Add(new SensorHit { Id = id, Text = Convert.ToString(textObj), Value = double.Parse(m.Value) });
                }
            }

            // JavaScriptSerializer represents a JSON array as object[], not ArrayList.
            dict.TryGetValue("Children", out childrenObj);
            var children = childrenObj as object[];
            if (children != null)
            {
                foreach (var c in children) FindTempSensors(c, list);
            }
        }

        static SensorHit PickBest(List<SensorHit> sensors, Regex vendor, Regex name)
        {
            var candidates = sensors.FindAll(s => vendor.IsMatch(s.Id));
            if (candidates.Count == 0) return null;
            var named = candidates.Find(s => name.IsMatch(s.Text ?? ""));
            if (named != null) return named;
            SensorHit best = candidates[0];
            foreach (var c in candidates) if (c.Value > best.Value) best = c;
            return best;
        }

        static double ToUnit(double celsius, string unit)
        {
            return unit == "F" ? (celsius * 9.0 / 5.0) + 32.0 : celsius;
        }

        public static async Task<Reading> ReadCpuAsync(int port, string unit)
        {
            try
            {
                var list = new List<SensorHit>();
                FindTempSensors(await FetchLhmAsync(port), list);
                var cpu = PickBest(list,
                    new Regex("/(intelcpu|amdcpu|cpu)/", RegexOptions.IgnoreCase),
                    new Regex("package|tctl|tdie|average|core max", RegexOptions.IgnoreCase));
                if (cpu != null) return new Reading { Ok = true, Value = ToUnit(cpu.Value, unit), Unit = unit, Source = "LibreHardwareMonitor" };
            }
            catch { /* fall through */ }
            return new Reading { Ok = false, Unit = unit, Source = "Install LibreHardwareMonitor" };
        }

        public static async Task<Reading> ReadMotherboardAsync(int port, string unit)
        {
            try
            {
                var list = new List<SensorHit>();
                FindTempSensors(await FetchLhmAsync(port), list);
                var mobo = PickBest(list,
                    new Regex("/(lpc|mainboard|motherboard)/", RegexOptions.IgnoreCase),
                    new Regex("system|mb|motherboard|mainboard", RegexOptions.IgnoreCase));
                if (mobo != null) return new Reading { Ok = true, Value = ToUnit(mobo.Value, unit), Unit = unit, Source = "LibreHardwareMonitor" };
            }
            catch { /* fall through */ }
            try
            {
                double kelvin = await Task.Run(() => ReadAcpiPerfCounter());
                return new Reading { Ok = true, Value = ToUnit(kelvin - 273.15, unit), Unit = unit, Source = "ACPI Thermal Zone" };
            }
            catch { /* fall through */ }
            return new Reading { Ok = false, Unit = unit, Source = "Install LibreHardwareMonitor" };
        }

        public static async Task<Reading> ReadGpuAsync(int port, string unit, int gpuIndex)
        {
            try
            {
                var list = new List<SensorHit>();
                FindTempSensors(await FetchLhmAsync(port), list);
                var gpu = PickBest(list,
                    new Regex("/(gpu-nvidia|gpu-amd|gpu-intel)/", RegexOptions.IgnoreCase),
                    new Regex("^gpu core$|core|hot spot", RegexOptions.IgnoreCase));
                if (gpu != null) return new Reading { Ok = true, Value = ToUnit(gpu.Value, unit), Unit = unit, Source = "LibreHardwareMonitor" };
            }
            catch { /* fall through */ }
            try
            {
                double c = await Task.Run(() => ReadNvidiaSmiTemp(gpuIndex));
                return new Reading { Ok = true, Value = ToUnit(c, unit), Unit = unit, Source = "nvidia-smi" };
            }
            catch { /* fall through */ }
            return new Reading { Ok = false, Unit = unit, Source = "Install LibreHardwareMonitor" };
        }

        public static Task<Reading> ReadCpuUsageAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    float pct = CpuUsageCounter.NextValue();
                    return new Reading { Ok = true, Value = pct, Unit = "%", Source = "PerformanceCounter" };
                }
                catch
                {
                    return new Reading { Ok = false, Unit = "%", Source = "unavailable" };
                }
            });
        }

        public static Task<Reading> ReadRamUsageAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var status = new MEMORYSTATUSEX();
                    status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                    if (!GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("GlobalMemoryStatusEx failed");
                    return new Reading { Ok = true, Value = status.dwMemoryLoad, Unit = "%", Source = "GlobalMemoryStatusEx" };
                }
                catch
                {
                    return new Reading { Ok = false, Unit = "%", Source = "unavailable" };
                }
            });
        }

        public static Task<Reading> ReadDiskUsageAsync(string driveLetter)
        {
            return Task.Run(() =>
            {
                try
                {
                    var drive = new DriveInfo(driveLetter);
                    double usedPct = 100.0 * (1.0 - (double)drive.AvailableFreeSpace / drive.TotalSize);
                    return new Reading { Ok = true, Value = usedPct, Unit = "%", Source = "DriveInfo" };
                }
                catch
                {
                    return new Reading { Ok = false, Unit = "%", Source = "unavailable" };
                }
            });
        }

        public static async Task<Reading> ReadGpuUsageAsync(int gpuIndex)
        {
            try
            {
                double pct = await Task.Run(() => ReadNvidiaSmiUsage(gpuIndex));
                return new Reading { Ok = true, Value = pct, Unit = "%", Source = "nvidia-smi" };
            }
            catch
            {
                return new Reading { Ok = false, Unit = "%", Source = "NVIDIA only" };
            }
        }

        static double ReadAcpiPerfCounter()
        {
            var category = new PerformanceCounterCategory("Thermal Zone Information");
            string[] instances = category.GetInstanceNames();
            double max = double.MinValue;
            bool found = false;
            foreach (var inst in instances)
            {
                using (var counter = new PerformanceCounter("Thermal Zone Information", "Temperature", inst, true))
                {
                    float v = counter.NextValue();
                    if (v > 0) { found = true; if (v > max) max = v; }
                }
            }
            if (!found) throw new InvalidOperationException("no thermal zone reading");
            return max;
        }

        static double ReadNvidiaSmiTemp(int gpuIndex)
        {
            string output = RunNvidiaSmi("--id=" + gpuIndex + " --query-gpu=temperature.gpu --format=csv,noheader,nounits");
            return double.Parse(output.Trim());
        }

        static double ReadNvidiaSmiUsage(int gpuIndex)
        {
            string output = RunNvidiaSmi("--id=" + gpuIndex + " --query-gpu=utilization.gpu --format=csv,noheader,nounits");
            return double.Parse(output.Trim());
        }

        static string RunNvidiaSmi(string args)
        {
            var psi = new ProcessStartInfo("nvidia-smi", args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                return output;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }

    // Loads logo.png (next to the exe) for the splash screen, header bar, and
    // settings page. The bitmap is decoded once and cached; every caller gets an
    // Image sized by height with the source's aspect ratio preserved.
    // Loads PNG assets that live next to the exe (logo.png, icon.png, Settings.png,
    // slider.png, knob.png), caching each by filename so repeated Build()/Load()
    // calls (header, splash, settings page, every slider row...) only hit disk once.
    public static class Assets
    {
        static readonly Dictionary<string, BitmapImage> _cache = new Dictionary<string, BitmapImage>();

        public static BitmapImage Load(string filename)
        {
            BitmapImage cached;
            if (_cache.TryGetValue(filename, out cached)) return cached;

            BitmapImage bmp = null;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = System.IO.Path.Combine(dir, filename);
                if (File.Exists(path))
                {
                    bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                }
            }
            catch { bmp = null; }

            _cache[filename] = bmp; // cache the miss too, so a missing file isn't retried every call
            return bmp;
        }
    }

    public static class Logo
    {
        public static FrameworkElement Build(double height)
        {
            var bmp = Assets.Load("logo.png");
            if (bmp == null)
            {
                // logo.png missing/unreadable: fall back to plain text so the UI
                // still has something legible instead of a blank gap.
                return new TextBlock
                {
                    Text = "Quickgauge",
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.Bold,
                    FontSize = height * 0.6,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            return new Image
            {
                Source = bmp,
                Height = height,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }

    // A user-picked LibreHardwareMonitor sensor shown as an extra overlay row.
    // Warn/Crit are NaN when the user hasn't set a threshold, in which case the
    // row is always shown in the label color rather than status-colored.
    // A slider skinned with slider.png (track) and knob.png (thumb) instead of
    // WPF's default Slider chrome. Built on Canvas for simple absolute positioning
    // of the knob; drag it or click anywhere on the track to set a value.
    public class ImageSlider : Canvas
    {
        public event Action<double> ValueChanged;

        readonly double _min, _max, _trackWidth, _knobSize;
        double _value;
        readonly FrameworkElement _knob;
        bool _dragging;

        public double Value
        {
            get { return _value; }
            set { _value = Math.Max(_min, Math.Min(_max, value)); PositionKnob(); }
        }

        public ImageSlider(double min, double max, double initial, double width)
        {
            _min = min;
            _max = max;
            _trackWidth = width;
            _knobSize = 18;
            _value = Math.Max(min, Math.Min(max, initial));

            Width = width;
            Height = 22;
            Background = Brushes.Transparent; // makes the whole row hit-testable, not just the track/knob images

            var trackSource = Assets.Load("slider.png");
            UIElement track = trackSource != null
                ? (UIElement)new Image { Source = trackSource, Width = width, Height = 8, Stretch = Stretch.Fill }
                : new Border { Width = width, Height = 4, Background = Brushes.DimGray, CornerRadius = new CornerRadius(2) };
            SetLeft(track, 0);
            SetTop(track, (Height - 8) / 2);
            Children.Add(track);

            var knobSource = Assets.Load("knob.png");
            _knob = knobSource != null
                ? (FrameworkElement)new Image { Source = knobSource, Width = _knobSize, Height = _knobSize }
                : new Ellipse { Width = _knobSize, Height = _knobSize, Fill = Brushes.DodgerBlue };
            _knob.Cursor = Cursors.Hand;
            Children.Add(_knob);

            MouseLeftButtonDown += (s, e) => { _dragging = true; CaptureMouse(); UpdateFromMouse(e.GetPosition(this).X); };
            MouseMove += (s, e) => { if (_dragging) UpdateFromMouse(e.GetPosition(this).X); };
            MouseLeftButtonUp += (s, e) => { _dragging = false; ReleaseMouseCapture(); };

            PositionKnob();
        }

        void PositionKnob()
        {
            double t = (_max > _min) ? (_value - _min) / (_max - _min) : 0;
            double x = t * (_trackWidth - _knobSize);
            SetLeft(_knob, x);
            SetTop(_knob, (Height - _knobSize) / 2);
        }

        void UpdateFromMouse(double x)
        {
            double t = Math.Max(0, Math.Min(1, (x - _knobSize / 2) / (_trackWidth - _knobSize)));
            _value = _min + t * (_max - _min);
            PositionKnob();
            var cb = ValueChanged;
            if (cb != null) cb(_value);
        }
    }

    public class CustomSensor
    {
        public string SensorId = "";
        public string Label = "";
        public double Warn = double.NaN;
        public double Crit = double.NaN;
    }

    public class Settings
    {
        public double Left = -1;
        public double Top = -1;
        public double Opacity = 0.9;
        public string Unit = "C";
        public double Scale = 1.0;
        public double FontSize = 14;

        public bool ShowMobo = true;
        public bool ShowCpuTemp = true;
        public bool ShowCpuUsage = false;
        public bool ShowGpuTemp = true;
        public bool ShowGpuUsage = false;
        public bool ShowRam = false;
        public bool ShowDisk = false;

        // Display order of the built-in rows (top to bottom). Custom sensors always
        // render after these, in the order they were added.
        public List<string> RowOrder = new List<string> { "mobo", "cpu_temp", "cpu_usage", "gpu_temp", "gpu_usage", "ram", "disk" };

        public string LabelMobo = "MOBO";
        public string LabelCpuTemp = "CPU";
        public string LabelCpuUsage = "CPU%";
        public string LabelGpuTemp = "GPU";
        public string LabelGpuUsage = "GPU%";
        public string LabelRam = "RAM";
        public string LabelDisk = "DISK";

        public int LhmPort = 8085;
        public List<CustomSensor> CustomSensors = new List<CustomSensor>();

        public string ColorBackground = "#1E1E1E";
        public string ColorBorder = "#2196F3";
        public string ColorLabel = "#FFFFFF";
        public string ColorOk = "#66BB6A";
        public string ColorWarn = "#FB8C00";
        public string ColorCrit = "#E53935";

        public string ColorHeaderBackground = "#141414";
        public string ColorDots = "#B0B0B0";
        public double HeaderHeight = 30;
        public double HeaderLogoSize = 70;
        public double HeaderDotSize = 2;
        public bool HeaderAlwaysVisible = false;

        public string HotkeyDisplay = "";
        public uint HotkeyModifiers = 0;
        public uint HotkeyKey = 0;

        static string PathOnDisk
        {
            get
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Quickgauge");
                Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "settings.json");
            }
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(PathOnDisk))
                {
                    var json = new JavaScriptSerializer();
                    return json.Deserialize<Settings>(File.ReadAllText(PathOnDisk));
                }
            }
            catch { /* fall back to defaults */ }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                var json = new JavaScriptSerializer();
                File.WriteAllText(PathOnDisk, json.Serialize(this));
            }
            catch { /* non-fatal */ }
        }

        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "Quickgauge";

        public static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    return key != null && key.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        public static void SetStartupEnabled(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        string exePath = Assembly.GetExecutingAssembly().Location;
                        key.SetValue(RunValueName, "\"" + exePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch { /* non-fatal: just leave startup state unchanged */ }
        }

        // Cleans up the previous app's ("SayerTempOverlay") startup entry, if present,
        // since it would otherwise point at a deleted, moved-away binary.
        public static void CleanupLegacyStartupEntry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null && key.GetValue("SayerTempOverlay") != null)
                        key.DeleteValue("SayerTempOverlay", false);
                }
            }
            catch { /* non-fatal */ }
        }

        public static Color SafeColor(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return fallback; }
        }
    }

    public class HotkeyManager
    {
        const int WM_HOTKEY = 0x0312;
        const int HotkeyId = 0xB00F;

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        IntPtr _hwnd;
        HwndSource _source;
        public event Action Pressed;

        public void Attach(Window window)
        {
            var helper = new WindowInteropHelper(window);
            _hwnd = helper.EnsureHandle();
            _source = HwndSource.FromHwnd(_hwnd);
            _source.AddHook(WndProc);
        }

        public void Register(uint modifiers, uint vk)
        {
            Unregister();
            if (vk == 0) return;
            RegisterHotKey(_hwnd, HotkeyId, modifiers, vk);
        }

        public void Unregister()
        {
            if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                var cb = Pressed;
                if (cb != null) cb();
                handled = true;
            }
            return IntPtr.Zero;
        }
    }

    public class SplashWindow : Window
    {
        public SplashWindow(Color accentColor)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Width = 240;
            Height = 240;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var logoElement = Logo.Build(88);
            logoElement.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(logoElement);
            panel.Children.Add(new TextBlock
            {
                Text = "v" + AppInfo.Version,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14, // 20 * 0.7 -- 30% smaller than the old "Quickgauge" wordmark text
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0)
            });

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x18, 0x18, 0x18)),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(28),
                Child = panel
            };
        }
    }

    public class OverlayWindow : Window
    {
        readonly Settings _settings;
        readonly HotkeyManager _hotkey = new HotkeyManager();
        readonly DispatcherTimer _timer = new DispatcherTimer();
        bool _busy;

        Border _rootBorder;
        Border _headerBar;
        StackPanel _rowsPanel;
        UIElement _dotsIcon;
        FrameworkElement _headerLogo;

        readonly Dictionary<string, TextBlock> _rows = new Dictionary<string, TextBlock>();
        readonly Dictionary<string, TextBlock> _customRows = new Dictionary<string, TextBlock>();

        public OverlayWindow(Settings settings)
        {
            _settings = settings;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Opacity = _settings.Opacity;

            BuildUi();

            MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource == _dotsIcon || IsDescendantOf(e.OriginalSource as DependencyObject, _dotsIcon)) return;
                DragMove();
                ClampToWorkArea();
                _settings.Left = Left;
                _settings.Top = Top;
                _settings.Save();
            };
            MouseWheel += (s, e) =>
            {
                double delta = e.Delta > 0 ? 0.05 : -0.05;
                Opacity = Math.Max(0.15, Math.Min(1.0, Opacity + delta));
                _settings.Opacity = Opacity;
                _settings.Save();
            };
            MouseEnter += (s, e) => { if (!_settings.HeaderAlwaysVisible) _headerBar.Visibility = Visibility.Visible; };
            MouseLeave += (s, e) => { if (!_settings.HeaderAlwaysVisible) _headerBar.Visibility = Visibility.Collapsed; };

            SourceInitialized += (s, e) =>
            {
                _hotkey.Attach(this);
                _hotkey.Pressed += ToggleVisibility;
                if (_settings.HotkeyKey != 0) _hotkey.Register(_settings.HotkeyModifiers, _settings.HotkeyKey);
            };

            Loaded += (s, e) =>
            {
                Left = _settings.Left >= 0 ? _settings.Left : SystemParameters.WorkArea.Width - ActualWidth - 24;
                Top = _settings.Top >= 0 ? _settings.Top : 24;
                ClampToWorkArea();
                _settings.Left = Left;
                _settings.Top = Top;
                _settings.Save();
            };

            ApplyRowVisibility();
            ApplyRowOrder();
            ApplyColors();
            ApplyHeaderSettings();
            ApplyScale();
            RebuildCustomRows();

            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += async (s, e) => await RefreshAsync();
            _timer.Start();
#pragma warning disable 4014
            RefreshAsync();
#pragma warning restore 4014
        }

        static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
        {
            if (ancestor == null) return false;
            var current = child;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        void BuildUi()
        {
            // Header bar: logo on the left, a settings (gear) icon on the right
            // (click to open Settings). Lives in its own Grid row so showing/hiding
            // it pushes the metric rows below via normal layout flow, rather than
            // overlapping them.
            var settingsIcon = new Image
            {
                Source = Assets.Load("Settings.png"),
                Width = _settings.HeaderDotSize,
                Height = _settings.HeaderDotSize,
                Stretch = Stretch.Uniform,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            settingsIcon.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                OpenSettings();
            };
            _dotsIcon = settingsIcon;

            _headerLogo = Logo.Build(_settings.HeaderLogoSize);
            _headerLogo.HorizontalAlignment = HorizontalAlignment.Left;
            _headerLogo.VerticalAlignment = VerticalAlignment.Center;

            var headerGrid = new Grid();
            headerGrid.Children.Add(_headerLogo);
            headerGrid.Children.Add(settingsIcon);

            _headerBar = new Border
            {
                Padding = new Thickness(10, 4, 10, 4),
                Visibility = Visibility.Collapsed,
                Child = headerGrid
            };

            _rowsPanel = new StackPanel { Margin = new Thickness(0) };
            foreach (var key in new[] { "mobo", "cpu_temp", "cpu_usage", "gpu_temp", "gpu_usage", "ram", "disk" })
            {
                var tb = new TextBlock
                {
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                _rows[key] = tb;
                _rowsPanel.Children.Add(tb);
            }

            var outer = new StackPanel();
            outer.Children.Add(_headerBar);
            outer.Children.Add(new Border { Padding = new Thickness(14, 10, 10, 10), Child = _rowsPanel });

            _rootBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Child = outer
            };
            Content = _rootBorder;
        }

        public void OpenSettings()
        {
            var win = new SettingsWindow(_settings, this);
            win.Owner = this;
            win.Show();
            win.Activate();
        }

        public void ApplyRowVisibility()
        {
            _rows["mobo"].Visibility = _settings.ShowMobo ? Visibility.Visible : Visibility.Collapsed;
            _rows["cpu_temp"].Visibility = _settings.ShowCpuTemp ? Visibility.Visible : Visibility.Collapsed;
            _rows["cpu_usage"].Visibility = _settings.ShowCpuUsage ? Visibility.Visible : Visibility.Collapsed;
            _rows["gpu_temp"].Visibility = _settings.ShowGpuTemp ? Visibility.Visible : Visibility.Collapsed;
            _rows["gpu_usage"].Visibility = _settings.ShowGpuUsage ? Visibility.Visible : Visibility.Collapsed;
            _rows["ram"].Visibility = _settings.ShowRam ? Visibility.Visible : Visibility.Collapsed;
            _rows["disk"].Visibility = _settings.ShowDisk ? Visibility.Visible : Visibility.Collapsed;
        }

        // Re-sorts the row TextBlocks already in _rowsPanel to match
        // _settings.RowOrder, then keeps custom-sensor rows pinned after all of them.
        public void ApplyRowOrder()
        {
            foreach (var key in _settings.RowOrder)
            {
                TextBlock tb;
                if (_rows.TryGetValue(key, out tb))
                {
                    _rowsPanel.Children.Remove(tb);
                    _rowsPanel.Children.Add(tb);
                }
            }
            foreach (var tb in _customRows.Values)
            {
                _rowsPanel.Children.Remove(tb);
                _rowsPanel.Children.Add(tb);
            }
        }

        public void ApplyColors()
        {
            var bg = Settings.SafeColor(_settings.ColorBackground, Color.FromRgb(0x1E, 0x1E, 0x1E));
            var border = Settings.SafeColor(_settings.ColorBorder, Colors.DodgerBlue);
            var headerBg = Settings.SafeColor(_settings.ColorHeaderBackground, Color.FromRgb(0x14, 0x14, 0x14));
            var dots = Settings.SafeColor(_settings.ColorDots, Colors.Gray);

            _rootBorder.Background = new SolidColorBrush(Color.FromArgb(0xD0, bg.R, bg.G, bg.B));
            _rootBorder.BorderBrush = new SolidColorBrush(border);
            _rootBorder.BorderThickness = new Thickness(1);

            _headerBar.Background = new SolidColorBrush(Color.FromArgb(0xE0, headerBg.R, headerBg.G, headerBg.B));

            foreach (var child in ((StackPanel)_dotsIcon).Children)
            {
                var ellipse = child as Ellipse;
                if (ellipse != null) ellipse.Fill = new SolidColorBrush(dots);
            }

            RequestImmediateRefresh(); // re-colors row values against the (possibly changed) status colors
        }

        public void ApplyHeaderSettings()
        {
            // MinHeight, not Height: a fixed Height would clip a logo taller than the
            // bar (e.g. logo size 70 vs. header height 30) since WPF would constrain
            // the child's arrange box to exactly Height regardless of its own size.
            _headerBar.MinHeight = _settings.HeaderHeight;
            _headerLogo.Width = _settings.HeaderLogoSize;
            _headerLogo.Height = _settings.HeaderLogoSize;
            foreach (var child in ((StackPanel)_dotsIcon).Children)
            {
                var ellipse = child as Ellipse;
                if (ellipse != null) { ellipse.Width = _settings.HeaderDotSize; ellipse.Height = _settings.HeaderDotSize; }
            }
            _headerBar.Visibility = _settings.HeaderAlwaysVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ApplyScale()
        {
            _rootBorder.LayoutTransform = Math.Abs(_settings.Scale - 1.0) < 0.001
                ? null
                : new ScaleTransform(_settings.Scale, _settings.Scale);
        }

        public void ApplyOpacity()
        {
            Opacity = _settings.Opacity;
        }

        public void ApplyHotkey()
        {
            if (_settings.HotkeyKey != 0) _hotkey.Register(_settings.HotkeyModifiers, _settings.HotkeyKey);
            else _hotkey.Unregister();
        }

        public void RequestImmediateRefresh()
        {
#pragma warning disable 4014
            RefreshAsync();
#pragma warning restore 4014
        }

        public void ToggleVisibility()
        {
            if (IsVisible) Hide(); else Show();
        }

        void ClampToWorkArea()
        {
            double minLeft = SystemParameters.VirtualScreenLeft;
            double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth;
            double minTop = SystemParameters.VirtualScreenTop;
            double maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight;

            if (maxLeft < minLeft) maxLeft = minLeft;
            if (maxTop < minTop) maxTop = minTop;

            Left = Math.Max(minLeft, Math.Min(maxLeft, Left));
            Top = Math.Max(minTop, Math.Min(maxTop, Top));
        }

        async Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                string unit = _settings.Unit;
                int port = _settings.LhmPort;

                if (_settings.ShowMobo)
                    SetRow(_rows["mobo"], _settings.LabelMobo, await Sensors.ReadMotherboardAsync(port, unit), 45, 60);
                if (_settings.ShowCpuTemp)
                    SetRow(_rows["cpu_temp"], _settings.LabelCpuTemp, await Sensors.ReadCpuAsync(port, unit), 70, 85);
                if (_settings.ShowCpuUsage)
                    SetRow(_rows["cpu_usage"], _settings.LabelCpuUsage, await Sensors.ReadCpuUsageAsync(), 70, 90);
                if (_settings.ShowGpuTemp)
                    SetRow(_rows["gpu_temp"], _settings.LabelGpuTemp, await Sensors.ReadGpuAsync(port, unit, 0), 75, 90);
                if (_settings.ShowGpuUsage)
                    SetRow(_rows["gpu_usage"], _settings.LabelGpuUsage, await Sensors.ReadGpuUsageAsync(0), 80, 95);
                if (_settings.ShowRam)
                    SetRow(_rows["ram"], _settings.LabelRam, await Sensors.ReadRamUsageAsync(), 80, 95);
                if (_settings.ShowDisk)
                    SetRow(_rows["disk"], _settings.LabelDisk, await Sensors.ReadDiskUsageAsync("C"), 85, 95);

                foreach (var custom in _settings.CustomSensors)
                {
                    TextBlock tb;
                    if (_customRows.TryGetValue(custom.SensorId, out tb))
                    {
                        var reading = await Sensors.ReadCustomAsync(port, custom.SensorId);
                        SetCustomRow(tb, custom.Label, reading, custom.Warn, custom.Crit);
                    }
                }
            }
            finally
            {
                _busy = false;
            }
        }

        void SetRow(TextBlock tb, string label, Reading r, double warn, double crit)
        {
            string text = r.Ok ? Math.Round(r.Value) + (r.Unit == "%" ? "%" : "°" + r.Unit) : "N/A";

            Brush valueColor;
            if (!r.Ok) valueColor = Brushes.Gray;
            else if (r.Value >= crit) valueColor = new SolidColorBrush(Settings.SafeColor(_settings.ColorCrit, Colors.Red));
            else if (r.Value >= warn) valueColor = new SolidColorBrush(Settings.SafeColor(_settings.ColorWarn, Colors.Orange));
            else valueColor = new SolidColorBrush(Settings.SafeColor(_settings.ColorOk, Colors.LightGreen));

            var labelColor = new SolidColorBrush(Settings.SafeColor(_settings.ColorLabel, Colors.White));

            tb.FontSize = _settings.FontSize;
            tb.Inlines.Clear();
            tb.Inlines.Add(new Run(label + "   ") { Foreground = labelColor });
            tb.Inlines.Add(new Run(text) { Foreground = valueColor });
        }

        // Custom sensors can be any unit (RPM, volts, watts...), so the raw LHM
        // string is shown as-is; status coloring only kicks in if the user set
        // Warn/Crit thresholds for that particular sensor.
        void SetCustomRow(TextBlock tb, string label, Reading r, double warn, double crit)
        {
            string text = r.Ok ? (r.RawText ?? Math.Round(r.Value).ToString()) : "N/A";

            Brush valueColor;
            if (!r.Ok) valueColor = Brushes.Gray;
            else if (!double.IsNaN(crit) && r.Value >= crit) valueColor = new SolidColorBrush(Settings.SafeColor(_settings.ColorCrit, Colors.Red));
            else if (!double.IsNaN(warn) && r.Value >= warn) valueColor = new SolidColorBrush(Settings.SafeColor(_settings.ColorWarn, Colors.Orange));
            else valueColor = new SolidColorBrush(Settings.SafeColor(_settings.ColorLabel, Colors.White));

            var labelColor = new SolidColorBrush(Settings.SafeColor(_settings.ColorLabel, Colors.White));

            tb.FontSize = _settings.FontSize;
            tb.Inlines.Clear();
            tb.Inlines.Add(new Run(label + "   ") { Foreground = labelColor });
            tb.Inlines.Add(new Run(text) { Foreground = valueColor });
        }

        // Rebuilds the dynamic custom-sensor rows to match _settings.CustomSensors.
        // Called on add/remove; regular refresh ticks just update existing rows.
        public void RebuildCustomRows()
        {
            foreach (var tb in _customRows.Values) _rowsPanel.Children.Remove(tb);
            _customRows.Clear();

            foreach (var custom in _settings.CustomSensors)
            {
                var tb = new TextBlock
                {
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = _settings.FontSize,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                _customRows[custom.SensorId] = tb;
                _rowsPanel.Children.Add(tb);
            }

            RequestImmediateRefresh();
        }
    }

    public class SettingsWindow : Window
    {
        readonly Settings _settings;
        readonly OverlayWindow _owner;
        TextBox _hotkeyBox;
        bool _capturingHotkey;

        public SettingsWindow(Settings settings, OverlayWindow owner)
        {
            _settings = settings;
            _owner = owner;

            Title = "Quickgauge - Settings";
            Width = 360;
            MaxHeight = SystemParameters.PrimaryScreenHeight * 0.85;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

            var root = new StackPanel { Margin = new Thickness(16) };

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            titleRow.Children.Add(Logo.Build(32));
            root.Children.Add(titleRow);

            Action onDisplayChange = () =>
            {
                _owner.ApplyRowVisibility();
                _owner.RequestImmediateRefresh();
                _settings.Save();
            };

            root.Children.Add(Header("Startup"));
            root.Children.Add(Check("Start with Windows", Settings.IsStartupEnabled, v => Settings.SetStartupEnabled(v)));

            root.Children.Add(Divider());
            root.Children.Add(Header("Display & order"));
            root.Children.Add(new TextBlock
            {
                Text = "Use Up/Dn to change the order rows appear in.",
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var displayListPanel = new StackPanel();
            Action refreshDisplayList = null;
            refreshDisplayList = () =>
            {
                displayListPanel.Children.Clear();
                for (int i = 0; i < _settings.RowOrder.Count; i++)
                {
                    string key = _settings.RowOrder[i];
                    int index = i;

                    var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

                    var cb = new CheckBox
                    {
                        Content = RowDisplayName(key),
                        Foreground = Brushes.White,
                        IsChecked = GetShow(key),
                        VerticalAlignment = VerticalAlignment.Center,
                        Width = 210
                    };
                    cb.Checked += (s, e) => { SetShow(key, true); onDisplayChange(); };
                    cb.Unchecked += (s, e) => { SetShow(key, false); onDisplayChange(); };

                    var upBtn = MakeButton("Up");
                    upBtn.Padding = new Thickness(6, 2, 6, 2);
                    upBtn.IsEnabled = index > 0;
                    upBtn.Click += (s, e) => MoveRow(key, -1, refreshDisplayList);

                    var downBtn = MakeButton("Dn");
                    downBtn.Padding = new Thickness(6, 2, 6, 2);
                    downBtn.Margin = new Thickness(4, 0, 0, 0);
                    downBtn.IsEnabled = index < _settings.RowOrder.Count - 1;
                    downBtn.Click += (s, e) => MoveRow(key, 1, refreshDisplayList);

                    rowPanel.Children.Add(cb);
                    rowPanel.Children.Add(upBtn);
                    rowPanel.Children.Add(downBtn);
                    displayListPanel.Children.Add(rowPanel);
                }
            };
            refreshDisplayList();
            root.Children.Add(displayListPanel);

            root.Children.Add(Divider());
            root.Children.Add(Header("Rename headings"));
            root.Children.Add(TextRow("Motherboard", () => _settings.LabelMobo, v => _settings.LabelMobo = v, onDisplayChange));
            root.Children.Add(TextRow("CPU temp", () => _settings.LabelCpuTemp, v => _settings.LabelCpuTemp = v, onDisplayChange));
            root.Children.Add(TextRow("CPU usage", () => _settings.LabelCpuUsage, v => _settings.LabelCpuUsage = v, onDisplayChange));
            root.Children.Add(TextRow("GPU temp", () => _settings.LabelGpuTemp, v => _settings.LabelGpuTemp = v, onDisplayChange));
            root.Children.Add(TextRow("GPU usage", () => _settings.LabelGpuUsage, v => _settings.LabelGpuUsage = v, onDisplayChange));
            root.Children.Add(TextRow("RAM", () => _settings.LabelRam, v => _settings.LabelRam = v, onDisplayChange));
            root.Children.Add(TextRow("Disk", () => _settings.LabelDisk, v => _settings.LabelDisk = v, onDisplayChange));

            root.Children.Add(Divider());
            root.Children.Add(Header("Text & size"));
            root.Children.Add(SliderRow("Text size", 10, 24, () => _settings.FontSize, v =>
            {
                _settings.FontSize = v;
                _owner.RequestImmediateRefresh();
                _settings.Save();
            }));
            root.Children.Add(SliderRow("Overlay size", 0.6, 2.0, () => _settings.Scale, v =>
            {
                _settings.Scale = v;
                _owner.ApplyScale();
                _settings.Save();
            }));
            root.Children.Add(SliderRow("Opacity", 0.15, 1.0, () => _settings.Opacity, v =>
            {
                _settings.Opacity = v;
                _owner.ApplyOpacity();
                _settings.Save();
            }));

            root.Children.Add(Divider());
            root.Children.Add(Header("Colors"));
            Action onColorChange = () => { _owner.ApplyColors(); _settings.Save(); };
            root.Children.Add(ColorRow("Background", () => _settings.ColorBackground, v => _settings.ColorBackground = v, onColorChange));
            root.Children.Add(ColorRow("Border / accent", () => _settings.ColorBorder, v => _settings.ColorBorder = v, onColorChange));
            root.Children.Add(ColorRow("Label text", () => _settings.ColorLabel, v => _settings.ColorLabel = v, onColorChange));
            root.Children.Add(ColorRow("Value: OK", () => _settings.ColorOk, v => _settings.ColorOk = v, onColorChange));
            root.Children.Add(ColorRow("Value: Warning", () => _settings.ColorWarn, v => _settings.ColorWarn = v, onColorChange));
            root.Children.Add(ColorRow("Value: Critical", () => _settings.ColorCrit, v => _settings.ColorCrit = v, onColorChange));

            root.Children.Add(Divider());
            root.Children.Add(Header("Header bar"));
            Action onHeaderChange = () => { _owner.ApplyHeaderSettings(); _owner.ApplyColors(); _settings.Save(); };
            root.Children.Add(Check("Always show (skip hover)", () => _settings.HeaderAlwaysVisible, v => _settings.HeaderAlwaysVisible = v, onHeaderChange));
            root.Children.Add(ColorRow("Header background", () => _settings.ColorHeaderBackground, v => _settings.ColorHeaderBackground = v, onHeaderChange));
            root.Children.Add(ColorRow("Dots color", () => _settings.ColorDots, v => _settings.ColorDots = v, onHeaderChange));
            root.Children.Add(SliderRow("Header height", 20, 50, () => _settings.HeaderHeight, v => { _settings.HeaderHeight = v; onHeaderChange(); }));
            root.Children.Add(SliderRow("Header logo size", 10, 34, () => _settings.HeaderLogoSize, v => { _settings.HeaderLogoSize = v; onHeaderChange(); }));
            root.Children.Add(SliderRow("Header dot size", 2, 8, () => _settings.HeaderDotSize, v => { _settings.HeaderDotSize = v; onHeaderChange(); }));

            root.Children.Add(Divider());
            root.Children.Add(Header("LibreHardwareMonitor"));
            root.Children.Add(new TextBlock
            {
                Text = "Real CPU temperature and non-NVIDIA GPU temp/usage require LibreHardwareMonitor running with its Remote Web Server enabled. Everything else (RAM/Disk/CPU usage, NVIDIA GPU) works without it.",
                Foreground = Brushes.Gray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 8)
            });

            var lhmStatus = new TextBlock { Text = "Checking...", Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var lhmRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var recheckBtn = MakeButton("Recheck");
            var downloadBtn = MakeButton("Download LibreHardwareMonitor");
            downloadBtn.Margin = new Thickness(8, 0, 0, 0);
            downloadBtn.Click += (s, e) =>
            {
                try { Process.Start("https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/latest"); }
                catch { /* no default browser association; nothing more we can do */ }
            };
            Action checkLhm = null;
            checkLhm = async () =>
            {
                lhmStatus.Text = "Checking...";
                lhmStatus.Foreground = Brushes.Gray;
                bool ok = await Sensors.IsLhmAvailableAsync(_settings.LhmPort);
                lhmStatus.Text = ok ? "Connected" : "Not detected";
                lhmStatus.Foreground = ok ? new SolidColorBrush(Settings.SafeColor(_settings.ColorOk, Colors.LightGreen)) : new SolidColorBrush(Settings.SafeColor(_settings.ColorCrit, Colors.Red));
                downloadBtn.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            };
            recheckBtn.Click += (s, e) => checkLhm();
            lhmRow.Children.Add(lhmStatus);
            lhmRow.Children.Add(recheckBtn);
            lhmRow.Children.Add(downloadBtn);
            root.Children.Add(lhmRow);
            Loaded += (s, e) => checkLhm();

            root.Children.Add(TextRow("Port", () => _settings.LhmPort.ToString(), v =>
            {
                int parsed;
                if (int.TryParse(v, out parsed) && parsed > 0 && parsed < 65536) _settings.LhmPort = parsed;
            }, () => { _settings.Save(); checkLhm(); }));

            root.Children.Add(Divider());
            root.Children.Add(Header("Custom sensors"));
            var customListPanel = new StackPanel();
            Action refreshCustomList = null;
            refreshCustomList = () =>
            {
                customListPanel.Children.Clear();
                foreach (var custom in _settings.CustomSensors)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    var labelBox = new TextBox { Width = 130, Text = custom.Label, VerticalContentAlignment = VerticalAlignment.Center };
                    var captured = custom;
                    labelBox.LostFocus += (s, e) =>
                    {
                        captured.Label = labelBox.Text;
                        _owner.RebuildCustomRows();
                        _settings.Save();
                    };
                    var removeBtn = MakeButton("Remove");
                    removeBtn.Margin = new Thickness(6, 0, 0, 0);
                    removeBtn.Click += (s, e) =>
                    {
                        _settings.CustomSensors.Remove(captured);
                        _owner.RebuildCustomRows();
                        _settings.Save();
                        refreshCustomList();
                    };
                    row.Children.Add(labelBox);
                    row.Children.Add(removeBtn);
                    customListPanel.Children.Add(row);
                }
            };
            refreshCustomList();
            root.Children.Add(customListPanel);

            var addSensorBtn = MakeButton("Add sensor...");
            addSensorBtn.Margin = new Thickness(0, 6, 0, 0);
            addSensorBtn.Click += (s, e) =>
            {
                var picker = new SensorPickerWindow(_settings.LhmPort, (id, label) =>
                {
                    _settings.CustomSensors.Add(new CustomSensor { SensorId = id, Label = label });
                    _owner.RebuildCustomRows();
                    _settings.Save();
                    refreshCustomList();
                });
                picker.Owner = this;
                picker.ShowDialog();
            };
            root.Children.Add(addSensorBtn);

            root.Children.Add(Divider());
            root.Children.Add(Header("Hotkey (toggle overlay)"));
            var hotkeyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
            _hotkeyBox = new TextBox
            {
                Width = 150,
                IsReadOnly = true,
                Text = string.IsNullOrEmpty(_settings.HotkeyDisplay) ? "(not set)" : _settings.HotkeyDisplay,
                Padding = new Thickness(4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var setBtn = MakeButton("Set...");
            setBtn.Margin = new Thickness(8, 0, 0, 0);
            setBtn.Click += (s, e) => StartHotkeyCapture();
            var clearBtn = MakeButton("Clear");
            clearBtn.Margin = new Thickness(8, 0, 0, 0);
            clearBtn.Click += (s, e) =>
            {
                _settings.HotkeyDisplay = "";
                _settings.HotkeyModifiers = 0;
                _settings.HotkeyKey = 0;
                _hotkeyBox.Text = "(not set)";
                _owner.ApplyHotkey();
                _settings.Save();
            };
            hotkeyRow.Children.Add(_hotkeyBox);
            hotkeyRow.Children.Add(setBtn);
            hotkeyRow.Children.Add(clearBtn);
            root.Children.Add(hotkeyRow);

            var closeBtn = MakeButton("Close");
            closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
            closeBtn.Margin = new Thickness(0, 12, 0, 0);
            closeBtn.Click += (s, e) => Close();
            root.Children.Add(closeBtn);

            // Fixed footer (outside the scroll area): app version centered, developer
            // credit on the left. Plain, non-interactive TextBlocks -- not settings
            // fields, so there is no way for the user to edit either value.
            var footer = new Grid { Margin = new Thickness(16, 6, 16, 10) };
            var devLink = new TextBlock
            {
                Text = "Created by " + AppInfo.Developer,
                Foreground = new SolidColorBrush(Settings.SafeColor(_settings.ColorBorder, Colors.DodgerBlue)),
                FontSize = 10,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextDecorations = TextDecorations.Underline
            };
            devLink.MouseLeftButtonDown += (s, e) =>
            {
                try { Process.Start(AppInfo.DeveloperUrl); }
                catch { /* no default browser association; nothing more we can do */ }
            };
            footer.Children.Add(devLink);
            footer.Children.Add(new TextBlock
            {
                Text = "v" + AppInfo.Version,
                Foreground = Brushes.Gray,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var outerDock = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            outerDock.Children.Add(footer);
            outerDock.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            Content = outerDock;

            PreviewKeyDown += SettingsWindow_PreviewKeyDown;
        }

        void StartHotkeyCapture()
        {
            _capturingHotkey = true;
            _hotkeyBox.Text = "Press a key combination...";
            Focus();
        }

        void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturingHotkey) return;

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin)
            {
                return;
            }

            uint modifiers = 0;
            var parts = new List<string>();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { modifiers |= 0x2; parts.Add("Ctrl"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { modifiers |= 0x1; parts.Add("Alt"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { modifiers |= 0x4; parts.Add("Shift"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) { modifiers |= 0x8; parts.Add("Win"); }

            if (modifiers == 0)
            {
                _hotkeyBox.Text = "Need at least one modifier (Ctrl/Alt/Shift)...";
                e.Handled = true;
                return;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            parts.Add(key.ToString());
            string display = string.Join("+", parts.ToArray());

            _settings.HotkeyModifiers = modifiers;
            _settings.HotkeyKey = vk;
            _settings.HotkeyDisplay = display;
            _hotkeyBox.Text = display;
            _owner.ApplyHotkey();
            _settings.Save();

            _capturingHotkey = false;
            e.Handled = true;
        }

        void MoveRow(string key, int direction, Action refresh)
        {
            int idx = _settings.RowOrder.IndexOf(key);
            int newIdx = idx + direction;
            if (idx < 0 || newIdx < 0 || newIdx >= _settings.RowOrder.Count) return;
            string temp = _settings.RowOrder[idx];
            _settings.RowOrder[idx] = _settings.RowOrder[newIdx];
            _settings.RowOrder[newIdx] = temp;
            _owner.ApplyRowOrder();
            _settings.Save();
            refresh();
        }

        bool GetShow(string key)
        {
            if (key == "mobo") return _settings.ShowMobo;
            if (key == "cpu_temp") return _settings.ShowCpuTemp;
            if (key == "cpu_usage") return _settings.ShowCpuUsage;
            if (key == "gpu_temp") return _settings.ShowGpuTemp;
            if (key == "gpu_usage") return _settings.ShowGpuUsage;
            if (key == "ram") return _settings.ShowRam;
            if (key == "disk") return _settings.ShowDisk;
            return false;
        }

        void SetShow(string key, bool v)
        {
            if (key == "mobo") _settings.ShowMobo = v;
            else if (key == "cpu_temp") _settings.ShowCpuTemp = v;
            else if (key == "cpu_usage") _settings.ShowCpuUsage = v;
            else if (key == "gpu_temp") _settings.ShowGpuTemp = v;
            else if (key == "gpu_usage") _settings.ShowGpuUsage = v;
            else if (key == "ram") _settings.ShowRam = v;
            else if (key == "disk") _settings.ShowDisk = v;
        }

        static string RowDisplayName(string key)
        {
            if (key == "mobo") return "Motherboard temperature";
            if (key == "cpu_temp") return "CPU temperature";
            if (key == "cpu_usage") return "CPU usage %";
            if (key == "gpu_temp") return "GPU temperature";
            if (key == "gpu_usage") return "GPU usage % (NVIDIA only)";
            if (key == "ram") return "RAM usage %";
            if (key == "disk") return "Disk usage % (C:)";
            return key;
        }

        static TextBlock Header(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 4)
            };
        }

        static Border Divider()
        {
            return new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                Margin = new Thickness(0, 10, 0, 0)
            };
        }

        static CheckBox Check(string text, Func<bool> get, Action<bool> set, Action onChange = null)
        {
            var cb = new CheckBox
            {
                Content = text,
                Foreground = Brushes.White,
                IsChecked = get(),
                Margin = new Thickness(0, 2, 0, 2)
            };
            cb.Checked += (s, e) => { set(true); if (onChange != null) onChange(); };
            cb.Unchecked += (s, e) => { set(false); if (onChange != null) onChange(); };
            return cb;
        }

        // A slider for quick/rough adjustment, paired with an editable number box for
        // exact values -- typing a number outside the slider's own range still applies
        // (the slider just won't visually track it), so the slider's range is a
        // suggestion, not a hard limit.
        static StackPanel SliderRow(string label, double min, double max, Func<double> get, Action<double> onChange)
        {
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };

            var headRow = new StackPanel { Orientation = Orientation.Horizontal };
            var lbl = new TextBlock { Text = label, Foreground = Brushes.LightGray, FontSize = 12, Width = 150, VerticalAlignment = VerticalAlignment.Center };
            var numberBox = new TextBox { Width = 60, Text = FormatNumber(get()), VerticalContentAlignment = VerticalAlignment.Center };
            headRow.Children.Add(lbl);
            headRow.Children.Add(numberBox);

            var slider = new ImageSlider(min, max, get(), 280) { Margin = new Thickness(0, 4, 0, 0) };

            bool syncing = false;
            slider.ValueChanged += v =>
            {
                if (syncing) return;
                syncing = true;
                numberBox.Text = FormatNumber(v);
                onChange(v);
                syncing = false;
            };
            numberBox.LostFocus += (s, e) =>
            {
                double v;
                if (double.TryParse(numberBox.Text, out v))
                {
                    syncing = true;
                    if (v >= min && v <= max) slider.Value = v;
                    onChange(v);
                    syncing = false;
                }
                else
                {
                    numberBox.Text = FormatNumber(get());
                }
            };

            row.Children.Add(headRow);
            row.Children.Add(slider);
            return row;
        }

        static string FormatNumber(double v)
        {
            return Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
        }

        static StackPanel TextRow(string label, Func<string> get, Action<string> set, Action onChange)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var lbl = new TextBlock { Text = label, Foreground = Brushes.LightGray, Width = 140, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            var box = new TextBox { Width = 106, Text = get(), VerticalContentAlignment = VerticalAlignment.Center };
            box.LostFocus += (s, e) => { set(box.Text); onChange(); };
            row.Children.Add(lbl);
            row.Children.Add(box);
            return row;
        }

        static StackPanel ColorRow(string label, Func<string> get, Action<string> set, Action onChange)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var lbl = new TextBlock { Text = label, Foreground = Brushes.LightGray, Width = 140, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            var swatch = new Border { Width = 20, Height = 20, Margin = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(3), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            var box = new TextBox { Width = 80, Text = get(), VerticalContentAlignment = VerticalAlignment.Center };

            Action refresh = () =>
            {
                try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(get())); }
                catch { swatch.Background = Brushes.Transparent; }
            };
            refresh();

            box.LostFocus += (s, e) =>
            {
                try
                {
                    ColorConverter.ConvertFromString(box.Text);
                    set(box.Text);
                    refresh();
                    onChange();
                }
                catch
                {
                    box.Text = get();
                }
            };

            row.Children.Add(lbl);
            row.Children.Add(swatch);
            row.Children.Add(box);
            return row;
        }

        static Button MakeButton(string text)
        {
            return new Button { Content = text, Padding = new Thickness(10, 4, 10, 4) };
        }
    }

    // Browses every sensor LibreHardwareMonitor currently exposes (temperature,
    // voltage, fan, load, power, clock, data...) so the user can add any of them
    // as a custom overlay row without needing to know its SensorId.
    public class SensorPickerWindow : Window
    {
        readonly Action<string, string> _onAdd;
        List<SensorEntry> _all = new List<SensorEntry>();
        ListBox _listBox;
        TextBlock _statusText;

        public SensorPickerWindow(int port, Action<string, string> onAdd)
        {
            _onAdd = onAdd;

            Title = "Add Sensor";
            Width = 460;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

            var root = new DockPanel { Margin = new Thickness(14) };

            var filterBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            filterBox.GotFocus += (s, e) => { if (filterBox.Text == "Filter...") filterBox.Text = ""; };
            filterBox.Text = "Filter...";
            DockPanel.SetDock(filterBox, Dock.Top);

            _statusText = new TextBlock { Text = "Loading sensors...", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(_statusText, Dock.Top);

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var addBtn = new Button { Content = "Add selected", Padding = new Thickness(10, 4, 10, 4) };
            var closeBtn = new Button { Content = "Close", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
            closeBtn.Click += (s, e) => Close();
            buttonRow.Children.Add(addBtn);
            buttonRow.Children.Add(closeBtn);
            DockPanel.SetDock(buttonRow, Dock.Bottom);

            _listBox = new ListBox { Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x15)) };
            _listBox.MouseDoubleClick += (s, e) => AddSelected();
            addBtn.Click += (s, e) => AddSelected();

            filterBox.TextChanged += (s, e) => ApplyFilter(filterBox.Text);

            root.Children.Add(filterBox);
            root.Children.Add(_statusText);
            root.Children.Add(buttonRow);
            root.Children.Add(_listBox);
            Content = root;

            Loaded += async (s, e) =>
            {
                try
                {
                    _all = await Sensors.ListAllSensorsAsync(port);
                    _statusText.Text = _all.Count + " sensors found. Double-click one, or select and click Add.";
                    ApplyFilter(filterBox.Text == "Filter..." ? "" : filterBox.Text);
                }
                catch
                {
                    _statusText.Text = "Could not reach LibreHardwareMonitor on port " + port + ".";
                }
            };
        }

        void ApplyFilter(string filter)
        {
            _listBox.Items.Clear();
            foreach (var entry in _all)
            {
                if (!string.IsNullOrEmpty(filter) && entry.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _listBox.Items.Add(new ListBoxItem { Content = entry.Path + "   [" + entry.Value + "]", Tag = entry });
            }
        }

        void AddSelected()
        {
            var item = _listBox.SelectedItem as ListBoxItem;
            if (item == null) return;
            var entry = item.Tag as SensorEntry;
            if (entry == null) return;

            string defaultLabel = entry.Path;
            int lastSep = defaultLabel.LastIndexOf('>');
            if (lastSep >= 0) defaultLabel = defaultLabel.Substring(lastSep + 1).Trim();

            _onAdd(entry.SensorId, defaultLabel);
            Close();
        }
    }

    // A standard Windows system tray icon. New tray icons land in the taskbar's
    // "hidden icons" overflow area by default, which is what makes the app show up
    // there without any extra configuration. Uses WinForms' NotifyIcon (there is no
    // WPF equivalent); every type here is fully qualified rather than adding a
    // blanket "using System.Windows.Forms" so it doesn't collide with WPF types of
    // the same name (Application, Button, TextBox, ...) used everywhere else.
    public class TrayIcon
    {
        System.Windows.Forms.NotifyIcon _icon;

        public void Show(Action onOpenSettings, Action onToggleVisibility, Action onExit)
        {
            _icon = new System.Windows.Forms.NotifyIcon();
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string iconPath = System.IO.Path.Combine(dir, "icon.ico");
                _icon.Icon = File.Exists(iconPath)
                    ? new System.Drawing.Icon(iconPath)
                    : System.Drawing.SystemIcons.Application;
            }
            catch
            {
                _icon.Icon = System.Drawing.SystemIcons.Application;
            }

            _icon.Text = "Quickgauge";
            _icon.Visible = true;

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Settings", null, (s, e) => onOpenSettings());
            menu.Items.Add("Show/Hide overlay", null, (s, e) => onToggleVisibility());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => onExit());
            _icon.ContextMenuStrip = menu;

            _icon.DoubleClick += (s, e) => onOpenSettings();
        }

        public void Hide()
        {
            if (_icon == null) return;
            _icon.Visible = false;
            _icon.Dispose();
        }
    }

    public class App : Application
    {
        [STAThread]
        public static void Main()
        {
            var app = new App();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            Settings.CleanupLegacyStartupEntry();
            var settings = Settings.Load();

            var splash = new SplashWindow(Settings.SafeColor(settings.ColorBorder, Colors.DodgerBlue));
            splash.Show();

            var overlay = new OverlayWindow(settings); // construction is cheap; sensor polling starts immediately

            var tray = new TrayIcon();
            tray.Show(
                overlay.OpenSettings,
                overlay.ToggleVisibility,
                () => { tray.Hide(); app.Shutdown(); });

            var stopwatch = Stopwatch.StartNew();
            var gate = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            gate.Tick += (s, e) =>
            {
                if (stopwatch.ElapsedMilliseconds < 3000) return;
                gate.Stop();
                overlay.Show();
                splash.Close();
            };
            gate.Start();

            app.Run();
            tray.Hide();
        }
    }
}
