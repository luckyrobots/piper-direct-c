namespace LuckyRobots.Piper.Plugin;

/// <summary>The Calibration component (P5): everything the calibration pages
/// remember between two events. It is made of parts, one per machine in the
/// state-machine spec; each part owns its fields, is their only writer, and
/// carries its own field table. Nothing outside a part can assign its fields.
/// Every reader sees one immutable value through one volatile reference and
/// takes no lock.
/// <code>
///   part     machine                          table on
///   Desk     Cal-A desk calibration session   DeskCalibration
///   Wizard   Cal-B calibration sheet          SheetWizard
///   Height   Cal-D jaw-height check           HeightCheckSession
///   Markers  Cal-C marker ownership           CalibrationMarkers
///   Lens     Cal-E lens calibration           LensCalibration
///   Cameras  Cal-F camera discovery           CameraDiscovery
///   Capture  Cal-G camera workers             CaptureWorkers
/// </code>
/// <b>Relation to <see cref="RobotCommandAuthority"/>.</b> The authority stays
/// the only owner of permission epochs. No calibration state grants motion and
/// no transition here runs inside the authority's epoch gate. The STOP path
/// neither reads nor writes this component, and no part holds a lock a STOP
/// could wait on.
///
/// Not owned here: the workflows and views (they stay on <c>PipersPlugin</c>),
/// the files a workflow reads and writes, the engine's markers and lines, and
/// the robot link the calibration is measured against.</summary>
internal sealed class CalibrationComponent
{
    public DeskCalibration Desk { get; } = new();
    public SheetWizard Wizard { get; } = new();
    public HeightCheckSession Height { get; } = new();
    public CalibrationMarkers Markers { get; } = new();
    public LensCalibration Lens { get; } = new();
    public CameraDiscovery Cameras { get; } = new();
    public CaptureWorkers Capture { get; } = new();
}
