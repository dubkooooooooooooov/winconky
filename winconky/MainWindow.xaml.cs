using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace winconky
{
    public class ProcessInfo
    {
        public string Name { get; set; } = "";
        public string Cpu { get; set; } = "";
        public string Mem { get; set; } = "";
    }

    public partial class MainWindow : Window
    {
        private Window _hiddenOwner;

        private PerformanceCounter _netSent;
        private PerformanceCounter _netReceived;
        private Dictionary<int, TimeSpan> _prevCpuTimes = new();
        private DateTime _prevTime = DateTime.UtcNow;
        private CryptoData? _cryptoData;
        private DispatcherTimer _cryptoTimer = new();

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            InitNetworkCounters();

            _ = UpdateCryptoAsync();
            _cryptoTimer.Interval = TimeSpan.FromHours(1);
            _cryptoTimer.Tick += async (s, e) => await UpdateCryptoAsync();
            _cryptoTimer.Start();

            this.Loaded += (s, e) => PositionOnRight();
            StartTimer();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Create an invisible, non-taskbar window and set it as owner
            _hiddenOwner = new Window()
            {
                Width = 0,
                Height = 0,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                Visibility = Visibility.Hidden
            };
            _hiddenOwner.Show();
            this.Owner = _hiddenOwner;

            // Also apply the TOOLWINDOW style via WinAPI for extra safety
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        private void InitNetworkCounters()
        {
            try
            {
                var category = new PerformanceCounterCategory("Network Interface");
                string[] adapterNames = category.GetInstanceNames();

                string? adapter = adapterNames.FirstOrDefault(i =>
                    !i.Contains("Loopback") && !i.Contains("Virtual"));

                adapter ??= adapterNames.FirstOrDefault();

                if (adapter != null)
                {
                    _netSent = new PerformanceCounter("Network Interface", "Bytes Sent/sec", adapter);
                    _netReceived = new PerformanceCounter("Network Interface", "Bytes Received/sec", adapter);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Network init error: {ex.Message}");
            }
        }

        private string FormatSpeed(float bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024)
                return $"{bytesPerSec / 1024 / 1024:0.0} MB/s";
            else if (bytesPerSec >= 1024)
                return $"{bytesPerSec / 1024:0.0} KB/s";
            else
                return $"{bytesPerSec:0} B/s";
        }

        private async Task UpdateCryptoAsync()
        {
            _cryptoData = await CryptoService.FetchAsync();

            CryptoText.Text = $"{_cryptoData.Btc}\n{_cryptoData.Eth}\n" +
                              $"{_cryptoData.Sol}";
            CryptoUpdatedText.Text = $"Updated: {DateTime.Now:g}";
        }

        private void PositionOnRight()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;

            this.Left = screenWidth - this.Width - 40;
            this.Top = 50;
        }

        private void StartTimer()
        {
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(5);
            timer.Tick += UpdateStats;
            timer.Start();
        }

        private async void UpdateStats(object sender, EventArgs e)
        {
            try
            {
                // System name
                SystemNameText.Text = Environment.MachineName;

                // Uptime
                TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                UptimeText.Text = "Uptime: " + uptime.ToString(@"dd\.hh\:mm\:ss");

                // RAM
                var ram = await Task.Run(() =>
                {
                    ulong total = 0;
                    ulong free = 0;
                    ulong commitLimit = 0;
                    ulong committedBytes = 0;

                    var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                    foreach (ManagementObject obj in searcher.Get())
                        total = (ulong)obj["TotalPhysicalMemory"];

                    var searcher2 = new ManagementObjectSearcher("SELECT FreePhysicalMemory, TotalVirtualMemorySize, FreeVirtualMemory FROM Win32_OperatingSystem");
                    foreach (ManagementObject obj2 in searcher2.Get())
                    {
                        free = (ulong)obj2["FreePhysicalMemory"] * 1024;
                        commitLimit = (ulong)obj2["TotalVirtualMemorySize"] * 1024;
                        committedBytes = commitLimit - (ulong)obj2["FreeVirtualMemory"] * 1024;
                    }

                    ulong used = total - free;
                    int percentRam = (int)(used * 100 / total);
                    int percentCommit = (int)(committedBytes * 100 / commitLimit);

                    return (used, total, percentRam, committedBytes, commitLimit, percentCommit);
                });

                RamText.Text = $"RAM Physical:\t {ram.used / 1024 / 1024}MB / {ram.total / 1024 / 1024}MB\n" +
                    $"RAM Commited:\t {ram.committedBytes / 1024 / 1024}MB / {ram.commitLimit / 1024 / 1024}MB";
                RamBar.Value = ram.percentCommit;

                // CPU
                var cpu = await Task.Run(() =>
                {
                    var searcher = new ManagementObjectSearcher(
                        "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");

                    foreach (ManagementObject obj in searcher.Get())
                        return Convert.ToSingle(obj["PercentProcessorTime"]);

                    return 0f;
                });

                CpuText.Text = $"CPU: {cpu:0.0}%";
                CpuBar.Value = cpu;

                // Net
                float netUp = _netSent.NextValue();
                float netDown = _netReceived.NextValue();
                NetText.Text = $"Net: ↑ {FormatSpeed(netUp)}  ↓ {FormatSpeed(netDown)}";

                // Processes
                var processes = await Task.Run(() =>
                {
                    var now = DateTime.UtcNow;
                    double elapsed = (now - _prevTime).TotalSeconds;
                    int cpuCount = Environment.ProcessorCount;

                    ulong totalRam = 0;
                    var s1 = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                    foreach (ManagementObject obj in s1.Get())
                        totalRam = (ulong)obj["TotalPhysicalMemory"];

                    var result = new List<(string Name, double Cpu, double Mem)>();

                    foreach (var p in Process.GetProcesses())
                    {
                        try
                        {
                            var cpu = p.TotalProcessorTime;
                            double cpuPercent = 0;

                            if (_prevCpuTimes.TryGetValue(p.Id, out var prev))
                                cpuPercent = (cpu - prev).TotalSeconds / (elapsed * cpuCount) * 100.0;

                            _prevCpuTimes[p.Id] = cpu;

                            double memPercent = totalRam > 0
                                ? (double)p.WorkingSet64 / totalRam * 100.0
                                : 0;

                            result.Add((p.ProcessName, cpuPercent, memPercent));
                        }
                        catch { }
                    }

                    _prevTime = now;

                    var alive = Process.GetProcesses().Select(p => p.Id).ToHashSet();
                    foreach (var key in _prevCpuTimes.Keys.Where(k => !alive.Contains(k)).ToList())
                        _prevCpuTimes.Remove(key);

                    return result
                        .OrderByDescending(p => p.Cpu)
                        .Take(10)
                        .ToList();
                });

                ProcessList.ItemsSource = processes.Select(p => new ProcessInfo
                {
                    Name = p.Name.Length > 18 ? p.Name[..18] : p.Name.PadRight(18),
                    Cpu = $"{p.Cpu,5:0.0}%",
                    Mem = $"{p.Mem,5:0.1}%"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }
    }
}