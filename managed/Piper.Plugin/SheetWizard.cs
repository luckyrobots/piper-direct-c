namespace LuckyRobots.Piper.Plugin;

/// <summary>Everything the calibration sheet remembers for one robot profile,
/// as one value. <see cref="Draft"/> is null until the profile's draft is opened.</summary>
internal sealed record WizardSession(string Profile, CalibrationWizardDraft? Draft, string Message, string ModelPoint, string ModelPointDetails,
    string[] Sites, int EditingRow, string SaveStatus)
{
    public const string ModelPointPrompt = "Show the point, then compare its marker with the real jaw tips.";

    public static readonly WizardSession Closed = new("", null, "", ModelPointPrompt,
        "Model coordinates and site identity are not a verified physical TCP calibration.", [], -1, "Measurements save automatically as you type.");
}

/// <summary>The Cal-B part of the Calibration component: the sheet's draft
/// (typed-in measurements, setup confirmations, step and epoch), the row being
/// corrected, and the texts the sheet shows.
/// <code>
///   field    written by                                            readers see
///   session  the UI actor (every transition below), the render     one volatile reference to an
///            path (Open and Answer while loading a profile's       immutable WizardSession. Its
///            draft) and marker maintenance (Report, Answer,        draft's answers and its site
///            Edit, ChangeSetup). Each write is a compare-and-swap  list are never changed once
///            from the value its writer read.                       published: a change publishes
///                                                                  a new draft with a new map
/// </code>
/// A reader that needs several fields to agree reads <see cref="Session"/> once.
/// A transition that changes nothing publishes nothing.
///
/// Not owned here: the draft file (the workflow reads and writes it), the desk
/// session the draft's captures belong to (Cal-A), and the markers a setup
/// confirmation refers to (Cal-C).</summary>
internal sealed class SheetWizard
{
    private WizardSession session = WizardSession.Closed;

    public WizardSession Session => Volatile.Read(ref session);
    /// <summary>The open draft. Throws when none is open.</summary>
    public CalibrationWizardDraft Draft => Session.Draft ?? throw new InvalidOperationException("Open the calibration wizard first.");

    private void Change(Func<WizardSession, WizardSession> change)
    {
        WizardSession current;
        do current = Session;
        while (!ReferenceEquals(Interlocked.CompareExchange(ref session, change(current), current), current));
    }

    /// <summary>A profile's draft is opened: no message, no discovered sites, no row being corrected.</summary>
    public void Open(string profile, CalibrationWizardDraft draft) => Change(current => current with
        { Profile = profile, Draft = draft, Message = "", Sites = [], ModelPoint = WizardSession.ModelPointPrompt, EditingRow = -1 });

    /// <summary>Changes the open draft. Throws when none is open.</summary>
    public void Edit(Func<CalibrationWizardDraft, CalibrationWizardDraft> change) => Change(current =>
    {
        var draft = current.Draft ?? throw new InvalidOperationException("Open the calibration wizard first.");
        var next = change(draft);
        return ReferenceEquals(next, draft) ? current : current with { Draft = next };
    });

    public void Answer(string key, string value) => Answer((key, value));

    /// <summary>Records typed-in or derived answers together, in a new map.</summary>
    public void Answer(params (string Key, string Value)[] entries) => Edit(draft =>
    {
        if (entries.All(entry => draft.Answers.TryGetValue(entry.Key, out string? held) && held == entry.Value)) return draft;
        var answers = new Dictionary<string, string>(draft.Answers, StringComparer.Ordinal);
        foreach (var (key, value) in entries) answers[key] = value;
        return draft with { Answers = answers };
    });

    /// <summary>Changes the setup confirmations.</summary>
    public void ChangeSetup(Func<SheetSetup, SheetSetup> change) => Edit(draft => draft with { Setup = change(draft.Setup) });

    /// <summary>The line of feedback under the sheet's actions.</summary>
    public void Report(string message) => Change(current => current with { Message = message });

    /// <summary>The named sites the current model exposes.</summary>
    public void Discovered(string[] sites) => Change(current => current with { Sites = sites });

    public void DescribeModelPoint(string point, string details) => Change(current => current with { ModelPoint = point, ModelPointDetails = details });

    /// <summary>Opens a captured row's ruler entries for correction.</summary>
    public void EditRow(int row) => Change(current => current with { EditingRow = row });
    public void StopEditing() => Change(current => current with { EditingRow = -1 });

    public void ReportSave(string status) => Change(current => current with { SaveStatus = status });
}
