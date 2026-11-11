namespace LuckyRobots.Piper.Plugin;

/// <summary>The Shell component (P5): what the plugin itself remembers, apart
/// from the robot, the workspace and the calibration. It is made of parts; each
/// part owns its fields, is their only writer, and carries its own field table.
/// <code>
///   part     machine                          table on
///   Backend  Plugin-4 owned backend, restart  OwnedBackend
/// </code>
/// <b>Relation to <see cref="RobotCommandAuthority"/>.</b> The authority stays
/// the only owner of permission epochs. Nothing here grants motion, and no
/// transition here runs inside the authority's epoch gate.
///
/// Not owned here: the backend workflows and the backend page (they stay on
/// <c>PipersPlugin</c>).</summary>
internal sealed class ShellComponent
{
    public OwnedBackend Backend { get; } = new();
}
