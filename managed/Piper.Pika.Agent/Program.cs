using System.Threading.Channels;
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
if (args is ["--list-devices"])
{
    Console.WriteLine(JsonSerializer.Serialize(PikaSerialDevices.EnumerateLinux()));
    return;
}
int deviceIndex = Array.IndexOf(args, "--device");
if (deviceIndex < 0 || deviceIndex + 1 >= args.Length) throw new ArgumentException("--device is required");
string configuredDevice = Path.GetFullPath(args[deviceIndex + 1]);
string device = PikaSerialDevices.ResolveLinux(configuredDevice);
int periodIndex = Array.IndexOf(args, "--command-period-ms");
double commandPeriodSeconds = periodIndex < 0 ? PikaFollowSession.MinimumCommandPeriodSeconds :
    periodIndex + 1 < args.Length && double.TryParse(args[periodIndex + 1], System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double milliseconds) ? milliseconds / 1000 :
    throw new ArgumentException("Invalid --command-period-ms");
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
// All bundled agents cooperate on the resolved physical tty, so aliases and
// separate host sessions cannot open two owners of the same gripper.
using var deviceLease = new FileStream(Path.Combine(Path.GetTempPath(), "luckyrobots-pika-" + Path.GetFileName(device) + ".lock"),
    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

// Backpressure bounds transport memory; the control loop processes at most one
// batch per tick and the following controller retains only the latest target.
var input = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true });
using var quit = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    try { while (await Console.In.ReadLineAsync(quit.Token) is { } line) { if (line.Length <= 4096) await input.Writer.WriteAsync(line, quit.Token); } }
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
double maximumEmitSeconds = 0;
void Emit(object data)
{
    double started = Now();
    Console.WriteLine(JsonSerializer.Serialize(data)); Console.Out.Flush();
    maximumEmitSeconds = Math.Max(maximumEmitSeconds, Now() - started);
}
bool armed = false, enabled = false, wrotePower = false;
double feedbackAt = -100, heartbeatAt = Now(), reportedAt = -100, position = 0, speed = 0;
string lastMotorRequest = ""; double motorRequestAt = -100;
double? target = null, arrivedAt = null; double stepAt = 0, targetAngle = 0, lastTargetAt = 0;
JsonElement? telemetry = null;
PikaDriverStatus? driverStatus = null;
string message = "Reading telemetry; no enable or target command sent.";
PikaPowerFeedback? powerFeedback = null;
bool Fresh() => telemetry is not null && Now() - feedbackAt < 2;
var wireEvidence = new PikaDriverFaultRecorder();
void Write(int command, double value = 0)
{
    byte[] bytes = PikaProtocol.Packet(command, value);
    var attempt = wireEvidence.BeginWrite(Now(), command, value, bytes);
    try { port.Write(bytes, 0, bytes.Length); PikaDriverFaultRecorder.FinishWrite(attempt, Now()); }
    catch (Exception error) { PikaDriverFaultRecorder.FinishWrite(attempt, Now(), error); throw; }
}
var following = new PikaFollowSession(angle => Write(22, angle), commandPeriodSeconds);
var measuredResume = new PikaMeasuredHoldResume(Write);
bool FollowFresh() => telemetry is not null && Now() - feedbackAt < PikaFollowSession.InputDeadlineSeconds;
void RequireHealthyDriver()
{
    if (driverStatus?.HasFault == true) throw new InvalidOperationException(driverStatus.Text + "; resolve the reported fault before sending a target or enabling.");
}
void Disable(string reason)
{
    measuredResume.Cancel();
    following.Cancel(reason, Now());
    armed = false; target = arrivedAt = null;
    Write(10); powerFeedback = new(false, Now()); message = reason + " Disable requested; verify motor status.";
}
var parser = new PikaJsonStream();
long serialReadCalls = 0, serialReadTimeouts = 0, serialBytes = 0, validTelemetryObjects = 0, invalidTelemetryObjects = 0;
var telemetryRejectionPaths = new Dictionary<string, long>(StringComparer.Ordinal);
double previousValidAt = -1, lastValidInterval = 0, maximumValidInterval = 0, maximumReadSeconds = 0, maximumLoopSeconds = 0, previousLoopAt = Now();
byte[] buffer = new byte[4096]; var decoder = Encoding.UTF8.GetDecoder(); char[] characters = new char[8192];
try
{
    port.Open();
    if (PikaSerialDevices.ResolveLinux(configuredDevice) != device)
        throw new IOException("The assigned Pika USB identity changed while opening; no motor command was sent.");
    Emit(new { type = "ready", connected = true, transport = "linux-serial", device = configuredDevice, system_device = device, baud = 460800 });
    while (!quit.IsCancellationRequested)
    {
        double loopAt = Now(); maximumLoopSeconds = Math.Max(maximumLoopSeconds, loopAt - previousLoopAt); previousLoopAt = loopAt;
        for (int batch = 0; batch < 32 && input.Reader.TryRead(out string? line); batch++)
        {
            string id = "";
            try
            {
                using var document = JsonDocument.Parse(line);
                var command = document.RootElement;
                id = command.GetProperty("id").GetString() ?? "";
                string action = command.GetProperty("action").GetString() ?? "";
                string? followSession = null;
                switch (action)
                {
                    case "heartbeat": heartbeatAt = Now(); break;
                    case "disable": Disable("Operator stop."); lastMotorRequest = id; motorRequestAt = Now(); break;
                    case "enable":
                        RequireHealthyDriver();
                        if (measuredResume.Active) throw new InvalidOperationException("Wait for measured-hold resume feedback or disable first.");
                        if (!Fresh() || !command.TryGetProperty("clear", out var clear) || clear.ValueKind != JsonValueKind.True)
                            throw new InvalidOperationException("Fresh telemetry and finger-clear confirmation are required");
                        following.Cancel("Enable requested; previous following targets cancelled.", Now());
                        Write(11); wrotePower = armed = true; heartbeatAt = Now(); message = "Enable requested; waiting for enabled feedback.";
                        lastMotorRequest = id; motorRequestAt = Now();
                        powerFeedback = new(true, motorRequestAt);
                        break;
                    case "position":
                        RequireHealthyDriver();
                        if (!Fresh() || !armed || !enabled || Now() - heartbeatAt > 1.5 || target is not null || following.Active || measuredResume.Active)
                            throw new InvalidOperationException("Fresh enabled feedback, live client and an idle gripper are required");
                        double angle = command.GetProperty("value").GetDouble(), width = PikaProtocol.Width(angle);
                        if (!PikaProtocol.ValidAngle(angle) || !double.IsFinite(width) || width < 0 || width > 90)
                            throw new InvalidOperationException("Opening target must be within 0–90 mm");
                        following.Cancel("Ordinary Pika position command.", Now());
                        powerFeedback = null;
                        Write(22, angle); targetAngle = angle; lastTargetAt = Now(); target = width; arrivedAt = null; stepAt = Now(); message = $"Target {width:F2} mm; waiting for measured arrival.";
                        lastMotorRequest = id; motorRequestAt = Now();
                        break;
                    case "follow-start":
                        RequireHealthyDriver();
                        if (!FollowFresh() || !armed || !enabled || Now() - heartbeatAt > 1.5 || target is not null || measuredResume.Active)
                            throw new InvalidOperationException("Fresh enabled feedback, live client and an idle gripper are required to follow.");
                        powerFeedback = null;
                        followSession = following.Start(Now()); heartbeatAt = Now(); message = following.Message;
                        break;
                    case "follow-update":
                        RequireHealthyDriver();
                        if (!FollowFresh() || !armed || !enabled)
                            throw new InvalidOperationException("Fresh enabled Pika feedback is required to follow.");
                        following.Update(command.GetProperty("follow_session").GetString() ?? "",
                            command.GetProperty("sequence").GetInt64(), command.GetProperty("opening_mm").GetDouble(), Now());
                        heartbeatAt = Now(); message = following.Message;
                        break;
                    case "hold":
                        if (measuredResume.Active)
                        {
                            Disable("Measured-hold resume cancelled before verification.");
                            lastMotorRequest = id; motorRequestAt = Now();
                            break;
                        }
                        // Retire an ordinary target too, even when fresh feedback is missing.
                        target = arrivedAt = null;
                        powerFeedback = null;
                        following.Hold(FollowFresh() && armed && enabled ? position : null, now: Now(), enabled: armed && enabled, driverStatus: driverStatus);
                        heartbeatAt = Now(); message = following.Message;
                        lastMotorRequest = id; motorRequestAt = Now();
                        break;
                    case "resume-hold":
                        RequireHealthyDriver();
                        if (!Fresh() || Now() - feedbackAt >= PikaMeasuredHoldResume.MaximumFeedbackAgeSeconds || Now() - heartbeatAt > 1.5 ||
                            armed || enabled || target is not null || following.Active || measuredResume.Active ||
                            !command.TryGetProperty("clear", out var resumeClear) || resumeClear.ValueKind != JsonValueKind.True)
                            throw new InvalidOperationException("Fresh disabled feedback and a live explicit measured-hold request are required.");
                        following.Cancel("Measured hold requested; prior destinations discarded.", Now()); powerFeedback = null;
                        wrotePower = true; armed = false; lastMotorRequest = id; motorRequestAt = Now();
                        measuredResume.Start(motorRequestAt, feedbackAt, position, speed, enabled, target is not null, following.Active,
                            command.GetProperty("expected_angle_rad").GetDouble(), command.GetProperty("expected_feedback_time_s").GetDouble(), id);
                        heartbeatAt = Now(); message = measuredResume.Message;
                        break;
                    case "disconnect":
                        if (armed || wrotePower) Disable("Session closed.");
                        quit.Cancel(); break;
                    default: throw new InvalidOperationException("Unsupported Pika action");
                }
                Emit(new { type = "ack", id, ok = true, message, follow_session = followSession,
                    follow_audit = action is "hold" or "disable" ? following.Audit : null });
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
            { Emit(new { type = "ack", id, ok = false, message = e.Message, follow_audit = following.Audit }); }
        }
        double readAt = Now();
        try
        {
            ++serialReadCalls;
            int count = port.Read(buffer, 0, buffer.Length);
            wireEvidence.Read(Now(), buffer.AsSpan(0, count));
            serialBytes += count;
            int length = decoder.GetChars(buffer, 0, count, characters, 0);
            foreach (var data in parser.Push(new string(characters, 0, length)))
            {
                if (!PikaProtocol.Telemetry(data, out double p, out double s, out bool on))
                {
                    ++invalidTelemetryObjects;
                    string reason = PikaProtocol.TelemetryRejectionPath(data);
                    telemetryRejectionPaths[reason] = telemetryRejectionPaths.GetValueOrDefault(reason) + 1;
                    continue;
                }
                ++validTelemetryObjects;
                double validAt = Now();
                if (previousValidAt >= 0) { lastValidInterval = validAt - previousValidAt; maximumValidInterval = Math.Max(maximumValidInterval, lastValidInterval); }
                previousValidAt = validAt;
                telemetry = data; position = p; speed = s; enabled = on; feedbackAt = Now();
                PikaDriverStatus.TryRead(data.GetProperty("motorstatus").GetProperty("Status").GetString(), out driverStatus);
                following.ObserveFeedback(feedbackAt, p, s, on, data.GetProperty("motorstatus").GetProperty("Status").GetString() ?? "invalid");
                if (wireEvidence.Observe(feedbackAt, data, p, s, on) is { } evidence)
                    Emit(new { type = "driver_fault_evidence", evidence });
                if (powerFeedback is { } acknowledgement && acknowledgement.IsConfirmed(enabled, feedbackAt))
                {
                    message = enabled ? "Enabled; measured motor feedback confirmed." :
                        message.Replace("Disable requested; verify motor status.", "Disabled feedback confirmed.", StringComparison.Ordinal);
                    powerFeedback = null;
                }
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
        catch (TimeoutException) { ++serialReadTimeouts; }
        finally { maximumReadSeconds = Math.Max(maximumReadSeconds, Now() - readAt); }
        if (measuredResume.Active)
        {
            measuredResume.Tick(Now(), feedbackAt, position, speed, enabled, Fresh());
            message = measuredResume.Message;
            if (!measuredResume.Active) armed = measuredResume.Succeeded;
        }
        if (following.Active)
        {
            try { following.Tick(Now(), FollowFresh() ? position : null, armed && enabled, driverStatus); message = following.Message; }
            catch (InvalidOperationException error) { message = error.Message; }
        }
        // Explicit/follow-deadline hold retains the enabled position servo. It is
        // separate from disable/disconnect; never drop a grasp on a stop request.
        if (armed && !following.Holding && (!Fresh() || Now() - heartbeatAt > 1.5 || (target is not null && Now() - stepAt > 6)))
            Disable("Telemetry, client heartbeat or movement deadline expired.");
        // The vendor example streams the current target every 30 ms. Keep the
        // same bounded target, only while feedback and the client remain live.
        if (armed && enabled && target is not null && Fresh() && Now() - heartbeatAt <= 1.5 && Now() - lastTargetAt >= .03)
        { Write(22, targetAngle); lastTargetAt = Now(); }
        if (!following.Holding && Now() - heartbeatAt > 8) { message = "Client disconnected."; break; }
        if (Now() - reportedAt >= .1)
        {
            Emit(new { type = "state", connected = true, fresh = Fresh(), enabled, armed, pending = target is not null,
                driver_status = driverStatus?.Bits, driver_fault_mask = driverStatus?.FaultMask,
                driver_fault = driverStatus?.HasFault, driver_status_text = driverStatus?.Text,
                driver_fault_evidence = wireEvidence.LastSummary,
                following = following.Active, follow_session = following.SessionId, follow_sequence = following.Sequence,
                follow_opening_mm = following.OpeningMm, holding = following.Holding,
                follow_audit = following.Audit,
                follow_minimum_command_period_s = following.ConfiguredCommandPeriodSeconds,
                follow_maximum_command_hz = 1 / following.ConfiguredCommandPeriodSeconds,
                resuming_hold = measuredResume.Active,
                last_measured_hold_resume = measuredResume.Audit,
                agent_diagnostics = new {
                    agent_time_s = Now(), serial_read_calls = serialReadCalls, serial_read_timeouts = serialReadTimeouts, serial_bytes = serialBytes,
                    valid_telemetry_objects = validTelemetryObjects, invalid_telemetry_objects = invalidTelemetryObjects,
                    telemetry_rejection_paths = telemetryRejectionPaths, parser = parser.Diagnostics,
                    last_valid_decode_interval_s = lastValidInterval, maximum_valid_decode_interval_s = maximumValidInterval,
                    maximum_read_and_parse_s = maximumReadSeconds, maximum_loop_interval_s = maximumLoopSeconds, maximum_stdout_emit_s = maximumEmitSeconds,
                    clock_note = "Agent receive/decode intervals; not firmware sample timestamps or a hardware synchronization guarantee."
                },
                verified_maximum_opening_speed_mm_s = (double?)null, physical_replay_available = false,
                message, telemetry, age_s = telemetry is null ? (double?)null : Now() - feedbackAt,
                last_motor_request = lastMotorRequest, request_time_s = lastMotorRequest.Length == 0 ? (double?)null : motorRequestAt,
                feedback_time_s = telemetry is null ? (double?)null : feedbackAt });
            reportedAt = Now();
        }
    }
}
catch (Exception e)
{
    Emit(new { type = "error", connected = false, message = e.Message, follow_audit = following.Audit });
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
