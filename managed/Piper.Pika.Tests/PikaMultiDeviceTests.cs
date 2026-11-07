using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

internal static class PikaMultiDeviceTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string a = "/dev/pika/by-host/11-4", b = "/dev/pika/by-host/15-4";
        string ownerA = Guid.NewGuid().ToString(), ownerB = Guid.NewGuid().ToString();
        var discovery = new Discovery(a, b);
        var agents = new Dictionary<string, Session>();
        await using var registry = new PikaSessionRegistry(a, "unused", "Ubuntu", discovery: discovery,
            factory: (device, distro) => agents[device + "@" + distro] = new Session(device, distro));
        check(registry.Snapshots().Count == 0 && agents.Count == 0, "Snapshot must not open or register a serial agent.");
        var inventory = await registry.DiscoverAsync("Ubuntu", default);
        check(inventory["devices"]!.AsArray().Count == 2 && agents.Count == 0, "Inventory opened an agent.");
        await registry.ExecuteAsync("connect", Input(a, ownerA), default);
        await registry.ExecuteAsync("connect", Input(b, ownerB), default);
        await registry.ExecuteAsync("enable", Input(a, ownerA), default);
        await registry.ExecuteAsync("enable", Input(b, ownerB), default);
        await registry.ExecuteAsync("position", Input(a, ownerA, .25), default);
        await registry.ExecuteAsync("position", Input(b, ownerB, .75), default);
        var first = agents[a + "@Ubuntu"]; var second = agents[b + "@Ubuntu"];
        check(agents.Count == 2 && first.State["value"]!.GetValue<double>() == .25 && second.State["value"]!.GetValue<double>() == .75,
            "Two selected grippers shared a mutable serial target.");
        check(registry.Snapshot()["device"]!.GetValue<string>() == a && registry.Snapshots().Count == 2,
            "Explicit second-device control changed the legacy default or hid the first session.");
        await Reject(() => registry.ExecuteAsync("position", Input(a, ownerB, .1), default), "Cross-device owner accepted.");
        await registry.ExecuteAsync("heartbeat", Input(b, ownerB), default);
        check(first.Owner == ownerA && second.Owner == ownerB && second.State["enabled"]!.GetValue<bool>(),
            "Foreign-owner rejection changed another gripper.");

        // Simulate a slow serial ACK from one independent process. Another
        // gripper's heartbeat must complete without waiting behind that I/O.
        first.PendingHeartbeat = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocked = registry.ExecuteAsync("heartbeat", Input(a, ownerA), default);
        await registry.ExecuteAsync("heartbeat", Input(b, ownerB), default).WaitAsync(TimeSpan.FromSeconds(1));
        check(!blocked.IsCompleted, "Fixture did not retain the delayed first-device ACK.");
        first.PendingHeartbeat.SetResult(); await blocked;

        // One failed powered session retains all of its evidence without
        // preventing the independently identified gripper from operating.
        first.State["connected"] = false; first.State["power_uncertain"] = true;
        await registry.ExecuteAsync("position", Input(b, ownerB, .4), default);
        check(registry.Snapshot(a, "Ubuntu")["power_uncertain"]!.GetValue<bool>() &&
            registry.Snapshot(b, "Ubuntu")["value"]!.GetValue<double>() == .4, "One gripper failure erased evidence or blocked another.");
        await Reject(() => registry.ExecuteAsync("connect", Input(a, ownerA, distribution: "Debian"), default),
            "The same physical Pika acquired a second distribution owner.");
        await registry.ExecuteAsync("disable", Input(a, ownerA), default);
        await registry.ExecuteAsync("disconnect", Input(a, ownerA), default);
        check(!first.State["connected"]!.GetValue<bool>() && second.State["connected"]!.GetValue<bool>() && second.State["enabled"]!.GetValue<bool>(),
            "Disconnecting the first gripper closed or disabled the second.");
        var projected = registry.Snapshots(); projected[0]!["owner"] = "changed-copy";
        check(first.Owner != "changed-copy", "A client modified live owner bookkeeping through its snapshot.");
        inventory = await registry.DiscoverAsync("Ubuntu", default);
        check(inventory["devices"]![1]!["connected"]!.GetValue<bool>() &&
            inventory["devices"]![1]!["owner"]!.GetValue<string>() == ownerB, "Inventory omitted independent owner/connection state.");
        int writes = first.Actions.Count + second.Actions.Count;
        await Reject(() => registry.ExecuteAsync("position", JsonSerializer.SerializeToElement(new { owner = ownerA, pika_device = 123 }), default),
            "Non-string selector accepted.");
        await Reject(() => registry.ExecuteAsync("connect", Input("/dev/pika/by-host/11-4/../../ttyUSB1", ownerA), default),
            "Malformed host USB identity accepted.");
        check(writes == first.Actions.Count + second.Actions.Count, "Invalid routing reached a serial agent.");

        // A by-id alias is usable only when it identifies one device; once
        // bound, refreshing USB inventory cannot redirect it to the other unit.
        string alias = "/dev/serial/by-id/usb-1a86_USB_Serial-if00-port0";
        var singleDiscovery = new Discovery(a);
        var aliasAgents = new Dictionary<string, Session>();
        await using var legacy = new PikaSessionRegistry(alias, "unused", "Ubuntu", discovery: singleDiscovery,
            factory: (path, distro) => aliasAgents[path] = new Session(path, distro));
        await legacy.ExecuteAsync("connect", JsonSerializer.SerializeToElement(new { owner = ownerA }), default);
        check(legacy.Snapshot()["device"]!.GetValue<string>() == a && aliasAgents.ContainsKey(a), "Legacy alias was not bound to verified USB identity.");
        singleDiscovery.Devices = Discovery.Inventory(b, a); // ambiguous symlink now points to B
        await legacy.ExecuteAsync("heartbeat", JsonSerializer.SerializeToElement(new { owner = ownerA }), default);
        check(aliasAgents.Count == 1 && aliasAgents[a].Actions.Last() == "heartbeat", "An existing alias silently changed grippers after USB reordering.");
        await using var ambiguous = new PikaSessionRegistry(alias, "unused", "Ubuntu", discovery: discovery,
            factory: (_, _) => throw new Exception("Ambiguous selection opened an agent."));
        await Reject(() => ambiguous.ExecuteAsync("connect", JsonSerializer.SerializeToElement(new { owner = ownerA }), default),
            "Two identical serial-number-free CH340s accepted an ambiguous by-id assignment.");

        string vhci = "hub port sta spd dev sockfd local_busid\nhs 0000 006 002 000b0004 000003 1-1\nhs 0001 006 002 000f0004 000004 1-2\nhs 0002 004 000 00000000 000000 0-0";
        var mapping = PikaSerialDevices.ParseVhciStatus(vhci);
        check(mapping.Count == 2 && mapping["1-1"] == "11-4" && mapping["1-2"] == "15-4",
            "USBIP remote bus/port identity was confused with a local tty enumeration index.");
        if (OperatingSystem.IsLinux()) VerifySysfsFixture(check, vhci);
        using var usb = JsonDocument.Parse("""
            {"Devices":[{"InstanceId":"USB\\VID_1A86&PID_7522\\FIRST","BusId":"11-4","Description":"USB Serial (COM6)","IsForced":true,"ClientIPAddress":"192.168.1.2"},
              {"InstanceId":"USB\\VID_1A86&PID_7522\\SECOND","BusId":"15-4","Description":"USB Serial (COM7)","IsForced":false,"ClientIPAddress":null},
              {"InstanceId":"USB\\VID_1BCF&PID_2CD1\\CAMERA","BusId":"15-1","IsForced":false}]}
            """);
        var merged = PikaDeviceDiscovery.MergeWindows(usb.RootElement,
            [new(a, "/dev/ttyUSB0", "11-4", [a, alias])], "Ubuntu");
        check(merged["devices"]!.AsArray().Count == 2 && merged["devices"]![0]!["available"]!.GetValue<bool>() &&
            !merged["devices"]![1]!["available"]!.GetValue<bool>() && merged["devices"]![1]!["device"]!.GetValue<string>() == b &&
            merged["devices"]![1]!["message"]!.GetValue<string>().Contains("administrator"),
            "Unshared second Pika was hidden, auto-opened or confused with a camera.");
        var stopAgents = new Dictionary<string, Session>();
        await using var stopRegistry = new PikaSessionRegistry(a, "unused", "Ubuntu", discovery: discovery,
            factory: (device, distro) => stopAgents[device] = new Session(device, distro));
        foreach (string path in new[] { a, b }) {
            await stopRegistry.ExecuteAsync("connect", Input(path, ownerA), default);
            await stopRegistry.ExecuteAsync("enable", Input(path, ownerA), default);
        }
        var held = await stopRegistry.HoldOwnedAsync(ownerA, default);
        check(held["ok"]!.GetValue<bool>() && stopAgents.Values.All(s => s.Actions.Count(x => x == "hold") == 1),
            "Global coordinated hold skipped an owned gripper");
        stopAgents[a].FailHold = true;
        var failedHold = await stopRegistry.HoldOwnedAsync(ownerA, default);
        check(!failedHold["ok"]!.GetValue<bool>() && stopAgents[b].Actions.Count(x => x == "hold") == 2,
            "One failed Pika hold prevented the other independent hold attempt");
        int actionsBefore = stopAgents.Values.Sum(s => s.Actions.Count);
        check((await stopRegistry.HoldOwnedAsync(ownerB, default))["ok"]!.GetValue<bool>() && stopAgents.Values.Sum(s => s.Actions.Count) == actionsBefore,
            "Global hold changed another owner's grippers");
        var uncertain = new Session(a, "Ubuntu") { UnresolvedOwner = ownerA };
        var independent = new Session(b, "Ubuntu") { Owner = ownerA };
        await using var uncertainRegistry = new PikaSessionRegistry(a, "unused", "Ubuntu", discovery: discovery,
            factory: (path, _) => path == a ? uncertain : independent);
        foreach (string path in new[] { a, b })
            await uncertainRegistry.ExecuteAsync("configure", JsonSerializer.SerializeToElement(new { owner = ownerA, pika_device = path, device = path }), default);
        var uncertainStop = await uncertainRegistry.HoldOwnedAsync(ownerA, default);
        var outcomes = uncertainStop["sessions"]!.AsArray().ToDictionary(result => result!["device"]!.GetValue<string>());
        check(uncertainStop["ok"]?.GetValue<bool>() == false && outcomes.Count == 2 &&
            outcomes[a]!["ok"]?.GetValue<bool>() == false && outcomes[b]!["ok"]?.GetValue<bool>() == true &&
            uncertain.Actions.Count == 0 && independent.Actions.SequenceEqual(["hold"]),
            "An unresolved owned gripper was silently omitted, or prevented another gripper from holding");

        var connecting = new Session(a, "Ubuntu")
        {
            ConnectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var defaultRegistry = new PikaSessionRegistry(a, "unused", "Ubuntu", discovery: discovery, factory: (_, _) => connecting);
        var connectingTask = defaultRegistry.ExecuteAsync("connect", JsonSerializer.SerializeToElement(new { owner = ownerA }), default);
        await connecting.ConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var clearing = defaultRegistry.ExecuteAsync("configure", JsonSerializer.SerializeToElement(new { owner = ownerA, device = "" }), default);
        try { check(!clearing.IsCompleted, "Legacy assignment cleared while its connection was in flight"); }
        finally { connecting.PendingConnect.TrySetResult(); }
        await connectingTask;
        await Reject(() => clearing, "Pending clear did not recheck current ownership after connect completed");
        check(defaultRegistry.Snapshot()["device"]?.GetValue<string>() == a, "Rejected concurrent clear changed the default device");
        Console.WriteLine("Pika multi-device tests passed: independent owners/commands/ACKs/evidence, alias binding, USBIP inventory, all-owned holds and failed-channel isolation.");

        async Task Reject(Func<Task> operation, string message)
        { bool rejected = false; try { await operation(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { rejected = true; } check(rejected, message); }
    }

    private static JsonElement Input(string path, string owner, double? value = null, string distribution = "Ubuntu") =>
        JsonSerializer.SerializeToElement(new { owner, pika_device = path, pika_distribution = distribution, value, clear = true });

    private static void VerifySysfsFixture(Action<bool, string> check, string status)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "pika-sysfs-test-" + Guid.NewGuid().ToString("N"));
        string sys = Path.Combine(temporary, "sys"), dev = Path.Combine(temporary, "dev");
        Directory.CreateDirectory(Path.Combine(sys, "class/tty"));
        Directory.CreateDirectory(Path.Combine(dev, "serial/by-id"));
        Directory.CreateDirectory(Path.Combine(dev, "serial/by-path"));
        string controller = Path.Combine(sys, "devices/platform/vhci_hcd.0");
        Directory.CreateDirectory(controller); File.WriteAllText(Path.Combine(controller, "status"), status);
        try
        {
            for (int i = 0; i < 2; i++)
            {
                string usb = Path.Combine(controller, "usb1", "1-" + (i + 1));
                string tty = Path.Combine(usb, $"1-{i + 1}:1.0", "ttyUSB" + i, "tty", "ttyUSB" + i);
                Directory.CreateDirectory(tty);
                File.WriteAllText(Path.Combine(usb, "idVendor"), "1a86\n");
                File.WriteAllText(Path.Combine(usb, "idProduct"), "7522\n");
                Directory.CreateSymbolicLink(Path.Combine(tty, "device"), "../../../ttyUSB" + i);
                Directory.CreateSymbolicLink(Path.Combine(sys, "class/tty", "ttyUSB" + i), tty);
                File.WriteAllText(Path.Combine(dev, "ttyUSB" + i), "fixture, not a serial port");
                File.CreateSymbolicLink(Path.Combine(dev, "serial/by-path", "host-" + i), "../../ttyUSB" + i);
            }
            // Two identical adapters expose just one colliding by-id name.
            File.CreateSymbolicLink(Path.Combine(dev, "serial/by-id", "usb-1a86_USB_Serial-if00-port0"), "../../ttyUSB1");
            var found = PikaSerialDevices.EnumerateLinux(sys, dev).OrderBy(item => item.HostBusId).ToArray();
            check(found.Length == 2 && found[0].Device == "/dev/pika/by-host/11-4" && found[1].Device == "/dev/pika/by-host/15-4" &&
                found[0].SystemDevice.EndsWith("ttyUSB0", StringComparison.Ordinal) && found[1].SystemDevice.EndsWith("ttyUSB1", StringComparison.Ordinal),
                "Real symlink/sysfs enumeration failed to preserve two physical host USB identities.");
            check(!found[0].Aliases.Any(alias => alias.Contains("/by-id/", StringComparison.Ordinal)) &&
                found[1].Aliases.Any(alias => alias.Contains("/by-id/", StringComparison.Ordinal)),
                "Colliding by-id symlink was incorrectly assigned to both grippers.");
        }
        finally { Directory.Delete(temporary, true); }
    }

    private sealed class Discovery(params string[] devices) : IPikaDeviceDiscovery
    {
        public JsonObject Devices = Inventory(devices);
        public static JsonObject Inventory(params string[] paths) => new() { ["ok"] = true,
            ["devices"] = new JsonArray(paths.Select((path, index) => (JsonNode)new JsonObject {
                ["device"] = path, ["distribution"] = "Ubuntu", ["available"] = true,
                ["aliases"] = new JsonArray(index == 0 ? "/dev/serial/by-id/usb-1a86_USB_Serial-if00-port0" : "/dev/ttyUSB1") }).ToArray()) };
        public Task<JsonObject> DiscoverAsync(string? distribution, CancellationToken cancellation) => Task.FromResult(Devices.DeepClone().AsObject());
    }

    private sealed class Session(string path, string? distribution) : IPikaSession
    {
        public string? Owner, UnresolvedOwner;
        public List<string> Actions = [];
        public TaskCompletionSource? PendingHeartbeat;
        public TaskCompletionSource? ConnectEntered, PendingConnect;
        public bool FailHold;
        public JsonObject State = new() { ["device"] = path, ["distribution"] = distribution, ["connected"] = false,
            ["enabled"] = false, ["power_uncertain"] = false, ["fresh"] = true };
        public JsonObject Snapshot(bool includeRawEvidence = false)
        {
            var result = State.DeepClone().AsObject(); result["owner"] = Owner;
            result["configuration_blocked"] = Owner is not null || State["power_uncertain"]?.GetValue<bool>() == true;
            return result;
        }
        public Task<PikaHoldResult> HoldForOwnerAsync(string owner, CancellationToken cancellation)
        {
            if (Owner != owner)
                return Task.FromResult(UnresolvedOwner == owner
                    ? new PikaHoldResult(PikaHoldOutcome.Unverified, "Fixture power is unverified.")
                    : new PikaHoldResult(PikaHoldOutcome.Skipped));
            Actions.Add("hold");
            return FailHold
                ? Task.FromException<PikaHoldResult>(new IOException("Fixture USB hold failure"))
                : Task.FromResult(new PikaHoldResult(PikaHoldOutcome.Acknowledged));
        }
        public async Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation)
        {
            string owner = input.GetProperty("owner").GetString()!;
            if (Owner is not null && Owner != owner) throw new InvalidOperationException("Another owner.");
            Actions.Add(action);
            if (action == "connect")
            {
                ConnectEntered?.TrySetResult();
                if (PendingConnect is { } connecting) await connecting.Task.WaitAsync(cancellation);
                Owner = owner; State["connected"] = true;
            }
            if (action == "heartbeat" && PendingHeartbeat is { } pending) await pending.Task.WaitAsync(cancellation);
            if (action == "enable") State["enabled"] = true;
            if (action == "disable") { State["enabled"] = false; State["power_uncertain"] = false; }
            if (action == "position") State["value"] = input.GetProperty("value").GetDouble();
            if (action == "disconnect") { Owner = null; State["connected"] = false; }
            return new JsonObject { ["ok"] = true };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
