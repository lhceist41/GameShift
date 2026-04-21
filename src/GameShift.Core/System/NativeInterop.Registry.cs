using System.Runtime.InteropServices;

namespace GameShift.Core.System;

public static partial class NativeInterop
{
    // ── RegNotifyChangeKeyValue ──────────────────────────────────────────────
    // https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regnotifychangekeyvalue

    /// <summary>
    /// Notifies the caller about changes to the attributes or contents of a registry key.
    /// When <paramref name="fAsynchronous"/> is true the call returns immediately and the
    /// provided event handle is signalled when a change occurs (one-shot, must be re-armed).
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern int RegNotifyChangeKeyValue(
        IntPtr   hKey,
        bool     bWatchSubtree,
        RegNotifyFilter dwNotifyFilter,
        IntPtr   hEvent,
        bool     fAsynchronous);

    /// <summary>
    /// Filter flags for <see cref="RegNotifyChangeKeyValue"/>.
    /// </summary>
    [Flags]
    internal enum RegNotifyFilter : uint
    {
        /// <summary>Notify the caller if a subkey is added or deleted.</summary>
        Name        = 0x00000001,

        /// <summary>Notify the caller of changes to the attributes of the key.</summary>
        Attributes  = 0x00000002,

        /// <summary>Notify the caller of changes to a value of the key (add, delete, modify).</summary>
        LastSet     = 0x00000004,

        /// <summary>Notify the caller of changes to the security descriptor of the key.</summary>
        Security    = 0x00000008,

        /// <summary>
        /// Indicates that the thread should not be unloaded until the notification fires.
        /// Combined with the other flags when the thread owns the registration.
        /// </summary>
        ThreadAgnostic = 0x10000000,
    }
}
