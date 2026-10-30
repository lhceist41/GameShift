using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using GameShift.Core.System;

namespace GameShift.Core.BackgroundMode;

/// <summary>Power plans through powrprof, so no localized powercfg output is parsed.</summary>
public sealed class WindowsPowerPlanStore : IPowerPlanStore
{
    private const uint ErrorNoMoreItems = 259;

    public IReadOnlyList<PowerSchemeInfo> List()
    {
        var plans = new List<PowerSchemeInfo>();
        for (uint index = 0; ; index++)
        {
            var id = Guid.Empty;
            uint size = (uint)Marshal.SizeOf<Guid>();
            uint result = NativeInterop.PowerEnumerate(
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeInterop.ACCESS_SCHEME, index, ref id, ref size);
            if (result == ErrorNoMoreItems)
                return plans;
            if (result != 0)
                throw new Win32Exception((int)result);

            plans.Add(new PowerSchemeInfo(id, ReadName(id)));
        }
    }

    public Guid GetActive() => SystemStateSnapshot.Capture().OriginalPowerPlan;

    public bool Activate(Guid id) => NativeInterop.PowerSetActiveScheme(IntPtr.Zero, ref id) == 0;

    public bool Delete(Guid id) => NativeInterop.PowerDeleteScheme(IntPtr.Zero, ref id) == 0;

    private static string? ReadName(Guid id)
    {
        uint size = 0;
        if (NativeInterop.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0)
            return null;

        var buffer = new byte[size];
        if (NativeInterop.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
            return null;

        return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
    }
}

/// <summary>Scheduled tasks through the Task Scheduler COM API, so no localized schtasks output is parsed.</summary>
public sealed class WindowsScheduledTaskStore : IScheduledTaskStore
{
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);

    public ScheduledTaskState GetState(string path)
    {
        try
        {
            var task = OpenTask(path);
            if (task == null)
                return ScheduledTaskState.NotFound;
            return (bool)task.Enabled ? ScheduledTaskState.Enabled : ScheduledTaskState.Disabled;
        }
        catch
        {
            return ScheduledTaskState.Unreadable;
        }
    }

    public bool Enable(string path)
    {
        try
        {
            var task = OpenTask(path);
            if (task == null)
                return false;
            task.Enabled = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static dynamic? OpenTask(string path)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();

        int split = path.LastIndexOf('\\');
        var folderPath = split <= 0 ? @"\" : path[..split];
        var name = path[(split + 1)..];
        try
        {
            return service.GetFolder(folderPath).GetTask(name);
        }
        // The runtime maps these HRESULTs to FileNotFoundException and DirectoryNotFoundException.
        catch (Exception ex) when (ex.HResult is HResultFileNotFound or HResultPathNotFound)
        {
            return null;
        }
    }
}
