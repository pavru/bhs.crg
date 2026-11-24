namespace BHS.CRG.Modules.Work.Summary;

public static class SummaryStates
{
    public const string Report = "report";
    public const string ZeroHours = "zero-hours";
    public const string Draft = "draft";
    public const string NotReceived = "not-received";
    public const string NotAssigned = "not-assigned";
    public const string NotEmployed = "not-employed";
    public const string WindowOpen = "window-open";
    public const string Future = "future";

    public static IReadOnlyList<string> All { get; } =
        Array.AsReadOnly(new[] { Report, ZeroHours, Draft, NotReceived, NotAssigned, NotEmployed, WindowOpen, Future });
}
