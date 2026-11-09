using System.Threading.Channels;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Pika.Agent;

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
        matched = PikaSerialDevices.IsPikaUsb(vendor, product);
        break;
    }
}
if (!matched) throw new InvalidOperationException($"Serial device is not the configured Pika CH340K ({PikaProtocol.UsbVendorId}:{PikaProtocol.UsbProductId})");
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
using var port = new SerialPort(device, PikaProtocol.BaudRate, Parity.None, 8, StopBits.One)
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
var mode = MotorMode.ReadOnly;
// `enabled` is measured feedback. `wrotePower` is deliberately sticky: once any
// power write was sent, disconnect and agent exit always write a disable.
bool enabled = false, wrotePower = false;
double feedbackAt = -100, heartbeatAt = Now(), reportedAt = -100, position = 0, speed = 0;
string lastMotorRequest = ""; double motorRequestAt = -100;
// The ordinary target; meaningful only while mode == Targeting.
double targetWidth = 0, targetAngle = 0, stepAt = 0, lastTargetAt = 0; double? arrivedAt = null;
JsonElement? telemetry = null;
PikaDriverStatus? driverStatus = null;
string message = "Reading telemetry; no enable or target command sent.";
PikaPowerFeedback? powerFeedback = null;
bool Fresh() => telemetry is not null && Now() - feedbackAt < PikaProtocol.FeedbackFreshSeconds;
bool HeartbeatExpired() => Now() - heartbeatAt > PikaProtocol.HeartbeatTimeoutSeconds;
bool IsArmed() => mode is MotorMode.Armed or MotorMode.Targeting;
var wireEvidence = new PikaDriverFaultRecorder();
void Write(PikaCommand command, double value = 0)
{
    byte[] bytes = PikaProtocol.Packet(command, value);
    var attempt = wireEvidence.BeginWrite(Now(), command, value, bytes);
    try { port.Write(bytes, 0, bytes.Length); PikaDriverFaultRecorder.FinishWrite(attempt, Now()); }
    catch (Exception error) { PikaDriverFaultRecorder.FinishWrite(attempt, Now(), error); throw; }
}
var following = new PikaFollowSession(angle => Write(PikaCommand.Position, angle), commandPeriodSeconds);
var measuredResume = new PikaMeasuredHoldResume(Write);
// The single Pika-E transition function.
void SetMode(MotorMode next)
{
    if (next == MotorMode.Targeting) arrivedAt = null;
    mode = next;
    Debug.Assert(mode != MotorMode.Targeting || following.Phase is not (PikaFollowPhase.Following or PikaFollowPhase.Holding),
        "A Pika position target coexists with a follow session.");
    Debug.Assert((mode == MotorMode.Resuming) == measuredResume.Active, "Motor mode and measured-hold resume disagree.");
}
bool FollowFresh() => telemetry is not null && Now() - feedbackAt < PikaFollowSession.InputDeadlineSeconds;
void RequireHealthyDriver()
{
    if (driverStatus?.HasFault == true) throw new InvalidOperationException(driverStatus.Text + "; resolve the reported fault before sending a target or enabling.");
}
void Disable(string reason)
{
    measuredResume.Cancel();
    following.Cancel(reason, Now());
    SetMode(MotorMode.ReadOnly);
    Write(PikaCommand.Disable); powerFeedback = new(false, Now()); message = reason + " Disable requested; verify motor status.";
}
void RecordMotorRequest(string id) { lastMotorRequest = id; motorRequestAt = Now(); }
// Applies one client command. Returns the new follow session ID for follow-start.
string? HandleCommand(string action, string id, JsonElement command)
{
    switch (action)
    {
        case "heartbeat": heartbeatAt = Now(); return null;
        case "disable": Disable("Operator stop."); RecordMotorRequest(id); return null;
        case "enable":
            RequireHealthyDriver();
            if (mode == MotorMode.Resuming) throw new InvalidOperationException("Wait for measured-hold resume feedback or disable first.");
            if (!Fresh() || !command.TryGetProperty("clear", out var clear) || clear.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Fresh telemetry and finger-clear confirmation are required");
            following.Cancel("Enable requested; previous following targets cancelled.", Now());
            Write(PikaCommand.Enable); wrotePower = true;
            if (mode == MotorMode.ReadOnly) SetMode(MotorMode.Armed);
            heartbeatAt = Now(); message = "Enable requested; waiting for enabled feedback.";
            RecordMotorRequest(id);
            powerFeedback = new(true, motorRequestAt);
            return null;
        case "position":
        {
            RequireHealthyDriver();
            // Armed exactly: not read-only, not already targeting and not resuming.
            if (!Fresh() || mode != MotorMode.Armed || !enabled || HeartbeatExpired() || following.Active)
                throw new InvalidOperationException("Fresh enabled feedback, live client and an idle gripper are required");
            double angle = command.GetProperty("value").GetDouble(), width = PikaProtocol.Width(angle);
            if (!PikaProtocol.ValidAngle(angle) || !PikaProtocol.ValidOpening(width))
                throw new InvalidOperationException("Opening target must be within 0–90 mm");
            following.Cancel("Ordinary Pika position command.", Now());
            powerFeedback = null;
            Write(PikaCommand.Position, angle); targetAngle = angle; lastTargetAt = Now(); targetWidth = width; stepAt = Now();
            SetMode(MotorMode.Targeting);
            message = $"Target {width:F2} mm; waiting for measured arrival.";
            RecordMotorRequest(id);
            return null;
        }
        case "follow-start":
            RequireHealthyDriver();
            if (!FollowFresh() || mode != MotorMode.Armed || !enabled || HeartbeatExpired())
                throw new InvalidOperationException("Fresh enabled feedback, live client and an idle gripper are required to follow.");
            powerFeedback = null;
            string session = following.Start(Now()); heartbeatAt = Now(); message = following.Message;
            return session;
        case "follow-update":
            RequireHealthyDriver();
            if (!FollowFresh() || !IsArmed() || !enabled)
                throw new InvalidOperationException("Fresh enabled Pika feedback is required to follow.");
            following.Update(command.GetProperty("follow_session").GetString() ?? "",
                command.GetProperty("sequence").GetInt64(), command.GetProperty("opening_mm").GetDouble(), Now());
            heartbeatAt = Now(); message = following.Message;
            return null;
        case "hold":
            if (mode == MotorMode.Resuming)
            {
                Disable("Measured-hold resume cancelled before verification.");
                RecordMotorRequest(id);
                return null;
            }
            // Retire an ordinary target too, even when fresh feedback is missing.
            if (mode == MotorMode.Targeting) SetMode(MotorMode.Armed);
            powerFeedback = null;
            following.Hold(FollowFresh() && IsArmed() && enabled ? position : null, now: Now(), enabled: IsArmed() && enabled, driverStatus: driverStatus);
            heartbeatAt = Now(); message = following.Message;
            RecordMotorRequest(id);
            return null;
        case "resume-hold":
            RequireHealthyDriver();
            // ReadOnly exactly: not armed, not targeting and not already resuming.
            if (!Fresh() || Now() - feedbackAt >= PikaMeasuredHoldResume.MaximumFeedbackAgeSeconds || HeartbeatExpired() ||
                mode != MotorMode.ReadOnly || enabled || following.Active ||
                !command.TryGetProperty("clear", out var resumeClear) || resumeClear.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Fresh disabled feedback and a live explicit measured-hold request are required.");
            following.Cancel("Measured hold requested; prior destinations discarded.", Now()); powerFeedback = null;
            wrotePower = true; RecordMotorRequest(id);
            measuredResume.Start(motorRequestAt, feedbackAt, position, speed, enabled, pending: false, following.Active,
                command.GetProperty("expected_angle_rad").GetDouble(), command.GetProperty("expected_feedback_time_s").GetDouble(), id);
            SetMode(MotorMode.Resuming);
            heartbeatAt = Now(); message = measuredResume.Message;
            return null;
        case "disconnect":
            if (IsArmed() || wrotePower) Disable("Session closed.");
            quit.Cancel(); return null;
        default: throw new InvalidOperationException("Unsupported Pika action");
    }
}
void ProcessLine(string line)
{
    string id = "";
    try
    {
        using var document = JsonDocument.Parse(line);
        var command = document.RootElement;
        id = command.GetProperty("id").GetString() ?? "";
        string action = command.GetProperty("action").GetString() ?? "";
        string? followSession = HandleCommand(action, id, command);
        Emit(new { type = "ack", id, ok = true, message, follow_session = followSession,
            follow_audit = action is "hold" or "disable" ? following.Audit : null });
    }
    catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
    { Emit(new { type = "ack", id, ok = false, message = e.Message, follow_audit = following.Audit }); }
}
var parser = new PikaJsonStream();
long serialReadCalls = 0, serialReadTimeouts = 0, serialBytes = 0, validTelemetryObjects = 0, invalidTelemetryObjects = 0;
var telemetryRejectionPaths = new Dictionary<string, long>(StringComparer.Ordinal);
double previousValidAt = -1, lastValidInterval = 0, maximumValidInterval = 0, maximumReadSeconds = 0, maximumLoopSeconds = 0, previousLoopAt = Now();
byte[] buffer = new byte[4096]; var decoder = Encoding.UTF8.GetDecoder(); char[] characters = new char[8192];
void ObserveTelemetry(JsonElement data)
{
    if (!PikaProtocol.TryReadTelemetry(data, out var sample, out string reason))
    {
        ++invalidTelemetryObjects;
        telemetryRejectionPaths[reason] = telemetryRejectionPaths.GetValueOrDefault(reason) + 1;
        return;
    }
    ++validTelemetryObjects;
    double validAt = Now();
    if (previousValidAt >= 0) { lastValidInterval = validAt - previousValidAt; maximumValidInterval = Math.Max(maximumValidInterval, lastValidInterval); }
    previousValidAt = validAt;
    telemetry = data; position = sample.Position; speed = sample.Speed; enabled = sample.Enabled; feedbackAt = Now();
    driverStatus = sample.Driver;
    following.ObserveFeedback(feedbackAt, sample.Position, sample.Speed, sample.Enabled, sample.RawStatus);
    if (wireEvidence.Observe(feedbackAt, data, sample) is { } evidence)
        Emit(new { type = "driver_fault_evidence", evidence });
    if (powerFeedback is { } acknowledgement && acknowledgement.IsConfirmed(enabled, feedbackAt))
    {
        message = enabled ? "Enabled; measured motor feedback confirmed." :
            message.Replace("Disable requested; verify motor status.", "Disabled feedback confirmed.", StringComparison.Ordinal);
        powerFeedback = null;
    }
    if (mode != MotorMode.Targeting) return;
    if (Math.Abs(PikaProtocol.Width(position) - targetWidth) > PikaProtocol.ArrivalToleranceMm || Math.Abs(speed) > PikaProtocol.StationarySpeedRadiansPerSecond)
    { arrivedAt = null; return; }
    arrivedAt ??= Now();
    if (Now() - arrivedAt >= .15)
    {
        message = $"Reached {PikaProtocol.Width(position):F2} mm, within {PikaProtocol.ArrivalToleranceMm:F1} mm of target.";
        SetMode(MotorMode.Armed);
    }
}
try
{
    port.Open();
    if (PikaSerialDevices.ResolveLinux(configuredDevice) != device)
        throw new IOException("The assigned Pika USB identity changed while opening; no motor command was sent.");
    Emit(new { type = "ready", connected = true, transport = "linux-serial", device = configuredDevice, system_device = device, baud = PikaProtocol.BaudRate });
    while (!quit.IsCancellationRequested)
    {
        double loopAt = Now(); maximumLoopSeconds = Math.Max(maximumLoopSeconds, loopAt - previousLoopAt); previousLoopAt = loopAt;
        for (int batch = 0; batch < 32 && input.Reader.TryRead(out string? line); batch++) ProcessLine(line);
        double readAt = Now();
        try
        {
            ++serialReadCalls;
            int count = port.Read(buffer, 0, buffer.Length);
            wireEvidence.Read(Now(), buffer.AsSpan(0, count));
            serialBytes += count;
            int length = decoder.GetChars(buffer, 0, count, characters, 0);
            foreach (var data in parser.Push(new string(characters, 0, length))) ObserveTelemetry(data);
        }
        catch (TimeoutException) { ++serialReadTimeouts; }
        finally { maximumReadSeconds = Math.Max(maximumReadSeconds, Now() - readAt); }
        if (mode == MotorMode.Resuming)
        {
            measuredResume.Tick(Now(), feedbackAt, position, speed, enabled, Fresh());
            message = measuredResume.Message;
            if (!measuredResume.Active) SetMode(measuredResume.Succeeded ? MotorMode.Armed : MotorMode.ReadOnly);
        }
        if (following.Active)
        {
            try { following.Tick(Now(), FollowFresh() ? position : null, IsArmed() && enabled, driverStatus); message = following.Message; }
            catch (InvalidOperationException error) { message = error.Message; }
        }
        // Explicit/follow-deadline hold retains the enabled position servo. It is
        // separate from disable/disconnect; never drop a grasp on a stop request.
        if (IsArmed() && !following.Holding && (!Fresh() || HeartbeatExpired() || (mode == MotorMode.Targeting && Now() - stepAt > 6)))
            Disable("Telemetry, client heartbeat or movement deadline expired.");
        // The vendor example streams the current target every 30 ms. Keep the
        // same bounded target, only while feedback and the client remain live.
        if (mode == MotorMode.Targeting && enabled && Fresh() && !HeartbeatExpired() && Now() - lastTargetAt >= .03)
        { Write(PikaCommand.Position, targetAngle); lastTargetAt = Now(); }
        if (!following.Holding && Now() - heartbeatAt > 8) { message = "Client disconnected."; break; }
        if (Now() - reportedAt >= .1)
        {
            Emit(new PikaAgentState
            {
                Connected = true, Fresh = Fresh(), Enabled = enabled, Armed = IsArmed(), Pending = mode == MotorMode.Targeting,
                DriverStatus = driverStatus?.Bits, DriverFaultMask = driverStatus?.FaultMask,
                DriverFault = driverStatus?.HasFault, DriverStatusText = driverStatus?.Text,
                DriverFaultEvidence = wireEvidence.LastSummary,
                Following = following.Active, FollowSession = following.SessionId, FollowSequence = following.Sequence,
                FollowOpeningMm = following.OpeningMm, Holding = following.Holding,
                FollowAudit = following.Audit,
                ResumingHold = measuredResume.Active,
                LastMeasuredHoldResume = measuredResume.Audit,
                AgentDiagnostics = new {
                    agent_time_s = Now(), serial_read_calls = serialReadCalls, serial_read_timeouts = serialReadTimeouts, serial_bytes = serialBytes,
                    valid_telemetry_objects = validTelemetryObjects, invalid_telemetry_objects = invalidTelemetryObjects,
                    telemetry_rejection_paths = telemetryRejectionPaths, parser = parser.Diagnostics,
                    last_valid_decode_interval_s = lastValidInterval, maximum_valid_decode_interval_s = maximumValidInterval,
                    maximum_read_and_parse_s = maximumReadSeconds, maximum_loop_interval_s = maximumLoopSeconds, maximum_stdout_emit_s = maximumEmitSeconds,
                    clock_note = "Agent receive/decode intervals; not firmware sample timestamps or a hardware synchronization guarantee."
                },
                Message = message, Telemetry = telemetry, AgeSeconds = telemetry is null ? null : Now() - feedbackAt,
                LastMotorRequest = lastMotorRequest, RequestTimeSeconds = lastMotorRequest.Length == 0 ? null : motorRequestAt,
                FeedbackTimeSeconds = telemetry is null ? null : feedbackAt
            });
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
        if (IsArmed() || wrotePower) try { Disable("Agent stopping."); } catch (Exception) { }
        port.Close();
    }
    quit.Cancel();
}

}
catch (Exception e) { Console.Error.WriteLine("Pika serial unavailable: " + e.Message); Environment.ExitCode = 1; }
