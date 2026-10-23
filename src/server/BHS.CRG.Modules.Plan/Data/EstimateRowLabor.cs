namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Трудозатраты рабочих по позиции сметы (ТЗ PLAN-9.1): чел.-ч на объём строки. Их ждут подсказка
/// монтажнику (<c>WORK-5.1</c>) и «по смете — фактически» (<c>WORK-42</c>).
/// </summary>
public sealed class EstimateRowLabor
{
    private EstimateRowLabor() { }

    public Guid Id { get; private set; }

    public Guid RowId { get; private set; }

    public decimal ManHours { get; private set; }

    internal static EstimateRowLabor Create(Guid rowId, decimal manHours) =>
        new() { Id = Guid.NewGuid(), RowId = rowId, ManHours = manHours };
}
