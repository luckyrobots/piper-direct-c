using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace LuckyRobots.Piper.Camera;

public sealed record VideoDevice(int Index, string Name, string Path, string UsbLocation = "", string InstanceId = "");

public static class VideoDevices
{
    public static IReadOnlyList<VideoDevice> List() => OperatingSystem.IsWindows() ? WindowsList() : OperatingSystem.IsLinux() ? LinuxList() : [];

    /// <summary>Linux colour cameras by their stable by-path name, which <see cref="ColorPreview"/>
    /// opens directly with V4L2. A camera is the first video node of a USB interface; the kernel's
    /// second node carries metadata only. D405 nodes are left out: depth cameras are chosen by serial.
    /// Reads names from sysfs and never opens a device.</summary>
    private static IReadOnlyList<VideoDevice> LinuxList()
    {
        const string folder = "/dev/v4l/by-path";
        if (!Directory.Exists(folder)) return [];
        var devices = new List<VideoDevice>();
        foreach (string link in Directory.EnumerateFiles(folder, "*-usb-*-video-index0").Order(StringComparer.Ordinal))
        {
            string? node;
            try { node = File.ResolveLinkTarget(link, true)?.Name; }
            catch (IOException) { continue; }
            if (node is null) continue;
            string name;
            try { name = File.ReadAllText("/sys/class/video4linux/" + node + "/name").Trim(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (name.Contains("RealSense", StringComparison.OrdinalIgnoreCase)) continue;
            // "…-usb-0:3.4.1:1.0-video-index0": the port chain names the socket, which survives replugging.
            var port = System.Text.RegularExpressions.Regex.Match(link, @"-usb-\d+:([\d.]+):");
            int colon = name.IndexOf(':');
            devices.Add(new(devices.Count, colon > 0 ? name[..colon].Trim() : name, link, port.Success ? port.Groups[1].Value : ""));
        }
        return devices;
    }

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
                            string devicePath = path?.ToString() ?? "";
                            result.Add(new(index++, name?.ToString() ?? "Camera", devicePath,
                                VideoDeviceLocation.ReadBusPort(devicePath), VideoDeviceLocation.InstanceId(devicePath)));
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
