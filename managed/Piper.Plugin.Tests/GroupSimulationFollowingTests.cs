using System.Diagnostics;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class GroupSimulationFollowingTests
{
    public static void Run(Action<bool, string> check)
    {
        FollowLaneTelemetryTests.Run(check);
        void Reject(Action action, string reason)
        { try { action(); } catch (InvalidOperationException) { check(true, reason); return; } check(false, reason); }
        JsonObject Frame() => new() { ["cursor"] = "1", ["simulationTimeNs"] = "0", ["mujocoTimeSeconds"] = 0d,
            ["episodeId"] = "episode-a", ["episodeOrdinal"] = 0L, ["phase"] = "arm:1", ["episodeComplete"] = false, ["episodeSucceeded"] = false,
            ["tracks"] = new JsonArray(new JsonObject { ["id"] = "first", ["commandJoints"] = new JsonArray(0d,0d,0d,0d,0d,0d),
                ["measuredJoints"] = new JsonArray(0d,0d,0d,0d,0d,0d), ["commandOpeningMm"] = 20d, ["measuredOpeningMm"] = 20d },
                new JsonObject { ["id"] = "second", ["commandJoints"] = new JsonArray(.1,0d,0d,0d,0d,0d),
                ["measuredJoints"] = new JsonArray(.1,0d,0d,0d,0d,0d), ["commandOpeningMm"] = 40d, ["measuredOpeningMm"] = 40d }) };
        var response = new JsonObject { ["frames"] = new JsonArray(Frame()) };
        var tracks = GroupSimulationFrames.Read(response, ["first", "second"]);
        check(tracks["first"][0].CommandDegrees[0] == 0 && Math.Abs(tracks["second"][0].CommandDegrees[0] - .1 * 180 / Math.PI) < 1e-9 &&
            tracks["first"][0].CommandOpeningMm == 20 && tracks["second"][0].CommandOpeningMm == 40 &&
            tracks["first"][0].Cursor == tracks["second"][0].Cursor && tracks["first"][0].SceneTimeNs == tracks["second"][0].SceneTimeNs,
            "Atomic track routing mixed arms, grippers or shared source clocks");
        foreach (string failure in new[] { "missing", "duplicate", "unknown", "nonfinite" })
        {
            var broken = Frame(); var rows = broken["tracks"]!.AsArray();
            if (failure == "missing") rows.RemoveAt(1);
            if (failure == "duplicate") rows[1]!["id"] = "first";
            if (failure == "unknown") rows[1]!["id"] = "third";
            if (failure == "nonfinite") rows[1]!["commandOpeningMm"] = "NaN";
            Reject(() => GroupSimulationFrames.Read(new JsonObject { ["frames"] = new JsonArray(broken) }, ["first", "second"]), "An invalid atomic group admitted a partial track set: " + failure);
        }

        var initial = SimulationFollowSample.Read(SimulationFollowTests.Pose());
        var limits = Enumerable.Range(1,6).Select(j => new EpisodeReplayJointLimit(j,-180,180,30,20,true)).ToArray();
        SimulationFollowHardware Hardware(string id, double[] pose, long sequence = 0) => new(id,7,true,true,true,pose,limits,20,new(id,7,77,sequence,pose));
        OrderedSimulationFrame Node(ulong cursor, double time, string phase, double joint) => new(cursor,(long)(time*1e9),time,
            new double[] {joint,0,0,0,0,0},new double[6],20,20,"episode-a",0,phase,false,false);
        var first = new OrderedSimulationExecution(initial,Hardware("can-a",new double[6]),1,true);
        var second = new OrderedSimulationExecution(initial,Hardware("can-b",new double[6]),1,true);
        first.Path.Append([Node(1,0,"arm:1",0),Node(2,.02,"arm:1",0),Node(3,.04,"arm:2",2),Node(4,.06,"wait:3",2)]);
        second.Path.Append([Node(1,0,"arm:1",0),Node(2,.02,"arm:1",0),Node(3,.04,"arm:2",3),Node(4,.06,"wait:3",3)]);
        check(first.Path.Current!.End.Cursor == 2 && second.Path.Current!.End.Cursor == 2,
            "Adjacent arm phases were merged across a relay handoff boundary");
        var barrier = new GroupSimulationPhaseBarrier(); barrier.Observe([first,second]);
        first.Step(0,Hardware("can-a",new double[6]),true,0,1,barrier.MayStart(first));
        second.Step(0,Hardware("can-b",new double[6]),false,0,1,barrier.MayStart(second));
        first.Step(.1,Hardware("can-a",new double[6]),true,0,2,barrier.MayStart(first));
        first.Step(.21,Hardware("can-a",new double[6]),true,0,3,barrier.MayStart(first));
        barrier.Observe([first,second]);
        check(first.CompletedCursor == 2 && !barrier.MayStart(first) && barrier.MayStart(second), "A faster partner was permitted to start the next relay phase alone");
        var held = first.Step(.3,Hardware("can-a",new double[6]),true,0,4,barrier.MayStart(first));
        check(held.JointDegrees.All(q=>q==0) && first.ActivePhase is null, "A completed partner moved instead of holding at the shared barrier");
        second.Step(.3,Hardware("can-b",new double[6]),true,0,4,barrier.MayStart(second));
        second.Step(.41,Hardware("can-b",new double[6]),true,0,5,barrier.MayStart(second));
        barrier.Observe([first,second]);
        check(barrier.CompletedCursor == 2 && barrier.MayStart(first) && barrier.MayStart(second), "All measured arrivals did not release the next phase together");
    }
}
