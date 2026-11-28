using System.Globalization;

namespace BHS.CRG.Modules.Work.Data;

public sealed partial class ReportReads
{
    internal static IReadOnlyList<ReviewMarkDto> DeliveryMarksOf(IReadOnlyList<ReportDeliveryEntry> entries)
    {
        var texts = new List<ReviewMarkDto>();
        var submission = entries.LastOrDefault(e => e.Kind is
            ReportJournalKinds.Submitted or ReportJournalKinds.AutoSubmitted or ReportJournalKinds.FieldReceipt);
        if (submission is { DeviceTime: { } submitted }
            && submitted <= submission.WindowEndsAt && submission.ServerTime > submission.WindowEndsAt)
            texts.Add(Mark(ReviewMarks.LateDelivery, submission, submission.ServerTime - submission.WindowEndsAt));

        ReportDeliveryEntry? previous = null;
        ReviewMarkDto? difference = null;
        ReviewMarkDto? back = null;
        foreach (var entry in entries)
        {
            if (entry.DeviceTime is not { } device) continue;
            var delta = (entry.ServerTime - device).Duration();
            if (delta > TimeSpan.FromMinutes(ReviewMarks.ClockToleranceMinutes))
                difference = Mark(ReviewMarks.ClockDifference, entry, delta);
            if (previous?.DeviceTime is { } prior && device < prior)
                back = Mark(ReviewMarks.ClockWentBack, entry, prior - device);
            previous = entry;
        }
        if (difference is not null) texts.Add(difference);
        if (back is not null) texts.Add(back);
        return texts;
    }

    private static ReviewMarkDto Mark(string code, ReportDeliveryEntry entry, TimeSpan delta)
    {
        var label = ReviewMarks.Of(code)!.Text;
        var minutes = Math.Ceiling(delta.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        var device = entry.DeviceTime!.Value.UtcDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        var server = entry.ServerTime.UtcDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        return new ReviewMarkDto(code, $"{label} ({minutes} мин) · действие {device} UTC · приём {server} UTC");
    }
}
