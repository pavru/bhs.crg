using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Objects;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Кто вне ядра держит записи ядра: скан схемы находит, объявления модулей называют (ТЗ CORE-34.1,
/// задача G2, issue #1094).
///
/// <para>Живёт здесь, а не в ядре, по той же причине, что и <see cref="ModuleSchemaBackup" />: только
/// корень композиции видит и базу, и модули сборки. Скан (<see cref="ModuleReferenceScan" />) о
/// модулях не знает — он отдаёт адрес колонки и числа; чья это колонка и какими словами её назвать,
/// решается здесь, по объявлениям (<see cref="IAppModule.References" />).</para>
///
/// <para><b>Объявление ничего не обнаруживает.</b> Колонку, о которой модуль промолчал, скан найдёт
/// всё равно, и она удержит запись — только названа будет не словами. Объявлением модуль может лишь
/// одно: ОСВОБОДИТЬ колонку, сказав, что она помнит, но не держит.</para>
///
/// <para>Модули читаются все, включая выключенные: их данные на месте и держат так же (AUTH-19).
/// Схема, у которой модуля в сборке нет вовсе, держит тоже — молчаливо отпустить её значило бы
/// потерять ссылки модуля, который вернут следующей поставкой.</para>
///
/// <para><b>Отказ составляется для того, кто спрашивает</b> (решение владельца 04.10.2026). Число
/// держателей видно всем. Названия документов («счета № 12») — содержимое модуля, и показываются при
/// праве его читать. Адрес таблицы — устройство базы, и показывается администратору.</para>
/// </summary>
public sealed class ModuleRecordHolders(
    ModuleRegistry registry, ModuleReferenceScan scan, IUserPermissions permissions,
    IHttpContextAccessor http, ILogger<ModuleRecordHolders> log)
    : IRecordHolders
{
    /// <summary>Сколько документов назвать в отказе. Отказ — не отчёт: хватит, чтобы узнать место.</summary>
    private const int NamedDocuments = 5;

    public async Task<HeldRecords> HeldAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new HeldRecords(new HashSet<Guid>(), Verified: true);

        try
        {
            var held = new HashSet<Guid>();
            foreach (var column in await scan.FindAsync(ids, ct))
                if (Declared(column) is not { Holds: false }) held.UnionWith(column.Hits.Keys);
            return new HeldRecords(held, Verified: true);
        }
        catch (ModuleDataUnreadableException ex)
        {
            log.LogWarning(ex, "Держатели записей не проверены: не прочитана колонка {Address}", ex.Address);
            return new HeldRecords(new HashSet<Guid>(), Verified: false);
        }
    }

    public async Task<RecordHoldings> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return RecordHoldings.None;

        var granted = await GrantedAsync(ct);
        var admin = granted.Contains(CorePermissions.SystemManage);

        IReadOnlyList<HeldColumn> columns;
        try
        {
            columns = await scan.FindAsync(ids, ct);
        }
        catch (ModuleDataUnreadableException ex)
        {
            return Unreadable(ex, admin);
        }

        var found = new List<RecordHolder>();
        foreach (var column in columns)
        {
            var declared = Declared(column);
            if (declared is { Holds: false }) continue;

            found.Add(await DescribeAsync(column, ModuleOf(column.Schema), declared, granted, admin, ct));
        }

        return new RecordHoldings(found);
    }

    public async Task<HoldingsByRecord> FindEachAsync(
        IReadOnlyCollection<Guid> ids, bool documents, CancellationToken ct = default)
    {
        if (ids.Count == 0) return HoldingsByRecord.None;
        var granted = await GrantedAsync(ct);
        var admin = granted.Contains(CorePermissions.SystemManage);
        // Один скан на весь пакет: его ответ уже разложен по записям (HeldColumn.Hits). Спрашивать
        // по записи значило бы пройти все колонки модулей столько раз, сколько записей.
        List<HeldColumn> holding;
        try
        {
            holding = [.. (await scan.FindAsync(ids, ct)).Where(c => Declared(c) is not { Holds: false })];
        }
        catch (ModuleDataUnreadableException ex)
        {
            return new HoldingsByRecord(Unreadable(ex, admin), new Dictionary<Guid, RecordHoldings>());
        }

        var each = new Dictionary<Guid, RecordHoldings>();
        foreach (var id in holding.SelectMany(c => c.Hits.Keys).Distinct())
        {
            // Колонка глазами одной записи: сколько строк держат именно её.
            var own = holding.Where(c => c.Hits.ContainsKey(id))
                .Select(c => c with { Hits = new Dictionary<Guid, int> { [id] = c.Hits[id] }, Rows = c.Hits[id] })
                .ToList();
            // Названия документов — только когда их просили и только отпускаемой записи: у занятой
            // включённым модулем их никто не прочтёт.
            var releasable = documents && own.All(c => ModuleOf(c.Schema) is not { } m || !registry.IsEnabled(m.Code));
            var found = new List<RecordHolder>();
            foreach (var column in own)
                found.Add(await DescribeAsync(
                    column, ModuleOf(column.Schema), Declared(column), releasable ? granted : [], admin, ct));
            each[id] = new RecordHoldings(found);
        }
        return new HoldingsByRecord(null, each);
    }

    public async Task<bool> AnyDormantAsync(CancellationToken ct = default)
    {
        // Схема вне ядра, которую не назвал включённый модуль: выключенного или снятого. Есть ли в
        // ней ссылки на записи, здесь не выясняется — вопрос задаёт экран, чтобы решить, показывать
        // ли действие, и ответ «может быть» ему достаточен.
        var live = registry.Enabled.Select(m => m.Schema?.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return (await scan.SchemasAsync(ct)).Any(schema => !live.Contains(schema));
    }

    /// <summary>
    /// Не «никто не держит»: непрочитанные данные могли держать, и ответ обязан читаться как отказ на
    /// любом пути. Адрес — только администратору; остальным достаточно знать, что удаление не
    /// состоялось и к кому идти. Что именно ответила база, уходит в журнал сервера: её текст — не
    /// наш, и человеку его отдавать незачем.
    /// </summary>
    private RecordHoldings Unreadable(ModuleDataUnreadableException ex, bool admin)
    {
        log.LogWarning(ex, "Держатели записи не проверены: не прочитана колонка {Address}", ex.Address);
        return RecordHoldings.Unverified(
            "Не удалось проверить, ссылаются ли на это данные модулей. " +
            (admin
                ? $"Не прочитано: {ex.Address}. Если таблица посторонняя — дайте учётной " +
                  "записи приложения право чтения или вынесите таблицу из этой базы; если идёт " +
                  "обновление модуля — повторите позже. Ответ базы записан в журнал сервера."
                : "Обратитесь к администратору: причина видна ему в том же сообщении."));
    }

    private ModuleReference? Declared(HeldColumn column) =>
        ModuleOf(column.Schema)?.References.FirstOrDefault(r =>
            string.Equals(r.Table, column.Table, StringComparison.Ordinal)
            && string.Equals(r.Column, column.Column, StringComparison.Ordinal));

    private async Task<RecordHolder> DescribeAsync(
        HeldColumn column, IAppModule? module, ModuleReference? declared,
        IReadOnlyCollection<string> granted, bool admin, CancellationToken ct)
    {
        var address = admin ? $" ({column.Address})" : "";

        // Схему не назвал ни один модуль сборки. ⚠️ Этот исход получается ИСКЛЮЧЕНИЕМ, и с #1187 он
        // отпускает запись: модуль, чьё имя схемы разошлось бы с настоящим, выглядел бы снятым.
        if (module is null)
            return new RecordHolder(HolderState.Absent, AbsentOwner, "записи", column.Rows,
                Traceable: false, column.Address, Documents: null, $"{AbsentOwner}: {column.Rows}{address}");

        var enabled = registry.IsEnabled(module.Code);
        var state = enabled ? HolderState.Enabled : HolderState.Disabled;
        var owner = $"«{module.Title}»" + (enabled ? "" : " (модуль выключен)");

        // Колонку модуль не объявил: держит она так же, а назвать её нечем, кроме числа. Адрес
        // помогает тому, кто пойдёт разбираться, — и показывается только ему.
        if (declared is null)
            return new RecordHolder(state, owner, "записи", column.Rows, Traceable: false,
                column.Address, Documents: null, $"{owner}: записей — {column.Rows}{address}");

        string? documents = null;
        if (declared.Document is { } doc && granted.Contains(doc.Permission))
        {
            var labels = await scan.LabelsAsync(
                column, doc.Table, doc.Key, doc.LabelColumn, doc.Via, NamedDocuments + 1, ct);
            if (labels.Count > 0)
                documents = $"{doc.Noun}: {string.Join(", ", labels.Take(NamedDocuments))}"
                    + (labels.Count > NamedDocuments ? " и другие" : "");
        }
        // Обратный опрос находит потерю только у колонки, цель которой объявлена записью справочника:
        // у колонки JSON с разными целями вид цели неизвестен.
        return new RecordHolder(state, owner, declared.What, column.Rows,
            Traceable: declared.Target == ReferenceTarget.Record, column.Address, documents,
            $"{owner}: {declared.What} — {column.Rows}" + (documents is null ? "" : $" ({documents})"));
    }

    private const string AbsentOwner = "данные модуля, которого нет в этой сборке";

    private IAppModule? ModuleOf(string schema) =>
        registry.Enabled.Concat(registry.Disabled)
            .FirstOrDefault(m => string.Equals(m.Schema?.Name, schema, StringComparison.Ordinal));

    /// <summary>
    /// Права спрашивающего. Вне запроса (фоновая уборка) человека нет — и прав нет: названий и
    /// адресов такой вызов не получает, а ответ «держат или нет» от прав не зависит.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> GrantedAsync(CancellationToken ct)
    {
        var user = http.HttpContext?.User;
        return user?.Identity?.IsAuthenticated == true ? await permissions.ForAsync(user, ct) : [];
    }
}
