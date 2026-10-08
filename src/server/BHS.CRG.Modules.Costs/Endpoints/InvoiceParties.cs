using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Организация справочника, чей ИНН совпал с прочитанным в скане.</summary>
/// <param name="Type">Код типа записи — у подтипа свой.</param>
/// <param name="Archived">Запись в архиве: совпала, но в новый счёт не выбирается.</param>
/// <param name="InheritedFrom">Запись, от которой ИНН унаследован. По нему видно, что несколько
/// совпадений — одна организация и её роли, а не дубли.</param>
public sealed record InvoicePartyCandidate(Guid Id, string? Name, string Type, bool Archived, Guid? InheritedFrom);

/// <summary>
/// Сторона счёта, как она прочитана в скане, и что о ней говорит справочник (issue #1077).
/// </summary>
/// <param name="State">
/// <c>matched</c> — одна действующая организация с таким ИНН;
/// <c>several</c> — действующих несколько, выбирает человек;
/// <c>archived</c> — совпали только архивные;
/// <c>absent</c> — в справочнике такой нет, и прочитаны ВСЕ записи;
/// <c>unknown</c> — совпадений нет, но часть записей прочитать не удалось: «нет» утверждать нельзя;
/// <c>noTaxId</c> — название в скане есть, ИНН нет — сопоставлять не по чему;
/// <c>badTaxId</c> — ИНН прочитан с ошибкой, поиска не было;
/// <c>unavailable</c> — сопоставить нечем: нет типа организаций или поля ИНН в его схеме.
/// </param>
/// <param name="Name">Название из скана, как прочитано.</param>
/// <param name="TaxId">ИНН: проверенный — цифрами, непрочитанный — текстом из скана.</param>
/// <param name="Why">Почему состояние такое — словами для человека; у <c>matched</c> пусто.</param>
/// <param name="Unreadable">Сколько записей справочника прочитать не удалось.</param>
public sealed record InvoicePartyView(
    string State, string? Name, string? TaxId, string? Why,
    IReadOnlyList<InvoicePartyCandidate> Candidates, int Unreadable);

/// <summary>Обе стороны; <c>null</c> — про сторону в скане не прочитано ничего.</summary>
public sealed record InvoicePartiesView(InvoicePartyView? Supplier, InvoicePartyView? Payer);

/// <summary>
/// Сопоставление поставщика и плательщика распознанного счёта с организациями справочника — по ИНН
/// (ТЗ COST-8, задача B1b, issue #1077).
///
/// <para><b>Ничего не хранит.</b> Ответ считается из прочитанного в скане каждый раз заново: справочник
/// меняется — организацию заводят, отправляют в архив, — и сохранённое «такой нет» устарело бы молча.
/// В счёт отсюда ничего не пишется; единственное совпадение кладёт в пустое поле фоновая задача
/// (<see cref="InvoiceScanReading" />), один раз.</para>
///
/// <para>⚠️ Сторож: «в справочнике нет» — самый дорогой ответ, по нему человек заведёт организацию.
/// Он даётся, только когда ИНН прочитан верно (контрольная сумма) и прочитаны все записи. Всё, что
/// мешает это утверждать, имеет своё состояние и свою причину.</para>
///
/// <para>По названию не сопоставляем: «ООО "Ромашка"» в справочнике может стоять как «Ромашка, ООО»
/// или «РОМАШКА», и совпадение по похожести подставило бы в счёт не ту организацию.</para>
/// </summary>
public sealed class InvoiceParties(IModuleCatalog catalog)
{
    /// <summary>Ключ поля ИНН в типе «Организация». Тип ведёт человек — переименование даёт
    /// состояние <c>unavailable</c> с названием поля, а не «организаций нет».</summary>
    public const string TaxIdField = "ИНН";

    public async Task<InvoicePartiesView> MatchAsync(IReadOnlyDictionary<string, string?> read, CancellationToken ct)
    {
        var supplier = Side(read, CostsRecognitionProfiles.Supplier, CostsRecognitionProfiles.SupplierTaxId);
        var payer = Side(read, CostsRecognitionProfiles.Payer, CostsRecognitionProfiles.PayerTaxId);

        // В справочник идём, только если есть что искать: вид открывают поллингом.
        if (supplier?.TaxId is null && payer?.TaxId is null)
            return new(supplier?.Refusal, payer?.Refusal);

        var answer = await catalog.FieldValuesAsync(CostsRecordTypes.OrganizationCode, TaxIdField, RecordsFor.Display, ct);
        return new(Judge(supplier, answer), Judge(payer, answer));
    }

    /// <summary>Что прочитано о стороне: проверенный ИНН либо готовый ответ, почему искать нечем.</summary>
    private sealed record Read(string? Name, string? TaxId, InvoicePartyView? Refusal);

    private static Read? Side(IReadOnlyDictionary<string, string?> read, string nameKey, string taxKey)
    {
        var name = Text(read, nameKey);
        var raw = Text(read, taxKey);
        if (name is null && raw is null) return null;

        var taxId = Data.TaxId.FromScan(raw, out var problem);
        if (taxId is not null) return new(name, taxId, null);

        return problem is null
            ? new(name, null, new("noTaxId", name, null,
                "ИНН в скане не прочитан, а по названию организации не сопоставляются — выберите её из справочника.", [], 0))
            : new(name, null, new("badTaxId", name, raw,
                $"ИНН {problem}. Сверьте его со сканом и выберите организацию из справочника.", [], 0));
    }

    private static InvoicePartyView? Judge(Read? side, ModuleCatalogFieldValues? answer)
    {
        if (side is null) return null;
        if (side.TaxId is not { } taxId) return side.Refusal;

        if (answer is null)
            return new("unavailable", side.Name, taxId,
                $"Тип «{CostsRecordTypes.OrganizationCode}» в системе не заведён — сопоставить не с чем.", [], 0);
        if (!answer.Declared)
            return new("unavailable", side.Name, taxId,
                $"В типе «{CostsRecordTypes.OrganizationCode}» нет простого поля «{TaxIdField}» — сопоставить не по чему. " +
                "Поле могли переименовать или сделать составным; организацию выберите из справочника.", [], 0);

        var unreadable = answer.Records.Count(r => r.Unreadable);
        var found = answer.Records
            .Where(r => !r.Unreadable && Data.TaxId.FromRecord(r.Value) == taxId)
            .Select(r => new InvoicePartyCandidate(
                r.Record.Id, r.Record.DisplayName, r.Record.EntityType, r.Record.Archived, r.InheritedFrom))
            .ToList();
        var live = found.Count(c => !c.Archived);

        return live switch
        {
            1 => new("matched", side.Name, taxId, null, found, unreadable),
            > 1 => new("several", side.Name, taxId,
                $"С ИНН {taxId} в справочнике несколько действующих записей — выберите нужную.", found, unreadable),
            _ when found.Count > 0 => new("archived", side.Name, taxId,
                $"Организация с ИНН {taxId} есть в справочнике, но в архиве. В новый счёт архивную не выбирают — " +
                "верните её из архива или выберите другую.", found, unreadable),
            _ when unreadable > 0 => new("unknown", side.Name, taxId,
                $"Организация с ИНН {taxId} среди прочитанных записей не найдена, но часть записей справочника " +
                $"прочитать не удалось (их {unreadable}): реквизиты они наследуют от записи, которой нет или " +
                "которая лежит в другой области. Сказать «такой организации нет» нельзя — проверьте справочник.",
                [], unreadable),
            _ => new("absent", side.Name, taxId, $"Организации с ИНН {taxId} в справочнике нет.", [], 0),
        };
    }

    private static string? Text(IReadOnlyDictionary<string, string?> read, string key) =>
        read.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
