using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Выбор человека, который надо запомнить: строка бумаги → позиция.</summary>
public sealed record MatchToRemember(SupplierMatchKey Key, Guid NomenclatureId);

/// <summary>Что запомнилось сохранением строк — уезжает в ответе, чтобы форма сказала это словами.</summary>
/// <param name="Remembered">Сколько соответствий записано: новых и заменённых вместе.</param>
/// <param name="Replaced">Сколько из них ЗАМЕНИЛИ запомненное раньше — другой позицией.</param>
/// <param name="Failed">Запомнить НЕ УДАЛОСЬ: строки счёта при этом записаны. Ноль запомненного без этого
/// признака читался бы как «запоминать было нечего».</param>
public sealed record InvoiceMatchMemory(int Remembered, int Replaced, bool Failed = false)
{
    public static readonly InvoiceMatchMemory Nothing = new(0, 0);
}

/// <summary>Запомненное для строки, которую пишет сервер: позиция и соответствие, которое её назвало.</summary>
public sealed record RecalledPosition(Guid Position, Guid Match);

/// <summary>Запомненное для строк одного поставщика.</summary>
/// <param name="Lines">По ответу на строку, в порядке строк; <c>null</c> — подставлять нечего.</param>
/// <param name="Unusable">Сколько строк УЗНАНО, но подставить нельзя: позиция в архиве или удалена
/// (либо вида «Номенклатура» в системе нет вовсе). Без числа такие строки не отличить от незнакомых.</param>
public sealed record RecallAnswer(IReadOnlyList<RecalledPosition?> Lines, int Unusable);

/// <summary>
/// Пометка «(запомнено)» у строки — и что стало с соответствием с тех пор.
/// </summary>
/// <param name="By">По чему строка узнана: <c>code</c> — артикул, <c>name</c> — наименование;
/// <c>null</c> — соответствия больше нет, и сказать нечем.</param>
/// <param name="State"><c>current</c> — соответствие на месте и ведёт туда же; <c>changed</c> — его
/// направили на другую позицию; <c>gone</c> — его забыли; <c>foreign</c> — оно другого поставщика
/// (поставщика у счёта сменили после подстановки).</param>
public sealed record InvoiceLineMatchView(
    Guid Id, string? By, string State, string? Source, DateTimeOffset? RememberedAt, string? RememberedBy)
{
    public const string Current = "current";
    public const string Changed = "changed";
    public const string Gone = "gone";
    public const string Foreign = "foreign";

    /// <summary>
    /// Состояние ВЫЧИСЛЯЕТСЯ на чтении, а не хранится: правка соответствия строки чужих счетов не
    /// переписывает (она шла бы мимо их версии и мимо закрытых периодов), и узнать о ней строка может
    /// только так.
    /// </summary>
    public static InvoiceLineMatchView? Of(
        InvoiceLine line, Guid? supplierId, IReadOnlyDictionary<Guid, SupplierMatch>? matches)
    {
        if (line.MatchedBy is not { } id) return null;
        if (matches is null || !matches.TryGetValue(id, out var match))
            return new(id, null, Gone, null, null, null);

        var state = match.SupplierId != supplierId ? Foreign
            : match.NomenclatureId != line.NomenclatureId ? Changed
            : Current;
        return new(id, Name(match.Kind), state, match.SourceText, match.UpdatedAt, match.UpdatedByName);
    }

    public static string Name(SupplierMatchKind kind) => kind == SupplierMatchKind.Code ? "code" : "name";
}

/// <summary>
/// Соответствия наименований поставщика (задача C3, issue #1079, ТЗ COST-7.1): найти запомненное,
/// проверить пометку, запомнить выбор.
///
/// <para>⚠️ <b>Чего здесь нет и быть не может — создания позиции номенклатуры.</b> Соответствие ведёт
/// только на позицию, которая в справочнике уже есть: незнакомая строка ждёт выбора человека. Заводят
/// позицию явным действием под <c>core.nomenclature.edit</c>, и делает это ядро, а не модуль.</para>
/// </summary>
public static class SupplierMatching
{
    /// <summary>
    /// Запомненное для строк поставщика — по одному ответу на строку, в порядке строк; <c>null</c> —
    /// строка незнакома либо узнавать её не по чему.
    /// </summary>
    public static async Task<IReadOnlyList<SupplierMatch?>> FindAsync(
        CostsDbContext db, Guid supplierId, IReadOnlyList<(string? Code, string? Text)> lines,
        CancellationToken ct)
    {
        var sought = lines.Select(l => SupplierMatchKey.Sought(l.Code, l.Text).ToList()).ToList();
        var hashes = sought.SelectMany(keys => keys).Select(k => k.Hash).Distinct().ToList();
        if (hashes.Count == 0) return [.. lines.Select(_ => (SupplierMatch?)null)];

        var found = (await db.SupplierMatches.AsNoTracking()
                .Where(m => m.SupplierId == supplierId && hashes.Contains(m.KeyHash))
                .ToListAsync(ct))
            .ToDictionary(m => (m.Kind, m.KeyHash));

        // Старшинство — порядком ключей: артикул, затем наименование.
        return [.. sought.Select(keys => keys
            .Select(k => found.GetValueOrDefault((k.Kind, k.Hash)))
            .FirstOrDefault(m => m is not null))];
    }

    /// <summary>
    /// Проверить НОВЫЕ пометки «(запомнено)», присланные формой: соответствие есть, оно этого
    /// поставщика, ведёт на эту позицию и узнаёт эту строку.
    ///
    /// <para>Без проверки пометка была бы словом клиента: любую позицию можно было бы назвать
    /// «подставленной из запомненного», и строка выглядела бы проверенной памятью, которой не было.</para>
    ///
    /// <para>Пометки, которые у строки УЖЕ стояли, сюда не приходят — как и стоявшие ссылки: соответствие
    /// могли с тех пор сменить, и это показывается состоянием пометки, а не отказом сохранить счёт.</para>
    /// </summary>
    public static async Task EnsureMarksAsync(
        CostsDbContext db, Guid? supplierId, IReadOnlyList<(int Number, InvoiceLineValues Values)> fresh,
        CancellationToken ct)
    {
        if (fresh.Count == 0) return;

        var ids = fresh.Select(f => f.Values.MatchedBy!.Value).Distinct().ToList();
        var matches = await db.SupplierMatches.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        foreach (var (number, values) in fresh)
        {
            var why = !matches.TryGetValue(values.MatchedBy!.Value, out var match)
                    ? "такого соответствия нет — его могли забыть, пока форма была открыта"
                : match.SupplierId != supplierId
                    ? "это соответствие другого поставщика"
                : match.NomenclatureId != values.NomenclatureId
                    ? "соответствие ведёт на другую позицию — его могли сменить, пока форма была открыта"
                : !SupplierMatchKey.Sought(values.SupplierCode, values.SupplierText)
                    .Any(k => k.Kind == match.Kind && k.Hash == match.KeyHash)
                    ? "артикул и наименование строки с тех пор изменились, и соответствие её не узнаёт"
                : null;

            if (why is not null)
                throw new InvalidRequestException(
                    $"Строка {number}: пометка «запомнено» не подтверждается — {why}. Перечитайте счёт и " +
                    "выберите позицию заново: строка, названная подставленной из запомненного, обязана " +
                    "такой и быть.");
        }
    }

    /// <summary>
    /// Запомненное для строк, которые пишет САМ СЕРВЕР, — прочитанных со скана.
    ///
    /// <para>Правила те же, что у подстановки в форме, и взяты из тех же мест: узнаёт строку
    /// <see cref="FindAsync" />, а архивную и удалённую позицию отсекает правило новой ссылки (ТЗ
    /// CORE-34.4). Отсечённое не теряется — оно сосчитано: звавший обязан сказать о нём человеку.</para>
    ///
    /// <para>Только спрашивает и в строки ничего не кладёт: вопрос задают до замка счёта, а кладут под
    /// ним, и поставщик к тому времени может оказаться другим.</para>
    /// </summary>
    public static async Task<RecallAnswer> RecallAsync(
        CostsDbContext db, IModuleCatalog catalog, Guid supplierId, IReadOnlyList<InvoiceLineValues> rows,
        CancellationToken ct)
    {
        var found = await FindAsync(db, supplierId, [.. rows.Select(r => (r.SupplierCode, r.SupplierText))], ct);
        var verdicts = await NewReferences.JudgeAsync(catalog, CostsRecordTypes.NomenclatureCode,
            [.. found.OfType<SupplierMatch>().Select(m => m.NomenclatureId).Distinct()], ct);

        RecalledPosition?[] lines =
        [
            .. found.Select(match => match is not null && verdicts is not null
                    && verdicts.GetValueOrDefault(match.NomenclatureId) == NewReference.Fine
                ? new RecalledPosition(match.NomenclatureId, match.Id)
                : null),
        ];

        return new(lines, found.Count(match => match is not null) - lines.Count(line => line is not null));
    }

    /// <summary>Тот же ли у строки ключ соответствия, что был: артикул, а без него — наименование.</summary>
    public static bool SameKey(InvoiceLineValues was, InvoiceLineValues now) =>
        (SupplierMatchKey.Of(was.SupplierCode, was.SupplierText), SupplierMatchKey.Of(now.SupplierCode, now.SupplierText))
            is var (before, after) && before?.Kind == after?.Kind && before?.Hash == after?.Hash;

    /// <summary>
    /// Оставить из запоминаемого то, что ведёт на ДЕЙСТВУЮЩУЮ позицию.
    ///
    /// <para>Позиция, уже стоявшая в строке, записью строк не перепроверяется (ТЗ CORE-34.4) — она вправе
    /// быть архивной. Но стоит поправить в такой строке опечатку, и смена ключа делала её «новостью»:
    /// запоминалось соответствие на архивную позицию, и следующий счёт получал «запомненное в архиве»,
    /// запомненное уже архивным (ревью PR #1262). Запоминание — новая ссылка, и правило у него то же,
    /// что у выбора руками.</para>
    /// </summary>
    public static async Task<IReadOnlyList<MatchToRemember>> AliveAsync(
        IModuleCatalog catalog, IReadOnlyList<MatchToRemember> chosen, CancellationToken ct)
    {
        if (chosen.Count == 0) return chosen;

        var verdicts = await NewReferences.JudgeAsync(catalog, CostsRecordTypes.NomenclatureCode,
            [.. chosen.Select(c => c.NomenclatureId).Distinct()], ct);
        return verdicts is null
            ? []
            : [.. chosen.Where(c => verdicts.GetValueOrDefault(c.NomenclatureId) == NewReference.Fine)];
    }

    /// <summary>
    /// Что из присланного стоит запомнить: строки, где человек САМ поставил позицию и это новость.
    ///
    /// <para>Новость — новая строка либо строка, у которой сменились позиция или ключ. Повторная
    /// отправка того же набора не запоминает ничего: форма присылает строки целиком на каждое
    /// сохранение, и без этого «только в этой строке» действовало бы до первого же следующего
    /// сохранения.</para>
    ///
    /// <para>⚠️ Ключ, которому в ОДНОМ счёте назначены две разные позиции, не запоминается вовсе: какая
    /// из двух верна, знать неоткуда, а «последняя по порядку» — это случайность, выданная за выбор.</para>
    /// </summary>
    public static IReadOnlyList<MatchToRemember> Worth(
        IEnumerable<(InvoiceLineValues? Was, InvoiceLineValues Now, bool Remember)> lines)
    {
        var chosen = new List<MatchToRemember>();
        var all = new List<MatchToRemember>();

        foreach (var (was, now, remember) in lines)
        {
            if (now.NomenclatureId is not { } position) continue;
            if (SupplierMatchKey.Of(now.SupplierCode, now.SupplierText) is not { } key) continue;

            all.Add(new(key, position));

            // Подставленное не запоминается: оно и есть запомненное. Отказ человека — тоже.
            if (now.MatchedBy is not null || !remember) continue;

            var news = was is null
                || was.NomenclatureId != position
                || SupplierMatchKey.Of(was.SupplierCode, was.SupplierText) is not { } before
                || before.Kind != key.Kind || before.Hash != key.Hash;
            if (news) chosen.Add(new(key, position));
        }

        // Спор считается по ВСЕМ строкам с позицией, а не только по новым: строка, лежавшая с позицией
        // А, спорит со свежей строкой того же ключа с позицией Б ничуть не меньше.
        var disputed = all.GroupBy(m => (m.Key.Kind, m.Key.Hash))
            .Where(g => g.Select(m => m.NomenclatureId).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        return [.. chosen.Where(m => !disputed.Contains((m.Key.Kind, m.Key.Hash)))
            .DistinctBy(m => (m.Key.Kind, m.Key.Hash))];
    }

    /// <summary>
    /// Запомнить выбор: новое соответствие либо замена запомненного раньше.
    ///
    /// <para>⚠️ <b>Вне транзакции счёта, и нарочно.</b> Замок записи — по счёту, а таблица у
    /// соответствий общая: два счёта одного поставщика, сохранённые разом, оба добавляют одну и ту же
    /// строку, и второй получает отказ по ключу. Внутри транзакции счёта этот отказ погубил бы само
    /// сохранение строк; здесь он означает лишь «кто-то успел раньше» — перечитываем и повторяем.
    /// Память о выборе — справка к счёту, а не его часть: счёт сохранён в любом случае.</para>
    /// </summary>
    public static async Task<InvoiceMatchMemory> RememberAsync(
        CostsDbContext db, Guid supplierId, IReadOnlyList<MatchToRemember> chosen, IModuleUser user,
        CancellationToken ct)
    {
        if (chosen.Count == 0) return InvoiceMatchMemory.Nothing;

        var hashes = chosen.Select(c => c.Key.Hash).ToList();

        for (var attempt = 0; ; attempt++)
        {
            var known = (await db.SupplierMatches
                    .Where(m => m.SupplierId == supplierId && hashes.Contains(m.KeyHash))
                    .ToListAsync(ct))
                .ToDictionary(m => (m.Kind, m.KeyHash));

            int remembered = 0, replaced = 0;
            foreach (var (key, position) in chosen)
            {
                if (!known.TryGetValue((key.Kind, key.Hash), out var match))
                {
                    db.SupplierMatches.Add(SupplierMatch.Create(supplierId, key, position, user.Id, user.Name));
                    remembered++;
                }
                else if (match.NomenclatureId != position)
                {
                    match.Point(key.Source, position, user.Id, user.Name);
                    remembered++;
                    replaced++;
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return new(remembered, replaced);
            }
            catch (Exception lost) when (attempt == 0
                && lost is DbUpdateException or ConflictException { InnerException: DbUpdateConcurrencyException })
            {
                // Кто-то добавил ту же строку раньше — либо сменил её позицию из списка соответствий, пока
                // мы читали (контекст отдаёт это отказом 409, см. CostsDbContext.SaveChangesAsync). Свои
                // несохранённые записи убираем из-под отслеживания — иначе повтор записал бы их снова.
                foreach (var entry in db.ChangeTracker.Entries<SupplierMatch>().ToList())
                    entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Запомнить, не дав отказу запоминания стать отказом сохранения строк.
    ///
    /// <para>Строки к этому мгновению записаны и зафиксированы. Пробрось мы отказ — человек увидел бы
    /// «строки не сохранены» при сохранённых строках, а форма осталась бы со старой версией счёта и на
    /// повторе получила бы «счёт тем временем изменили» от собственной правки (ревью PR #1262). Поэтому
    /// отказ уезжает в ответе признаком, а причина — в журнал сервера.</para>
    /// </summary>
    public static async Task<InvoiceMatchMemory> RememberSafelyAsync(
        CostsDbContext db, Guid supplierId, IReadOnlyList<MatchToRemember> chosen, IModuleUser user, ILogger log,
        CancellationToken ct)
    {
        try
        {
            return await RememberAsync(db, supplierId, chosen, user, ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Несохранённое — из-под отслеживания: дальше тем же контекстом собирается ответ, и ничто
            // не должно попытаться записать это снова.
            foreach (var entry in db.ChangeTracker.Entries<SupplierMatch>().ToList())
                entry.State = EntityState.Detached;

            log.LogWarning(failure,
                "Соответствия наименований поставщика {SupplierId} не запомнены ({Count}): строки счёта записаны.",
                supplierId, chosen.Count);
            return new(0, 0, Failed: true);
        }
    }

    /// <summary>Соответствия, на которые ссылаются пометки строк, — для ответа счёта.</summary>
    public static async Task<IReadOnlyDictionary<Guid, SupplierMatch>> MarkedAsync(
        CostsDbContext db, IReadOnlyList<InvoiceLine> lines, CancellationToken ct)
    {
        var ids = lines.Select(l => l.MatchedBy).OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, SupplierMatch>();

        return await db.SupplierMatches.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
    }
}
