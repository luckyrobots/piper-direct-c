using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LuckyRobots.Piper.Pika.Agent;

/// <summary>Process composition only: arguments, USB identity, the serial port,
/// stdio and termination signals. All behaviour is <see cref="PikaAgent"/>.</summary>
internal static class AgentProgram
{
    /// <summary>Test fixture seam. When set, sysfs and /dev are read from
    /// &lt;root&gt;/sys and &lt;root&gt;/dev, and modem lines are left alone because
    /// the fixture serial is a pty. Every identity check still runs, against
    /// that tree; no real /dev/ttyUSB* can be resolved while it is set.</summary>
    public const string FixtureRootVariable = "PIKA_AGENT_FIXTURE_ROOT";

    private sealed class Port(SerialPort port) : IPikaAgentSerial
    {
        public int Read(byte[] buffer) => port.Read(buffer, 0, buffer.Length);
        public void Write(byte[] packet) => port.Write(packet, 0, packet.Length);
    }

    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception e) { Console.Error.WriteLine("Pika serial unavailable: " + e.Message); return 1; }
    }

    private static void Run(string[] args)
    {
        // Physical USB serial belongs to Linux. Never open the crash-prone WCH
        // Windows COM driver, including when launched by the Windows dashboard.
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Pika agent requires the Linux USB serial driver");
        string? fixture = Environment.GetEnvironmentVariable(FixtureRootVariable) is { Length: > 0 } root ? Path.GetFullPath(root) : null;
        string sysRoot = fixture is null ? "/sys" : Path.Combine(fixture, "sys"), devRoot = fixture is null ? "/dev" : Path.Combine(fixture, "dev");
        if (args is ["--list-devices"])
        {
            Console.WriteLine(JsonSerializer.Serialize(PikaSerialDevices.EnumerateLinux(sysRoot, devRoot)));
            return;
        }
        int deviceIndex = Array.IndexOf(args, "--device");
        if (deviceIndex < 0 || deviceIndex + 1 >= args.Length) throw new ArgumentException("--device is required");
        string configuredDevice = Path.GetFullPath(args[deviceIndex + 1]);
        string device = PikaSerialDevices.ResolveLinux(configuredDevice, sysRoot, devRoot);
        int periodIndex = Array.IndexOf(args, "--command-period-ms");
        double commandPeriodSeconds = periodIndex < 0 ? PikaFollowSession.MinimumCommandPeriodSeconds :
            periodIndex + 1 < args.Length && double.TryParse(args[periodIndex + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double milliseconds) ? milliseconds / 1000 :
            throw new ArgumentException("Invalid --command-period-ms");
        if (!device.StartsWith(devRoot + "/", StringComparison.Ordinal)) throw new ArgumentException("Expected a Linux serial device");
        if (!File.Exists(device)) throw new InvalidOperationException("Pika USB is not attached to the Linux serial driver. Prepare the USB connection first.");
        string resolved = File.ResolveLinkTarget(device, true)?.FullName ?? device;
        // Resolve the class link first: .NET resolves the final link's target, but
        // intermediate sysfs links can otherwise leave Parent on the class alias.
        var tty = Directory.ResolveLinkTarget(Path.Combine(sysRoot, "class/tty", Path.GetFileName(resolved)), true) as DirectoryInfo;
        var usb = tty is null ? null : Directory.ResolveLinkTarget(Path.Combine(tty.FullName, "device"), true) as DirectoryInfo;
        bool matched = false;
        for (int i = 0; usb is not null && i < 10; i++, usb = usb.Parent)
        {
            string vendor = Path.Combine(usb.FullName, "idVendor"), product = Path.Combine(usb.FullName, "idProduct");
            if (File.Exists(vendor) && File.Exists(product))
            {
                matched = PikaSerialDevices.IsPikaUsb(vendor, product);
                break;
            }
        }
        if (!matched) throw new InvalidOperationException($"Serial device is not the configured Pika CH340K ({PikaProtocol.UsbVendorId}:{PikaProtocol.UsbProductId})");
        // All bundled agents cooperate on the resolved physical tty, so aliases and
        // separate host sessions cannot open two owners of the same gripper.
        using var deviceLease = new FileStream(Path.Combine(Path.GetTempPath(), "luckyrobots-pika-" + Path.GetFileName(device) + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var port = new SerialPort(device, PikaProtocol.BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 50, WriteTimeout = 200, Handshake = Handshake.None,
            DtrEnable = fixture is null, RtsEnable = fixture is null, Encoding = Encoding.UTF8
        };

        // SIGINT, SIGTERM, SIGHUP, SIGQUIT and process exit all take the one
        // exit path: the runner stops and PikaAgent.Shutdown writes the disable.
        var terminate = new ManualResetEventSlim(); // Process lifetime: the exit handler still uses both.
        var stopped = new ManualResetEventSlim();
        var signals = new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT }
            .Select(signal => PosixSignalRegistration.Create(signal, context => { context.Cancel = true; terminate.Set(); })).ToArray();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { terminate.Set(); stopped.Wait(TimeSpan.FromSeconds(2)); };

        var output = new AgentOutput(line => { Console.Out.WriteLine(line); Console.Out.Flush(); });
        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;
        var serial = new Port(port);
        var agent = new PikaAgent(serial, output, Now, commandPeriodSeconds);
        try
        {
            port.Open();
            if (PikaSerialDevices.ResolveLinux(configuredDevice, sysRoot, devRoot) != device)
                throw new IOException("The assigned Pika USB identity changed while opening; no motor command was sent.");
            output.Required(new { type = "ready", connected = true, transport = "linux-serial", device = configuredDevice, system_device = device, baud = PikaProtocol.BaudRate });
            PikaAgentRunner.Run(agent, serial, Console.In.ReadLine, Now, terminate.WaitHandle);
        }
        catch (Exception e) { agent.Fault(e); }
        finally
        {
            agent.Shutdown();
            output.Drain(TimeSpan.FromSeconds(1));
            if (port.IsOpen) try { port.Close(); } catch (IOException) { }
            stopped.Set();
            foreach (var signal in signals) signal.Dispose();
        }
    }
}
