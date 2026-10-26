using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Действия над накладной в журнале (задача D1 этапа 2, issue #1083; ТЗ CORE-25, CORE-28).
///
/// <para>Каждое закрыто правом чтения накладных: запись называет стройку и номер документа, а журнал
/// читают по праву ядра (см. <see cref="ModuleActivityAction.ReadPermission" />). Правом на счета оно
/// не открывается и счетов не открывает — у накладных своё право (COST-29).</para>
/// </summary>
public sealed class WaybillActions : IModuleActivityActions
{
    private const string WaybillRead = "costs.waybill.read";

    public static readonly ModuleActivityAction Created =
        new("costs.waybill.created", "Накладная заведена", WaybillRead);

    public static readonly ModuleActivityAction Changed =
        new("costs.waybill.changed", "Накладная изменена", WaybillRead);

    public static readonly ModuleActivityAction LinesChanged =
        new("costs.waybill.lines", "Строки накладной изменены", WaybillRead);

    /// <summary>Сопоставление строки проведённой накладной — отдельно от правки строк: документ при
    /// этом не распроводят, и в журнале должно быть видно, что менялась только ссылка на позицию.</summary>
    public static readonly ModuleActivityAction Matched =
        new("costs.waybill.matched", "Строка накладной сопоставлена", WaybillRead);

    public static readonly ModuleActivityAction Posted =
        new("costs.waybill.posted", "Накладная проведена", WaybillRead);

    public static readonly ModuleActivityAction Draft =
        new("costs.waybill.draft", "Накладная возвращена в черновик", WaybillRead);

    /// <summary>Удаление черновика (issue #1283). История удалённой накладной находится по тому же
    /// <c>targetId</c>: запись называет номер, дату, склад, получателя и число строк.</summary>
    public static readonly ModuleActivityAction Deleted =
        new("costs.waybill.deleted", "Накладная удалена", WaybillRead);

    /// <summary>Исправление накладной закрытого периода (issue #1340): «было» — номер ушедшей версии,
    /// «стало» — причина. Прежнее содержимое читается адресом версий.</summary>
    public static readonly ModuleActivityAction Corrected =
        new("costs.waybill.corrected", "Накладная исправлена после закрытия периода", WaybillRead);

    public IReadOnlyList<ModuleActivityAction> Actions =>
        [Created, Changed, LinesChanged, Matched, Posted, Draft, Deleted, Corrected];
}
