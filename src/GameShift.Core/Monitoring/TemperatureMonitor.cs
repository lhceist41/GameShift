using Serilog;
using Timer = global::System.Timers.Timer;

namespace GameShift.Core.Monitoring;

/// <summary>
/// Event data for a temperature sample.
/// </summary>
public class TemperatureSample
{
    public float CpuTempCelsius { get; init; }
    public float GpuTempCelsius { get; init; }
}

/// <summary>
/// Monitors CPU and GPU temperatures using LibreHardwareMonitor.
/// 2-second poll interval. Graceful fallback: IsAvailable = false if sensors unavailable.
/// Requires admin privileges for hardware access.
/// </summary>
public class TemperatureMonitor : IDisposable
{
    private LibreHardwareMonitor.Hardware.Computer? _computer;
    private Timer _timer;
    private bool _isMonitoring;
    private bool _disposed;
    private readonly ILogger _logger;

    public float CurrentCpuTemp { get; private set; }
    public float CurrentGpuTemp { get; private set; }
    public float MinCpuTemp { get; private set; }
    public float MaxCpuTemp { get; private set; }
    public float MinGpuTemp { get; private set; }
    public float MaxGpuTemp { get; private set; }
    private bool _hasFirstReading;
    public bool IsAvailable { get; private set; }
    public bool IsMonitoring => _isMonitoring;

    public event EventHandler<TemperatureSample>? TemperatureUpdated;

    public TemperatureMonitor()
    {
        _logger = Config.SettingsManager.Logger;

        try
        {
            _computer = new LibreHardwareMonitor.Hardware.Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true
            };
            _computer.Open();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "LibreHardwareMonitor failed to initialize. Temperature monitoring unavailable.");
            IsAvailable = false;
        }

        _timer = new Timer(2000);
        _timer.Elapsed += OnTimerElapsed;
        _timer.AutoReset = true;
    }

    public void Start()
    {
        if (!IsAvailable) return;
        _isMonitoring = true;
        _timer.Enabled = true;
        _logger.Information("Temperature monitor started");
    }

    public void Stop()
    {
        _timer.Enabled = false;
        _isMonitoring = false;
        _logger.Information("Temperature monitor stopped");
    }

    /// <summary>Pause monitoring (stop timer but keep state).</summary>
    public void Pause()
    {
        if (!_isMonitoring) return;
        _timer.Enabled = false;
        _logger.Debug("Temperature monitor paused");
    }

    /// <summary>Resume monitoring after a pause.</summary>
    public void Resume()
    {
        if (!_isMonitoring) return;
        _timer.Enabled = true;
        _logger.Debug("Temperature monitor resumed");
    }

    private void OnTimerElapsed(object? sender, global::System.Timers.ElapsedEventArgs e)
    {
        if (_computer == null || !IsAvailable) return;

        try
        {
            float cpuTemp = 0;
            float gpuTemp = 0;
            bool foundCpu = false;
            bool foundGpu = false;

            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();

                if (hardware.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu && !foundCpu)
                {
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature &&
                            sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) &&
                            sensor.Value.HasValue)
                        {
                            cpuTemp = sensor.Value.Value;
                            foundCpu = true;
                            break;
                        }
                    }
                    // Fallback: first temp sensor
                    if (!foundCpu)
                    {
                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature &&
                                sensor.Value.HasValue)
                            {
                                cpuTemp = sensor.Value.Value;
                                foundCpu = true;
                                break;
                            }
                        }
                    }
                }

                if ((hardware.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia ||
                     hardware.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.GpuAmd ||
                     hardware.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.GpuIntel) && !foundGpu)
                {
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature &&
                            sensor.Value.HasValue)
                        {
                            gpuTemp = sensor.Value.Value;
                            foundGpu = true;
                            break;
                        }
                    }
                }
            }

            CurrentCpuTemp = cpuTemp;
            CurrentGpuTemp = gpuTemp;

            if (cpuTemp > 0 || gpuTemp > 0)
            {
                if (!_hasFirstReading)
                {
                    if (cpuTemp > 0) { MinCpuTemp = cpuTemp; MaxCpuTemp = cpuTemp; }
                    if (gpuTemp > 0) { MinGpuTemp = gpuTemp; MaxGpuTemp = gpuTemp; }
                    _hasFirstReading = true;
                }
                else
                {
                    if (cpuTemp > 0) { MinCpuTemp = Math.Min(MinCpuTemp, cpuTemp); MaxCpuTemp = Math.Max(MaxCpuTemp, cpuTemp); }
                    if (gpuTemp > 0) { MinGpuTemp = Math.Min(MinGpuTemp, gpuTemp); MaxGpuTemp = Math.Max(MaxGpuTemp, gpuTemp); }
                }
            }

            TemperatureUpdated?.Invoke(this, new TemperatureSample
            {
                CpuTempCelsius = cpuTemp,
                GpuTempCelsius = gpuTemp
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error reading temperature sensors");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Enabled = false;
        _timer.Dispose();
        try { _computer?.Close(); } catch { }
        GC.SuppressFinalize(this);
    }
}
