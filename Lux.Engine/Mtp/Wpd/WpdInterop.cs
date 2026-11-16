using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Lux.Engine.Mtp.Wpd;

// The COM interfaces of Windows Portable Devices (PortableDeviceApi.dll), reduced to the methods that read.
// The methods of each interface are in the order of the Windows interface, because COM calls a method by its position.
// A method that this backend does not call has a placeholder with the name "_" and a number. Do not remove or move a placeholder.

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
    public PropertyKey(string formatId, uint propertyId) { FormatId = new Guid(formatId); PropertyId = propertyId; }
}

/// <summary>A PROPVARIANT, read-only use: the type tag and the first eight bytes of the value.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public double Date;
    [FieldOffset(8)] public IntPtr Pointer;
    public const ushort VtDate = 7, VtLpwstr = 31;
}

internal static class Wpd
{
    public const string Object = "EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C";
    public static PropertyKey ObjectName = new(Object, 4), ObjectContentType = new(Object, 7), ObjectSize = new(Object, 11),
                              ObjectOriginalFileName = new(Object, 12), ObjectDateModified = new(Object, 19);

    public const string Device = "26D4979A-E643-4626-9E2B-736DC0C92FDC";
    public static PropertyKey DeviceFirmwareVersion = new(Device, 3), DevicePowerLevel = new(Device, 4), DeviceManufacturer = new(Device, 7),
                              DeviceModel = new(Device, 8), DeviceSerialNumber = new(Device, 9);

    public const string ClientInfo = "204D9F0C-2292-4080-9F42-40664E70F859";
    public static PropertyKey ClientName = new(ClientInfo, 2), ClientMajorVersion = new(ClientInfo, 3), ClientMinorVersion = new(ClientInfo, 4),
                              ClientRevision = new(ClientInfo, 5), ClientDesiredAccess = new(ClientInfo, 9);

    public static PropertyKey ResourceDefault = new("E81E79BE-34F0-41BF-B53F-F1A06AE87842", 0);

    public static readonly Guid ContentTypeFolder = new("27E2E392-A111-48E0-AB0C-E17705A05F85");
    public static readonly Guid ContentTypeFunctionalObject = new("99ED0160-17FF-4C44-9D98-1D7A6F941921");

    /// <summary>The object identifier of the device itself. Its children are the storages.</summary>
    public const string DeviceObjectId = "DEVICE";
    public const uint GenericReadWrite = 0xC0000000;

    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant value);

    /// <summary>Reads a string that COM allocated, and frees it.</summary>
    public static string TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(p) ?? ""; } finally { Marshal.FreeCoTaskMem(p); }
    }
}

[ComImport, Guid("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3")] internal class PortableDeviceManagerClass { }
[ComImport, Guid("F7C0039A-4762-488A-B4B3-760EF9A1BA9B")] internal class PortableDeviceFtmClass { }
[ComImport, Guid("0C15D503-D017-47CE-9016-7B3F978721CC")] internal class PortableDeviceValuesClass { }
[ComImport, Guid("DE2D022D-2480-43BE-97F0-D1FA2CF98F4F")] internal class PortableDeviceKeyCollectionClass { }

[ComImport, Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceKeyCollection
{
    void _0(); void _1();
    [PreserveSig] int Add(ref PropertyKey key);
}

[ComImport, Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    [PreserveSig] int GetDevices([In, Out, MarshalAs(UnmanagedType.LPArray)] IntPtr[]? ids, ref uint count);
    [PreserveSig] int RefreshDeviceList();
    [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string id, [Out, MarshalAs(UnmanagedType.LPArray)] char[]? name, ref uint length);
    [PreserveSig] int GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string id, [Out, MarshalAs(UnmanagedType.LPArray)] char[]? name, ref uint length);
    [PreserveSig] int GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string id, [Out, MarshalAs(UnmanagedType.LPArray)] char[]? name, ref uint length);
}

[ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    [PreserveSig] int Open([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceValues clientInfo);
    [PreserveSig] int _1(uint flags, IntPtr parameters, out IntPtr results);            // SendCommand
    [PreserveSig] int Content(out IPortableDeviceContent content);
    [PreserveSig] int _2(out IntPtr capabilities);                                      // Capabilities
    [PreserveSig] int Cancel();
    [PreserveSig] int Close();
}

[ComImport, Guid("6A96ED84-7C73-4480-9938-BF5AF477D426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceContent
{
    [PreserveSig] int EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentId, IntPtr filter, out IEnumPortableDeviceObjectIDs objects);
    [PreserveSig] int Properties(out IPortableDeviceProperties properties);
    [PreserveSig] int Transfer(out IPortableDeviceResources resources);
}

[ComImport, Guid("10ECE955-CF41-4728-BFA0-41EEDF1BBF19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumPortableDeviceObjectIDs
{
    [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] ids, out uint fetched);
}

[ComImport, Guid("7F6D695C-03DF-4439-A809-59266BEEE3A6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceProperties
{
    [PreserveSig] int _1([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IntPtr keys);                      // GetSupportedProperties
    [PreserveSig] int _2([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IntPtr attributes);   // GetPropertyAttributes
    /// <summary>With no key list, the device gives all the properties of the object. That is slower.</summary>
    [PreserveSig] int GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection? keys, out IPortableDeviceValues values);
}

[ComImport, Guid("FD8878AC-D841-4D17-891C-E6829CDB6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceResources
{
    [PreserveSig] int _1([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IntPtr keys);                       // GetSupportedResources
    [PreserveSig] int _2([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IntPtr attributes);    // GetResourceAttributes
    [PreserveSig] int GetStream([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, uint mode, ref uint optimalBufferSize, out IStream stream);
}

[ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    [PreserveSig] int _1(out uint count);                                                                           // GetCount
    [PreserveSig] int _2(uint index, ref PropertyKey key, ref PropVariant value);                                   // GetAt
    [PreserveSig] int _3(ref PropertyKey key, ref PropVariant value);                                               // SetValue
    [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int GetStringValue(ref PropertyKey key, out IntPtr value);
    [PreserveSig] int SetUnsignedIntegerValue(ref PropertyKey key, uint value);
    [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
    [PreserveSig] int _4(ref PropertyKey key, int value);                                                           // SetSignedIntegerValue
    [PreserveSig] int _5(ref PropertyKey key, out int value);                                                       // GetSignedIntegerValue
    [PreserveSig] int _6(ref PropertyKey key, ulong value);                                                         // SetUnsignedLargeIntegerValue
    [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
    [PreserveSig] int _7(ref PropertyKey key, long value);                                                          // SetSignedLargeIntegerValue
    [PreserveSig] int _8(ref PropertyKey key, out long value);                                                      // GetSignedLargeIntegerValue
    [PreserveSig] int _9(ref PropertyKey key, float value);                                                         // SetFloatValue
    [PreserveSig] int _10(ref PropertyKey key, out float value);                                                    // GetFloatValue
    [PreserveSig] int _11(ref PropertyKey key, int value);                                                          // SetErrorValue
    [PreserveSig] int _12(ref PropertyKey key, out int value);                                                      // GetErrorValue
    [PreserveSig] int _13(ref PropertyKey key, ref PropertyKey value);                                              // SetKeyValue
    [PreserveSig] int _14(ref PropertyKey key, out PropertyKey value);                                              // GetKeyValue
    [PreserveSig] int _15(ref PropertyKey key, int value);                                                          // SetBoolValue
    [PreserveSig] int _16(ref PropertyKey key, out int value);                                                      // GetBoolValue
    [PreserveSig] int _17(ref PropertyKey key, IntPtr value);                                                       // SetIUnknownValue
    [PreserveSig] int _18(ref PropertyKey key, out IntPtr value);                                                   // GetIUnknownValue
    [PreserveSig] int _19(ref PropertyKey key, ref Guid value);                                                     // SetGuidValue
    [PreserveSig] int GetGuidValue(ref PropertyKey key, out Guid value);
}
