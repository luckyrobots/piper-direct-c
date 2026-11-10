using System.Text.Json;
using LuckyRobots.Piper.Host;

/// <summary>Which grippers a coordinated STOP holds, for every shape of request (B5).
/// No request is unclassifiable, and only a cancel that names an arm but no gripper is refused.</summary>
internal static class CoordinatedStopScopeTests
{
    internal static void Run()
    {
        Expect("{}", GripperStopLeg.HoldAll, degraded: false);
        Expect("""{"owner":"page"}""", GripperStopLeg.HoldAll, degraded: false);
        Expect("""{"pika_device":"/dev/ttyUSB0"}""", GripperStopLeg.HoldAll, degraded: false);
        Expect("""{"device_id":"usb:A","pika_device":"/dev/ttyUSB0"}""", GripperStopLeg.HoldNamed, degraded: false);
        Expect("""{"device_id":"usb:A","request_id":4,"pika_device":"/dev/ttyUSB0"}""", GripperStopLeg.HoldNamed, degraded: false);
        Expect("""{"device_id":"usb:A","request_id":4}""", GripperStopLeg.RefuseCancel, degraded: false);
        Expect("""{"device_id":"usb:A","request_id":4,"pika_device":" "}""", GripperStopLeg.RefuseCancel, degraded: false);
        // An emergency whose scope cannot be honoured still holds every gripper and says why.
        Expect("""{"device_id":"usb:A"}""", GripperStopLeg.HoldAll, degraded: true);
        Expect("""{"device_id":7,"request_id":4}""", GripperStopLeg.HoldAll, degraded: true);
        Expect("""{"device_id":"","request_id":4}""", GripperStopLeg.HoldAll, degraded: true);
        Expect("""{"request_id":4}""", GripperStopLeg.HoldAll, degraded: true);
        Expect("""{"generation":2}""", GripperStopLeg.HoldAll, degraded: true);
    }

    private static void Expect(string request, GripperStopLeg leg, bool degraded)
    {
        using var input = JsonDocument.Parse(request);
        var scope = CoordinatedStopScope.Of(input.RootElement);
        Check(scope.Leg == leg && scope.Degraded is not null == degraded, $"Coordinated STOP {request} was scoped as {scope}");
    }
}
