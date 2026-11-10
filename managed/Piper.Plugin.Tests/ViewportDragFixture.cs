using LuckyRobots.Piper.Plugin;

/// <summary>Puts a viewport drag where a test needs it, through the same
/// transitions the plugin uses.</summary>
internal static class ViewportDragFixture
{
    public static ViewportDrag Teleoperate(this ViewportDragSession viewport, string joint = "joint6")
    {
        viewport.Stop();
        Check(viewport.TrySelectJoint(viewport.Current, joint), "The fixture could not select a viewport joint");
        return viewport.Current;
    }

    public static ViewportDrag Follow(this ViewportDragSession viewport, long request, string joint = "joint6")
    {
        Check(viewport.TryBeginFollowing(viewport.Teleoperate(joint), request, joint), "The fixture could not begin viewport following");
        return viewport.Current;
    }
}
