using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lux.Engine.Mtp.Wpd;

// Reads part of a file with the MTP operation GetPartialObject, sent through the MTP command interface of Windows Portable Devices.
// The file stream of Windows reads all the bytes before an offset, which takes seconds in a large file. GetPartialObject reads only
// the bytes that are asked for. It is a read operation: it changes nothing on the camera.

/// <summary>IPortableDeviceValues with the methods that the command interface needs. A placeholder keeps the position of each method
/// that is not called.</summary>
[ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceCommandValues
{
    void _0(); void _1(); void _2(); void _3();
    [PreserveSig] int SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int GetStringValue(ref PropertyKey key, out IntPtr value);
    [PreserveSig] int SetUnsignedIntegerValue(ref PropertyKey key, uint value);
    [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
    void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16();
    [PreserveSig] int GetErrorValue(ref PropertyKey key, out int value);
    void _18(); void _19(); void _20(); void _21(); void _22(); void _23();
    [PreserveSig] int SetGuidValue(ref PropertyKey key, ref Guid value);
    void _25();
    [PreserveSig] int SetBufferValue(ref PropertyKey key, [In, MarshalAs(UnmanagedType.LPArray)] byte[] value, uint size);
    [PreserveSig] int GetBufferValue(ref PropertyKey key, out IntPtr value, out uint size);
    void _28(); void _29();
    [PreserveSig] int SetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, IPortableDevicePropVariantCollection value);
}

/// <summary>IPortableDevice with the command method.</summary>
[ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceCommands
{
    void _0();
    [PreserveSig] int SendCommand(uint flags, IPortableDeviceCommandValues parameters, out IPortableDeviceCommandValues results);
}

[ComImport, Guid("89B2E422-4F1B-4316-BCEF-A44AFEA83EB3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
    void _0(); void _1();
    [PreserveSig] int Add(ref UnsignedVariant value);
}

[ComImport, Guid("08A99E2F-6D6D-4B80-AF5A-BAF2BCBE4CB9")] internal class PortableDevicePropVariantCollectionClass { }

/// <summary>A PROPVARIANT that holds a 32-bit unsigned integer.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct UnsignedVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public uint Value;
    public UnsignedVariant(uint value) { Type = 19; Value = value; }
}

[SupportedOSPlatform("windows")]
internal static class WpdPartial
{
    const string MtpExtension = "4D545058-1A2E-4106-A357-771E0819FC56";
    static Guid s_category = new(MtpExtension);
    const uint CommandReadStart = 13, CommandReadData = 15, CommandEnd = 17;
    static PropertyKey s_operationCode = new(MtpExtension, 1001), s_operationParams = new(MtpExtension, 1002), s_responseCode = new(MtpExtension, 1003),
                       s_context = new(MtpExtension, 1006), s_bytesToRead = new(MtpExtension, 1008), s_data = new(MtpExtension, 1012);
    const string Common = "F0422A9C-5DC8-4440-B5BD-5DF28835658A";
    static PropertyKey s_commandCategory = new(Common, 1001), s_commandId = new(Common, 1002), s_commandResult = new(Common, 1003);

    const uint GetPartialObject = 0x101B, ResponseOk = 0x2001;

    /// <summary>The MTP object handle in an object identifier of the Windows MTP driver ("o" and the handle in hexadecimal).</summary>
    static bool Handle(string objectId, out uint handle)
    {
        handle = 0;
        return objectId.Length > 1 && objectId[0] == 'o'
               && uint.TryParse(objectId.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out handle);
    }

    static IPortableDeviceCommandValues Command(uint id)
    {
        var v = (IPortableDeviceCommandValues)new PortableDeviceValuesClass();
        v.SetGuidValue(ref s_commandCategory, ref s_category);
        v.SetUnsignedIntegerValue(ref s_commandId, id);
        return v;
    }

    /// <summary>Sends a command. Returns its results, or null when the driver or the camera refuses the command.</summary>
    static IPortableDeviceCommandValues? Send(IPortableDeviceCommands device, IPortableDeviceCommandValues command)
    {
        try
        {
            if (device.SendCommand(0, command, out var results) < 0 || results is null) return null;
            if (results.GetErrorValue(ref s_commandResult, out int hr) >= 0 && hr < 0) { Marshal.ReleaseComObject(results); return null; }
            return results;
        }
        finally { Marshal.ReleaseComObject(command); }
    }

    /// <summary>Reads <paramref name="count"/> bytes of an object from <paramref name="offset"/>; fewer at the end of the object.
    /// Returns null when this way to read is not available: the caller then reads through the file stream.</summary>
    public static byte[]? Read(IPortableDevice device, string objectId, long offset, int count)
    {
        if (!Handle(objectId, out uint handle) || offset < 0 || offset + count > uint.MaxValue) return null;
        if (count == 0) return Array.Empty<byte>();
        if (device is not IPortableDeviceCommands commands) return null;

        var start = Command(CommandReadStart);
        var parameters = (IPortableDevicePropVariantCollection)new PortableDevicePropVariantCollectionClass();
        string context;
        try
        {
            foreach (uint p in new[] { handle, (uint)offset, (uint)count })
            {
                var pv = new UnsignedVariant(p);
                if (parameters.Add(ref pv) < 0) { Marshal.ReleaseComObject(start); return null; }
            }
            start.SetUnsignedIntegerValue(ref s_operationCode, GetPartialObject);
            start.SetIPortableDevicePropVariantCollectionValue(ref s_operationParams, parameters);
        }
        finally { Marshal.ReleaseComObject(parameters); }
        var started = Send(commands, start);
        if (started is null) return null;
        try
        {
            if (started.GetStringValue(ref s_context, out var c) < 0) return null;
            context = Wpd.TakeString(c);
        }
        finally { Marshal.ReleaseComObject(started); }

        var data = new byte[count];
        int have = 0;
        bool failed = false;
        while (have < count && !failed)
        {
            int want = count - have;
            var read = Command(CommandReadData);
            read.SetStringValue(ref s_context, context);
            read.SetUnsignedIntegerValue(ref s_bytesToRead, (uint)want);
            read.SetBufferValue(ref s_data, new byte[want], (uint)want);
            var got = Send(commands, read);
            if (got is null) { failed = true; break; }
            try
            {
                if (got.GetBufferValue(ref s_data, out var buffer, out uint size) < 0) { failed = true; break; }
                try
                {
                    if (size == 0) break;   // the end of the object
                    int n = (int)Math.Min(size, (uint)want);
                    Marshal.Copy(buffer, data, have, n);
                    have += n;
                }
                finally { Marshal.FreeCoTaskMem(buffer); }
            }
            finally { Marshal.ReleaseComObject(got); }
        }

        // The transfer must always be ended, or the driver keeps the camera busy.
        var end = Command(CommandEnd);
        end.SetStringValue(ref s_context, context);
        var ended = Send(commands, end);
        if (ended is null) return null;
        try
        {
            if (ended.GetUnsignedIntegerValue(ref s_responseCode, out uint response) < 0 || response != ResponseOk) return null;
        }
        finally { Marshal.ReleaseComObject(ended); }
        if (failed) return null;
        return have == count ? data : data[..have];
    }
}
