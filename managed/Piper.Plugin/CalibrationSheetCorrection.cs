using System.Text.Json;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    internal int sheetEditingRow = -1;
    private bool SheetCanCorrect => calibrationIdentity is not null && link is not null && calibrationIdentity == CurrentCalibrationIdentity() &&
        calibrationApplied is null && !calibrationPlacementUnverified && SheetContactCompatible;

    private async Task EditSheetMeasurement(int row, CancellationToken cancellation)
    {
        if (!SheetCanCorrect) throw new InvalidOperationException("Undo an applied alignment and verify the original connection before editing captured measurements.");
        if (sheetEditingRow >= 0)
        {
            var previous = SheetSample(sheetEditingRow)!;
            Wizard.Answers[$"p{sheetEditingRow}left"] = CalibrationNumber(previous.Measurement.LeftMm);
            Wizard.Answers[$"p{sheetEditingRow}back"] = CalibrationNumber(previous.Measurement.BackMm);
        }
        sheetEditingRow = sheetEditingRow == row ? -1 : row;
        if (sheetEditingRow >= 0)
        {
            var current = SheetSample(sheetEditingRow)!;
            Wizard.Answers[$"p{sheetEditingRow}left"] = CalibrationNumber(current.Measurement.LeftMm);
            Wizard.Answers[$"p{sheetEditingRow}back"] = CalibrationNumber(current.Measurement.BackMm);
        }
        sheetMarkersDirty = true;
        wizardMessage = sheetEditingRow < 0 ? "Edit cancelled. The captured measurements are unchanged."
            : $"Correct {SheetPointName(row)}'s BACK / LEFT values, then Save entry. Its recorded joint pose and contact point are preserved.";
        await SaveWizard(cancellation);
    }

    private (CalibrationSample? Correction, string Blocker) ValidateSheetCorrection(int row, CalibrationSample sample)
    {
        try
        {
            var corrected = sample.Measurement with
            {
                LeftMm = CalibrationWizardFlow.Number(Wizard, $"p{row}left", 0, calibrationDesk.Width * 1000),
                BackMm = CalibrationWizardFlow.Number(Wizard, $"p{row}back", 0, calibrationDesk.Depth * 1000)
            };
            var measured = corrected.World(calibrationDesk);
            int referenceRow = 0;
            foreach (var other in calibrationSamples)
            {
                int otherRow = other.HeldOut ? 4 : referenceRow++;
                if (ReferenceEquals(other, sample)) continue;
                double spacing = sample.HeldOut || other.HeldOut ? CheckSpacingMetres : CalibrationSolver.ReferenceDuplicateDistanceMetres;
                if (measured.PlanarDistance(other.MeasuredWorld) >= spacing) continue;
                if (sample.HeldOut) return (null, "Use a check cross at least 50 mm from every reference point.");
                if (other.HeldOut) return (null, "Keep each reference point at least 50 mm from the captured check cross.");
                string name = otherRow < 4 ? SheetPointName(otherRow) : other.Name;
                return (null, $"This overlaps {name} (within 1 mm). Use a distinct physical cross.");
            }

            string blocker = sample.HeldOut ? "" : SheetReservedCrossBlocker(corrected.LeftMm, corrected.BackMm);
            return blocker.Length == 0 ? (sample with { Measurement = corrected, MeasuredWorld = measured }, "") : (null, blocker);
        }
        catch (InvalidOperationException)
        {
            return (null, "Enter BACK and LEFT distances for this cross, within the desk edges (mm).");
        }
    }

    private async Task SaveSheetMeasurementCorrection(int row, CancellationToken cancellation)
    {
        if (sheetEditingRow != row || !SheetCanCorrect) throw new InvalidOperationException("Click Edit entry beside this captured point first. Applied or unverified alignments cannot be edited.");
        var sample = SheetSample(row) ?? throw new InvalidOperationException("This point has not been captured.");
        var (correction, blocker) = ValidateSheetCorrection(row, sample);
        if (correction is null) throw new InvalidOperationException(blocker);
        if (sample.Measurement == correction.Measurement) { sheetEditingRow = -1; await SaveWizard(cancellation); return; }
        // Keep the original report and exact captured evidence before replacing
        // entered ruler coordinates. Audit files are never auto-loaded as reports.
        string audit = Path.Combine(RobotCalibrationDirectory, "measurement-corrections");
        Directory.CreateDirectory(audit);
        string receipt = Path.Combine(audit, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            reason = "Operator corrected entered desk measurements; captured pose unchanged", row, originalSample = sample,
            correctedMeasurement = correction.Measurement, previousReport = JsonDocument.Parse(await File.ReadAllTextAsync(calibrationPath, cancellation)).RootElement.Clone()
        }, LuckyEngine.PluginSdk.PluginProtocol.Json), cancellation);
        int index = calibrationSamples.IndexOf(sample);
        var oldFit = calibrationFit;
        calibrationSamples[index] = correction;
        calibrationFit = null;
        try { await SaveCalibration(cancellation); }
        catch { calibrationSamples[index] = sample; calibrationFit = oldFit; throw; }
        sheetEditingRow = -1; sheetMarkersDirty = true;
        wizardMessage = $"{SheetPointName(row)} measurements corrected and saved. Its captured arm pose is unchanged; the original report is retained. Calculate alignment again.";
        await SaveWizard(cancellation);
    }
}
