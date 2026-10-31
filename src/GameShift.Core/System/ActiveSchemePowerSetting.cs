namespace GameShift.Core.System;

/// <summary>
/// Reads a power setting's index on the active power scheme through powrprof. Unlike the text
/// powercfg prints, these reads do not depend on the Windows display language.
/// </summary>
internal static class ActiveSchemePowerSetting
{
    private static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static readonly Guid SchemePersonality = new("245d8541-3943-4422-b025-13a784f679b7");
    private const uint RegBinary = 3;

    /// <summary>The active scheme's current AC or DC index, or null when it cannot be read.</summary>
    public static uint? ReadIndex(Guid subgroup, Guid setting, bool ac)
    {
        try
        {
            var scheme = SystemStateSnapshot.Capture().OriginalPowerPlan;
            if (scheme == Guid.Empty) return null;

            uint index;
            uint error = ac
                ? NativeInterop.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out index)
                : NativeInterop.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out index);
            return error == 0 ? index : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Windows' default AC or DC index for the active scheme, or null when it cannot be read.</summary>
    public static uint? ReadDefaultIndex(Guid subgroup, Guid setting, bool ac)
    {
        try
        {
            // Defaults are kept per scheme personality (Balanced, High performance, Power saver),
            // not per scheme, and a custom or OEM scheme has its own GUID.
            var scheme = SystemStateSnapshot.Capture().OriginalPowerPlan;
            if (scheme == Guid.Empty) return null;

            var noSubgroup = NoSubgroup;
            var personalitySetting = SchemePersonality;
            var buffer = new byte[16];
            uint size = (uint)buffer.Length;
            if (NativeInterop.PowerReadACValue(IntPtr.Zero, ref scheme, ref noSubgroup, ref personalitySetting,
                    out uint type, buffer, ref size) != 0
                || type != RegBinary || size != buffer.Length)
                return null;
            var personality = new Guid(buffer);

            uint index;
            uint error = ac
                ? NativeInterop.PowerReadACDefaultIndex(IntPtr.Zero, ref personality, ref subgroup, ref setting, out index)
                : NativeInterop.PowerReadDCDefaultIndex(IntPtr.Zero, ref personality, ref subgroup, ref setting, out index);
            return error == 0 ? index : null;
        }
        catch
        {
            return null;
        }
    }
}
