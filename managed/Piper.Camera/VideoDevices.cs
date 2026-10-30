using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace LuckyRobots.Piper.Camera;

public sealed record VideoDevice(int Index, string Name, string Path);

public static class VideoDevices
{
    public static IReadOnlyList<VideoDevice> List() => OperatingSystem.IsWindows() ? WindowsList() : [];

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<VideoDevice> WindowsList()
    {
        // Enumerate the same DirectShow category/order that OpenCV's DSHOW uses.
        var creator = (ICreateDevEnum)Activator.CreateInstance(Type.GetTypeFromCLSID(new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"), true)!)!;
        try
        {
            var category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
            if (creator.CreateClassEnumerator(ref category, out var enumeration, 0) != 0) return [];
            try
            {
                var result = new List<VideoDevice>();
                var monikers = new IMoniker[1];
                int index = 0;
                while (enumeration.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    var moniker = monikers[0];
                    try
                    {
                        var iid = typeof(IPropertyBag).GUID;
                        moniker.BindToStorage(null!, null!, ref iid, out object storage);
                        try
                        {
                            var bag = (IPropertyBag)storage;
                            bag.Read("FriendlyName", out object name, IntPtr.Zero);
                            bag.Read("DevicePath", out object path, IntPtr.Zero);
                            result.Add(new(index++, name?.ToString() ?? "Camera", path?.ToString() ?? ""));
                        }
                        finally { Marshal.ReleaseComObject(storage); }
                    }
                    finally { Marshal.ReleaseComObject(moniker); }
                }
                return result;
            }
            finally { Marshal.ReleaseComObject(enumeration); }
        }
        finally { Marshal.ReleaseComObject(creator); }
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker enumerator, int flags);
    }
    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
