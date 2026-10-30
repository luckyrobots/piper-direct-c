using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using LuckyRobots.Piper.Pika;

try
{
// Physical USB serial belongs to Linux. Never open the crash-prone WCH
// Windows COM driver, including when launched by the Windows dashboard.
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Pika agent requires the Linux USB serial driver");
int deviceIndex = Array.IndexOf(args, "--device");
if (deviceIndex < 0 || deviceIndex + 1 >= args.Length) throw new ArgumentException("--device is required");
string device = Path.GetFullPath(args[deviceIndex + 1]);
if (!device.StartsWith("/dev/", StringComparison.Ordinal)) throw new ArgumentException("Expected a Linux serial device");
if (!File.Exists(device)) throw new InvalidOperationException("Pika USB is not attached to the Linux serial driver. Prepare the USB connection first.");
string resolved = File.ResolveLinkTarget(device, true)?.FullName ?? device;
// Resolve the class link first: .NET resolves the final link's target, but
// intermediate sysfs links can otherwise leave Parent on the class alias.
var tty = Directory.ResolveLinkTarget($"/sys/class/tty/{Path.GetFileName(resolved)}", true) as DirectoryInfo;
var usb = tty is null ? null : Directory.ResolveLinkTarget(Path.Combine(tty.FullName, "device"), true) as DirectoryInfo;
bool matched = false;
for (int i = 0; usb is not null && i < 10; i++, usb = usb.Parent)
{
    string vendor = Path.Combine(usb.FullName, "idVendor"), product = Path.Combine(usb.FullName, "idProduct");
    if (File.Exists(vendor) && File.Exists(product))
    {
        matched = File.ReadAllText(vendor).Trim() == "1a86" && File.ReadAllText(product).Trim() == "7522";
        break;
    }
}
if (!matched) throw new InvalidOperationException("Serial device is not the configured Pika CH340K (1a86:7522)");

var input = new ConcurrentQueue<string>();
using var quit = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    try { while (await Console.In.ReadLineAsync(quit.Token) is { } line) { if (line.Length <= 4096) input.Enqueue(line); } }
    catch (OperationCanceledException) { }
    finally { quit.Cancel(); }
});
using var port = new SerialPort(device, 460800, Parity.None, 8, StopBits.One)
{
    ReadTimeout = 50, WriteTimeout = 200, Handshake = Handshake.None,
    DtrEnable = true, RtsEnable = true, Encoding = Encoding.UTF8
};
var clock = Stopwatch.StartNew();
double Now() => clock.Elapsed.TotalSeconds;
void Emit(object data) { Console.WriteLine(JsonSerializer.Serialize(data)); Console.Out.Flush(); }
bool armed = false, enabled = false, wrotePower = false;
double feedbackAt = -100, heartbeatAt = Now(), reportedAt = -100, position = 0, speed = 0;
string lastMotorRequest = ""; double motorRequestAt = -100;
double? target = null, arrivedAt = null; double stepAt = 0, targetAngle = 0, lastTargetAt = 0;
JsonElement? telemetry = null;
string message = "Reading telemetry; no enable or target command sent.";
bool Fresh() => telemetry is not null && Now() - feedbackAt < 2;
void Write(int command, double value = 0) { byte[] bytes = PikaProtocol.Packet(command, value); port.Write(bytes, 0, bytes.Length); }
void Disable(string reason)
{
    armed = false; target = arrivedAt = null;
    Write(10); message = reason + " Disable requested; verify motor status.";
}
var parser = new PikaJsonStream();
byte[] buffer = new byte[4096]; var decoder = Encoding.UTF8.GetDecoder(); char[] characters = new char[8192];
try
{
    port.Open(); Emit(new { type = "ready", connected = true, transport = "linux-serial", device, baud = 460800 });
    while (!quit.IsCancellationRequested)
    {
        while (input.TryDequeue(out string? line))
        {
            string id = "";
            try
            {
                using var document = JsonDocument.Parse(line);
                var command = document.RootElement;
                id = command.GetProperty("id").GetString() ?? "";
                string action = command.GetProperty("action").GetString() ?? "";
                switch (action)
                {
                    case "heartbeat": heartbeatAt = Now(); break;
                    case "disable": Disable("Operator stop."); lastMotorRequest = id; motorRequestAt = Now(); break;
                    case "enable":
                        if (!Fresh() || !command.TryGetProperty("clear", out var clear) || clear.ValueKind != JsonValueKind.True)
                            throw new InvalidOperationException("Fresh telemetry and finger-clear confirmation are required");
                        Write(11); wrotePower = armed = true; heartbeatAt = Now(); message = "Enable requested; waiting for enabled feedback.";
                        lastMotorRequest = id; motorRequestAt = Now();
                        break;
                    case "position":
                        if (!Fresh() || !armed || !enabled || Now() - heartbeatAt > 1.5 || target is not null)
                            throw new InvalidOperationException("Fresh enabled feedback, live client and an idle gripper are required");
                        double angle = command.GetProperty("value").GetDouble(), width = PikaProtocol.Width(angle);
                        if (!PikaProtocol.ValidAngle(angle) || !double.IsFinite(width) || width < 0 || width > 90)
                            throw new InvalidOperationException("Opening target must be within 0–90 mm");
                        Write(22, angle); targetAngle = angle; lastTargetAt = Now(); target = width; arrivedAt = null; stepAt = Now(); message = $"Target {width:F2} mm; waiting for measured arrival.";
                        lastMotorRequest = id; motorRequestAt = Now();
                        break;
                    case "disconnect":
                        if (armed || wrotePower) Disable("Session closed.");
                        quit.Cancel(); break;
                    default: throw new InvalidOperationException("Unsupported Pika action");
                }
                Emit(new { type = "ack", id, ok = true, message });
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            { Emit(new { type = "ack", id, ok = false, message = e.Message }); }
        }
        try
        {
            int count = port.Read(buffer, 0, buffer.Length);
            int length = decoder.GetChars(buffer, 0, count, characters, 0);
            foreach (var data in parser.Push(new string(characters, 0, length)))
            {
                if (!PikaProtocol.Telemetry(data, out double p, out double s, out bool on)) continue;
                telemetry = data; position = p; speed = s; enabled = on; feedbackAt = Now();
                if (target is { } goal)
                {
                    if (Math.Abs(PikaProtocol.Width(position) - goal) <= PikaProtocol.ArrivalToleranceMm && Math.Abs(speed) <= .02)
                    {
                        arrivedAt ??= Now();
                        if (Now() - arrivedAt >= .15) { message = $"Reached {PikaProtocol.Width(position):F2} mm, within {PikaProtocol.ArrivalToleranceMm:F1} mm of target."; target = arrivedAt = null; }
                    }
                    else arrivedAt = null;
                }
            }
        }
        catch (TimeoutException) { }
        if (armed && (!Fresh() || Now() - heartbeatAt > 1.5 || (target is not null && Now() - stepAt > 6)))
            Disable("Telemetry, client heartbeat or movement deadline expired.");
        // The vendor example streams the current target every 30 ms. Keep the
        // same bounded target, only while feedback and the client remain live.
        if (armed && enabled && target is not null && Fresh() && Now() - heartbeatAt <= 1.5 && Now() - lastTargetAt >= .03)
        { Write(22, targetAngle); lastTargetAt = Now(); }
        if (Now() - heartbeatAt > 8) { message = "Client disconnected."; break; }
        if (Now() - reportedAt >= .1)
        {
            Emit(new { type = "state", connected = true, fresh = Fresh(), enabled, armed, pending = target is not null,
                message, telemetry, age_s = telemetry is null ? (double?)null : Now() - feedbackAt,
                last_motor_request = lastMotorRequest, request_time_s = lastMotorRequest.Length == 0 ? (double?)null : motorRequestAt,
                feedback_time_s = telemetry is null ? (double?)null : feedbackAt });
            reportedAt = Now();
        }
    }
}
catch (Exception e)
{
    Emit(new { type = "error", connected = false, message = e.Message });
}
finally
{
    if (port.IsOpen)
    {
        if (armed || wrotePower) try { Disable("Agent stopping."); } catch (Exception) { }
        port.Close();
    }
    quit.Cancel();
}

}
catch (Exception e) { Console.Error.WriteLine("Pika serial unavailable: " + e.Message); Environment.ExitCode = 1; }
