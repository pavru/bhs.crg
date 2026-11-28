using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed class FieldReportRefusal(int status, string reason) : Exception(reason)
{
    public int Status { get; } = status;
    public string Reason { get; } = reason;
}

internal sealed class FieldReportDelivery(DeviceSessions sessions, Guid deviceId, string token)
{
    private FieldDeviceAccess _access = null!;
    private DateTimeOffset _checkedAt;

    // В той же транзакции, что отчёт. Если отзыв уже получил замок, используем обновлённые ограничения сессии.
    internal async Task LockAsync(CancellationToken ct)
    {
        await sessions.LockSessionAsync(deviceId, ct);
        (_access, _checkedAt) = await sessions.CheckDeliveryAsync(token, deviceId, ct);
    }

    internal void EnsureDate(Guid employeeId, DateOnly date)
    {
        if (!_access.AllowsReport(employeeId, date, delivering: true, _checkedAt))
            throw new FieldReportRefusal(403, "device-revoked");
    }

    internal void EnsureOwnReportEdit(Report report, bool confirmed)
    {
        if (report.EnteredByUserId is not null && !confirmed)
            throw new FieldReportRefusal(409, "manager-report-conflict");
    }
}
