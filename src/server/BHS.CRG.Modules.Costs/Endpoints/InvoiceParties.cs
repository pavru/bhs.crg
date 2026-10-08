using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Организация справочника — совпавшая по ИНН либо та, чей ИНН прочитать не удалось.</summary>
/// <param name="Type">Код типа записи — у подтипа свой.</param>
/// <param name="Archived">Запись в архиве: совпала, но в новый счёт не выбирается.</param>
/// <param name="InheritedFrom">Запись, от которой ИНН унаследован. По нему видно, что несколько
/// совпадений — одна организация и её роли, а не дубли.</param>
public sealed record InvoicePartyCandidate(Guid Id, string? Name, string Type, bool Archived, Guid? InheritedFrom);

/// <summary>
/// Состояния стороны — договор с клиентом. Одно место: то же слово читает форма, и опечатка в нём
/// не дала бы ошибки компиляции — состояние просто перестало бы узнаваться.
/// </summary>
public static class InvoicePartyStates
{
    /// <summary>Организация найдена и названа в <see cref="InvoicePartyView.Match" />: единственная
    /// действующая с таким ИНН либо организация среди своих ролей.</summary>
    public const string Matched = "matched";

    /// <summary>Действующих несколько, и это разные записи — выбирает человек.</summary>
    public const string Several = "several";

    /// <summary>Организация в архиве; действующих записей с таким ИНН нет, кроме её же ролей.</summary>
    public const string Archived = "archived";

    /// <summary>В справочнике такой нет, и прочитаны ВСЕ записи.</summary>
    public const string Absent = "absent";

    /// <summary>Совпадений нет, но часть записей прочитать не удалось: «нет» утверждать нельзя.</summary>
    public const string Unknown = "unknown";

    /// <summary>Название в скане есть, ИНН нет — сопоставлять не по чему.</summary>
    public const string NoTaxId = "noTaxId";

    /// <summary>ИНН прочитан с ошибкой, поиска не было.</summary>
    public const string BadTaxId = "badTaxId";

    /// <summary>Сопоставить нечем: нет типа организаций, поля ИНН в его схеме — или справочник не ответил.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Сторона счёта, как она прочитана в скане, и что о ней говорит справочник (issue #1077).
/// </summary>
/// <param name="State">Одно из <see cref="InvoicePartyStates" />.</param>
/// <param name="Name">Название из скана, как прочитано.</param>
/// <param name="TaxId">ИНН: проверенный — цифрами, непрочитанный — текстом из скана.</param>
/// <param name="Why">Почему состояние такое — словами для человека; у <c>matched</c> пусто.</param>
/// <param name="Candidates">Записи с таким ИНН — все: и роли найденной организации, и архивные.</param>
/// <param name="Unreadable">Записи справочника, чей ИНН прочитать не удалось, — поимённо: «проверьте
/// справочник» без них было бы советом без пути.</param>
/// <param name="Match">Найденная организация — есть только у <c>matched</c>. Отдельным полем, а не
/// «первым кандидатом»: в <paramref name="Candidates" /> лежат и её роли, и архивные двойники.</param>
public sealed record InvoicePartyView(
    string State, string? Name, string? TaxId, string? Why,
    IReadOnlyList<InvoicePartyCandidate> Candidates, IReadOnlyList<InvoicePartyCandidate> Unreadable,
    Guid? Match = null);

/// <summary>Обе стороны; <c>null</c> — про сторону в скане не прочитано ничего.</summary>
public sealed record InvoicePartiesView(InvoicePartyView? Supplier, InvoicePartyView? Payer);

/// <summary>
/// Сопоставление поставщика и плательщика распознанного счёта с организациями справочника — по ИНН
/// (ТЗ COST-8, задача B1b, issue #1077).
///
/// <para><b>Ничего не хранит.</b> Ответ считается из прочитанного в скане каждый раз заново: справочник
/// меняется — организацию заводят, отправляют в архив, — и сохранённое «такой нет» устарело бы молча.
/// В счёт отсюда ничего не пишется; найденную организацию кладёт в пустое поле фоновая задача
/// (<see cref="InvoiceScanReading" />), один раз.</para>
///
/// <para>⚠️ Сторож: «в справочнике нет» — самый дорогой ответ, по нему человек заведёт организацию.
/// Он даётся, только когда ИНН прочитан верно (контрольная сумма) и прочитаны все записи. Всё, что
/// мешает это утверждать, имеет своё состояние и свою причину.</para>
///
/// <para>⚠️ Сопоставление — помощь, а не условие: его отказ (справочник не ответил) не вправе ни
/// уронить раскладку уже прочитанного скана, ни спрятать от формы сохранённые значения. Поэтому
/// отказ здесь — тоже состояние стороны (<c>unavailable</c>), а не исключение.</para>
///
/// <para>По названию не сопоставляем: «ООО "Ромашка"» в справочнике может стоять как «Ромашка, ООО»
/// или «РОМАШКА», и совпадение по похожести подставило бы в счёт не ту организацию.</para>
/// </summary>
public sealed class InvoiceParties(IModuleCatalog catalog, ILoggerFactory logs)
{
    /// <summary>Ключ поля ИНН в типе «Организация». Тип ведёт человек — переименование даёт
    /// состояние <c>unavailable</c> с названием поля, а не «организаций нет».</summary>
    public const string TaxIdField = "ИНН";

    public async Task<InvoicePartiesView> MatchAsync(IReadOnlyDictionary<string, string?> read, CancellationToken ct)
    {
        var supplier = Side(read, CostsRecognitionProfiles.Supplier, CostsRecognitionProfiles.SupplierTaxId);
        var payer = Side(read, CostsRecognitionProfiles.Payer, CostsRecognitionProfiles.PayerTaxId);

        // В справочник идём, только если есть что искать.
        if (supplier?.TaxId is null && payer?.TaxId is null)
            return new(supplier?.Refusal, payer?.Refusal);

        ModuleCatalogFieldValues? answer;
        try
        {
            answer = await catalog.FieldValuesAsync(CostsRecordTypes.OrganizationCode, TaxIdField, RecordsFor.Display, ct);
        }
        catch (Exception crash) when (crash is not OperationCanceledException)
        {
            logs.CreateLogger<InvoiceParties>().LogError(crash,
                "Сопоставление сторон счёта: справочник организаций не ответил.");
            return new(Failed(supplier), Failed(payer));
        }

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
            ? new(name, null, new(InvoicePartyStates.NoTaxId, name, null,
                "ИНН в скане не прочитан, а по названию организации не сопоставляются — выберите её из справочника.", [], []))
            : new(name, null, new(InvoicePartyStates.BadTaxId, name, raw,
                $"ИНН {problem}. Сверьте его со сканом и выберите организацию из справочника.", [], []));
    }

    private static InvoicePartyView? Failed(Read? side) => side switch
    {
        null => null,
        { TaxId: null } => side.Refusal,
        _ => new(InvoicePartyStates.Unavailable, side.Name, side.TaxId,
            "Сопоставить со справочником не удалось: внутренняя ошибка, подробности в журнале сервера. " +
            "Откройте счёт ещё раз или выберите организацию из справочника.", [], []),
    };

    private static InvoicePartyView? Judge(Read? side, ModuleCatalogFieldValues? answer)
    {
        if (side is null) return null;
        if (side.TaxId is not { } taxId) return side.Refusal;

        if (answer is null)
            return new(InvoicePartyStates.Unavailable, side.Name, taxId,
                $"Тип «{CostsRecordTypes.OrganizationCode}» в системе не заведён — сопоставить не с чем.", [], []);
        if (!answer.Declared)
            return new(InvoicePartyStates.Unavailable, side.Name, taxId,
                $"В типе «{CostsRecordTypes.OrganizationCode}» нет простого поля «{TaxIdField}» — сопоставить не по чему. " +
                "Поле могли переименовать или сделать составным; организацию выберите из справочника.", [], []);

        var unreadable = answer.Records.Where(r => r.Unreadable).Select(Candidate).ToList();
        var found = answer.Records
            .Where(r => !r.Unreadable && Data.TaxId.FromRecord(r.Value) == taxId)
            .Select(Candidate)
            .ToList();

        // Роль архивной организации в счёт не идёт, сколько бы их ни было: поставщик — организация, а
        // она в архиве. Оставь мы роль «действующей», единственная роль подставилась бы в счёт сама —
        // запись чужой стройки вместо организации.
        var retired = found.Where(c => c.Archived).Select(c => c.Id).ToHashSet();
        var live = found
            .Where(c => !c.Archived && !(c.InheritedFrom is { } source && retired.Contains(source)))
            .ToList();

        return live.Count switch
        {
            1 => new(InvoicePartyStates.Matched, side.Name, taxId, null, found, unreadable, live[0].Id),
            > 1 when Principal(live) is { } principal =>
                new(InvoicePartyStates.Matched, side.Name, taxId, null, found, unreadable, principal),
            > 1 => new(InvoicePartyStates.Several, side.Name, taxId,
                $"С ИНН {taxId} в справочнике несколько действующих записей — выберите нужную.", found, unreadable),
            _ when found.Count > 0 => new(InvoicePartyStates.Archived, side.Name, taxId,
                $"Организация с ИНН {taxId} есть в справочнике, но в архиве. В новый счёт архивную не выбирают — " +
                "верните её из архива или выберите другую.", found, unreadable),
            _ when unreadable.Count > 0 => new(InvoicePartyStates.Unknown, side.Name, taxId,
                $"Организация с ИНН {taxId} среди прочитанных записей не найдена, но у части записей справочника " +
                $"ИНН прочитать не удалось: {Names(unreadable)}. Либо запись наследует реквизиты от записи, которой " +
                $"нет или которая лежит в другой области, либо в поле «{TaxIdField}» у неё лежит не текст и не число. " +
                "Сказать «такой организации нет» нельзя — проверьте эти записи.",
                [], unreadable),
            _ => new(InvoicePartyStates.Absent, side.Name, taxId, $"Организации с ИНН {taxId} в справочнике нет.", [], []),
        };
    }

    private static InvoicePartyCandidate Candidate(ModuleCatalogFieldValue value) =>
        new(value.Record.Id, value.Record.DisplayName, value.Record.EntityType, value.Record.Archived, value.InheritedFrom);

    /// <summary>Записи поимённо — первые несколько: текст читает человек, полный перечень — в ответе.</summary>
    private static string Names(List<InvoicePartyCandidate> records)
    {
        const int shown = 5;
        var names = string.Join(", ", records.Take(shown).Select(r => $"«{r.Name}»"));
        return records.Count > shown ? $"{names} и ещё {records.Count - shown}" : names;
    }

    /// <summary>
    /// Организация среди своих ролей; <c>null</c> — совпавшие записи не одна организация.
    ///
    /// <para>Роль («Подрядчик», «Заказчик») — запись стройки, которая реквизиты наследует; для документов
    /// стройки она и нужна, а поставщик счёта — сама организация. На живых данных роль есть почти у
    /// каждой организации, и без этого правила сопоставление всегда отвечало бы «несколько» (решение
    /// владельца продукта от 08.10.2026, по итогам проверки на стенде).</para>
    ///
    /// <para>⚠️ Узко нарочно: ИНН у всех совпавших взят у ОДНОЙ записи, и она сама среди действующих.
    /// Две записи со своим ИНН каждая — дубли, между ними выбирает человек.</para>
    /// </summary>
    private static Guid? Principal(List<InvoicePartyCandidate> live)
    {
        var sources = live.Select(c => c.InheritedFrom ?? c.Id).Distinct().ToList();
        return sources.Count == 1 && live.Any(c => c.Id == sources[0] && c.InheritedFrom is null) ? sources[0] : null;
    }

    private static string? Text(IReadOnlyDictionary<string, string?> read, string key) =>
        read.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
