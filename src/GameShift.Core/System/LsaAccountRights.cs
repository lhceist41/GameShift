using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GameShift.Core.System;

/// <summary>Rights assigned to accounts in the local security policy. Every call returns an NTSTATUS.</summary>
internal interface IAccountRights
{
    int Enumerate(SecurityIdentifier account, out IReadOnlyList<string> rights);

    int Add(SecurityIdentifier account, string right);

    int Remove(SecurityIdentifier account, bool allRights, string right);
}

/// <summary>
/// Account rights through the LSA policy API, so no policy file is exported to or imported from a
/// user-writable folder.
/// </summary>
internal sealed class LsaAccountRights : IAccountRights
{
    public int Enumerate(SecurityIdentifier account, out IReadOnlyList<string> rights)
    {
        rights = Array.Empty<string>();
        int status = OpenPolicy(NativeInterop.POLICY_LOOKUP_NAMES, out var policy);
        if (status != NativeInterop.STATUS_SUCCESS)
            return status;

        try
        {
            status = NativeInterop.LsaEnumerateAccountRights(policy, ToBinary(account), out var buffer, out uint count);
            if (status != NativeInterop.STATUS_SUCCESS)
                return status;

            try
            {
                var names = new List<string>((int)count);
                int size = Marshal.SizeOf<NativeInterop.LSA_UNICODE_STRING>();
                for (int i = 0; i < count; i++)
                {
                    var name = Marshal.PtrToStructure<NativeInterop.LSA_UNICODE_STRING>(buffer + i * size);
                    names.Add(Marshal.PtrToStringUni(name.Buffer, name.Length / 2));
                }
                rights = names;
                return status;
            }
            finally
            {
                NativeInterop.LsaFreeMemory(buffer);
            }
        }
        finally
        {
            NativeInterop.LsaClose(policy);
        }
    }

    public int Add(SecurityIdentifier account, string right) =>
        Change(NativeInterop.POLICY_LOOKUP_NAMES | NativeInterop.POLICY_CREATE_ACCOUNT, account, right,
            (policy, sid, rights) => NativeInterop.LsaAddAccountRights(policy, sid, rights, 1));

    public int Remove(SecurityIdentifier account, bool allRights, string right) =>
        Change(NativeInterop.POLICY_LOOKUP_NAMES, account, right,
            (policy, sid, rights) => NativeInterop.LsaRemoveAccountRights(policy, sid, allRights, rights, 1));

    private static int Change(
        uint access,
        SecurityIdentifier account,
        string right,
        Func<IntPtr, byte[], NativeInterop.LSA_UNICODE_STRING[], int> call)
    {
        int status = OpenPolicy(access, out var policy);
        if (status != NativeInterop.STATUS_SUCCESS)
            return status;

        IntPtr text = Marshal.StringToHGlobalUni(right);
        try
        {
            var rights = new[]
            {
                new NativeInterop.LSA_UNICODE_STRING
                {
                    Length = (ushort)(right.Length * 2),
                    MaximumLength = (ushort)((right.Length + 1) * 2),
                    Buffer = text,
                },
            };
            return call(policy, ToBinary(account), rights);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
            NativeInterop.LsaClose(policy);
        }
    }

    private static int OpenPolicy(uint access, out IntPtr policy)
    {
        var attributes = new NativeInterop.LSA_OBJECT_ATTRIBUTES
        {
            Length = (uint)Marshal.SizeOf<NativeInterop.LSA_OBJECT_ATTRIBUTES>(),
        };
        return NativeInterop.LsaOpenPolicy(IntPtr.Zero, ref attributes, access, out policy);
    }

    private static byte[] ToBinary(SecurityIdentifier account)
    {
        var binary = new byte[account.BinaryLength];
        account.GetBinaryForm(binary, 0);
        return binary;
    }
}
