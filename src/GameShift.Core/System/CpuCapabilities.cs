using System.Runtime.Intrinsics.X86;

namespace GameShift.Core.System;

/// <summary>
/// Lightweight CPU capability probe via CPUID. Used to decide whether BCD timer tweaks
/// (forcing the platform timer / disabling the dynamic tick) are appropriate on this processor.
/// All values are read once and cached.
/// </summary>
public static class CpuCapabilities
{
    private static readonly Lazy<string> LazyVendor = new(ReadVendor);

    /// <summary>CPUID vendor string (e.g. "AuthenticAMD", "GenuineIntel"). Empty when CPUID is unavailable.</summary>
    public static string Vendor => LazyVendor.Value;

    /// <summary>True when the processor reports the AMD vendor id.</summary>
    public static bool IsAmd => string.Equals(Vendor, "AuthenticAMD", StringComparison.Ordinal);

    /// <summary>
    /// True when forcing the platform timer / periodic tick degrades performance on this CPU.
    /// On AMD Ryzen the per-core timer (invariant TSC) is efficient, so replacing it with the
    /// platform timer makes Windows log HAL event 17 ("Performance may be degraded") and
    /// Kernel-Power event 508 ("constrained to a periodic tick") with no upside. Gated on AMD,
    /// the case where the harm is documented and reproduced; Intel systems keep current behavior.
    /// </summary>
    public static bool PlatformTimerTweaksHarmful => IsAmd;

    private static string ReadVendor()
    {
        try
        {
            if (!X86Base.IsSupported) return string.Empty;

            // CPUID leaf 0: vendor id is EBX, EDX, ECX (in that order).
            var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
            Span<byte> buffer = stackalloc byte[12];
            BitConverter.TryWriteBytes(buffer[..4], ebx);
            BitConverter.TryWriteBytes(buffer[4..8], edx);
            BitConverter.TryWriteBytes(buffer[8..12], ecx);
            return global::System.Text.Encoding.ASCII.GetString(buffer);
        }
        catch
        {
            // CPUID unavailable (non-x86 host or hypervisor quirk): treat vendor as unknown,
            // which leaves the timer tweaks enabled (the pre-existing behavior).
            return string.Empty;
        }
    }
}
