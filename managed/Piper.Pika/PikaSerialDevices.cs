using System.Globalization;

namespace LuckyRobots.Piper.Pika;

public sealed record PikaSerialDevice(string Device, string SystemDevice, string? HostBusId, string[] Aliases);

/// <summary>Reads sysfs only. Identical, serial-number-free CH340 adapters must
/// never be distinguished by a colliding by-id symlink or tty enumeration order.</summary>
public static class PikaSerialDevices
{
    public const string HostPrefix = "/dev/pika/by-host/";

    public static IReadOnlyDictionary<string, string> ParseVhciStatus(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 7 || fields[0] is not ("hs" or "ss") || fields[2] != "006" || fields[6] == "0-0" ||
                !uint.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint remote) || remote == 0) continue;
            string host = $"{remote >> 16}-{remote & 0xffff}";
            if (!result.TryAdd(fields[6], host)) throw new IOException("Ambiguous USBIP attachment identity.");
        }
        return result;
    }

    public static IReadOnlyList<PikaSerialDevice> EnumerateLinux(string sysRoot = "/sys", string devRoot = "/dev")
    {
        var remote = new Dictionary<string, string>(StringComparer.Ordinal);
        string platform = Path.Combine(sysRoot, "devices/platform");
        if (Directory.Exists(platform))
            foreach (string controller in Directory.EnumerateDirectories(platform, "vhci_hcd.*"))
            {
                string status = Path.Combine(controller, "status");
                if (!File.Exists(status)) continue;
                foreach (var mapping in ParseVhciStatus(File.ReadAllText(status)))
                    if (!remote.TryAdd(mapping.Key, mapping.Value)) throw new IOException("Ambiguous USBIP attachment identity.");
            }
        string classes = Path.Combine(sysRoot, "class/tty");
        if (!Directory.Exists(classes)) return [];
        var result = new List<PikaSerialDevice>();
        foreach (string tty in Directory.EnumerateDirectories(classes, "ttyUSB*"))
        {
            var resolvedTty = Directory.ResolveLinkTarget(tty, true) as DirectoryInfo;
            var usb = resolvedTty is null ? null : Directory.ResolveLinkTarget(Path.Combine(resolvedTty.FullName, "device"), true) as DirectoryInfo;
            for (int depth = 0; usb is not null && depth < 12; depth++, usb = usb.Parent)
            {
                string vendor = Path.Combine(usb.FullName, "idVendor"), product = Path.Combine(usb.FullName, "idProduct");
                if (!File.Exists(vendor) || !File.Exists(product)) continue;
                if (File.ReadAllText(vendor).Trim() != "1a86" || File.ReadAllText(product).Trim() != "7522") break;
                string actual = Path.Combine(devRoot, Path.GetFileName(tty));
                var aliases = new List<string> { actual };
                foreach (string kind in new[] { "by-id", "by-path" })
                {
                    string directory = Path.Combine(devRoot, "serial", kind);
                    if (!Directory.Exists(directory)) continue;
                    foreach (string link in Directory.EnumerateFiles(directory))
                        if (File.ResolveLinkTarget(link, true)?.FullName == actual) aliases.Add(link);
                }
                remote.TryGetValue(usb.Name, out string? host);
                string device = host is not null ? HostPrefix + host :
                    aliases.FirstOrDefault(alias => alias.Contains("/by-path/", StringComparison.Ordinal)) ?? actual;
                aliases.Add(device);
                result.Add(new(device, actual, host, aliases.Distinct(StringComparer.Ordinal).ToArray()));
                break;
            }
        }
        return result;
    }

    public static string ResolveLinux(string configured)
    {
        var devices = EnumerateLinux();
        var matches = devices.Where(item => item.Device == configured || item.Aliases.Contains(configured, StringComparer.Ordinal)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The assigned Pika is unavailable or ambiguous. Refresh gripper devices in Settings; the plugin will not substitute another gripper.");
        return matches[0].SystemDevice;
    }
}
