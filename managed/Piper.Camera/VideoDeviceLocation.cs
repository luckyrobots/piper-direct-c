using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace LuckyRobots.Piper.Camera;

/// <summary>Read-only Windows device-tree metadata; it never opens a camera or changes a driver.</summary>
public static class VideoDeviceLocation
{
    public static string InstanceId(string path)
    {
        int start = path.IndexOf("usb#", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        string[] parts = path[(start + 4)..].Split('#');
        return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0
            ? "USB\\" + parts[0] + "\\" + parts[1] : "";
    }

    public static string BusPort(string location)
    {
        var match = Regex.Match(location, @"^Port_#(\d+)\.Hub_#(\d+)$", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out int port) &&
            int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out int hub) && hub > 0 && port > 0
            ? hub.ToString(CultureInfo.InvariantCulture) + "-" + port.ToString(CultureInfo.InvariantCulture) : "";
    }

    [SupportedOSPlatform("windows")]
    public static string ReadBusPort(string path)
    {
        string instance = InstanceId(path);
        if (instance.Length == 0 || LocateNode(out uint node, instance, 0) != 0) return "";
        var key = new PropertyKey(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 15);
        byte[] buffer = new byte[2048];
        for (int depth = 0; depth < 16; depth++)
        {
            uint size = (uint)buffer.Length;
            if (ReadProperty(node, ref key, out uint type, buffer, ref size, 0) == 0 && type == 0x12 && size <= buffer.Length)
            {
                string busPort = BusPort(Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0'));
                if (busPort.Length > 0) return busPort;
            }
            if (GetParent(out uint parent, node, 0) != 0 || parent == node) break;
            node = parent;
        }
        return "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint LocateNode(out uint node, string instanceId, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static extern uint GetParent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint ReadProperty(uint node, ref PropertyKey key, out uint type, byte[] buffer, ref uint size, uint flags);
}
