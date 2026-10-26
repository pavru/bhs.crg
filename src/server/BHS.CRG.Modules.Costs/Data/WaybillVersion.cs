using System.Text.Json;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Версия накладной, ушедшая под исправление (issue #1340, ТЗ COST-16, CORE-36): шапка и строки такими,
/// какими они были ДО исправления, с причиной, автором и временем.
///
/// <para><b>Номер — номер ушедшей версии</b>, как у счёта (<see cref="InvoiceVersion" />): первое
/// исправление пишет версию 1, второе — версию 2. Номер действующей хранит накладная
/// (<see cref="Waybill.CurrentVersion" />), и следующий номер берётся у неё, а не максимумом по истории.</para>
///
/// <para>Книги проводок у накладной нет — денег нет (<see cref="Waybill" />). Версия — единственный след
/// исправления, кроме журнала.</para>
/// </summary>
public sealed class WaybillVersion : ModuleRecordVersion
{
    /// <summary>Для EF.</summary>
    private WaybillVersion() { }

    private WaybillVersion(Guid waybillId, int number, string content, string reason, Guid? author)
        : base(waybillId, number, content, reason, author) { }

    public static WaybillVersion Create(Guid waybillId, int number, string content, string reason, Guid? author) =>
        new(waybillId, number, content, reason, author);

    /// <summary>
    /// Содержимое накладной документом JSON — тем видом, каким его читает форма: шапка и строки. Имя
    /// автора — на момент записи, как у книги счёта: учётную запись могут переименовать.
    /// </summary>
    public static string Snapshot(Waybill waybill, IReadOnlyList<WaybillLine> lines, string author) =>
        JsonSerializer.Serialize(new
        {
            number = waybill.Number,
            issuedOn = waybill.IssuedOn,
            warehouse = waybill.Warehouse,
            constructionId = waybill.ConstructionId,
            receivedBy = waybill.ReceivedBy,
            note = waybill.Note,
            accountingOn = waybill.AccountingOn,
            authorName = author,
            lines = lines.OrderBy(l => l.Ordinal).Select(l => new
            {
                id = l.Id, ordinal = l.Ordinal, nomenclatureId = l.NomenclatureId, sourceText = l.SourceText,
                unit = l.Unit, quantity = l.Quantity, note = l.Note,
            }),
        });

    internal static void Map(ModelBuilder builder) =>
        // RESTRICT, как у счёта: накладная с историей не удаляется, каскад снёс бы историю молча. Удаление
        // черновика отказывает раньше ключа — по номеру действующей версии (WaybillDeletion).
        Map<WaybillVersion>(builder, "waybill_versions")
            .HasOne<Waybill>().WithMany().HasForeignKey(v => v.RecordId).OnDelete(DeleteBehavior.Restrict);
}
