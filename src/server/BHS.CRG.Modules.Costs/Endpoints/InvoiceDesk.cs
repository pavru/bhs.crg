using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Счёт под записью: прочитан ПОСЛЕ замка, границы периодов неподвижны до конца транзакции.</summary>
/// <param name="Locked">Чем счёт заперт — словами; <c>null</c> — не заперт. Запертый счёт досюда доходит
/// только у пути, который назвал себя разрешённым (приложить скан).</param>
public sealed record InvoiceWrite(Invoice Invoice, PeriodBoundaries Boundaries, string? Locked);

/// <summary>
/// Запись счёта и его ответ — одним местом на все адреса (задача C5, issue #1082).
///
/// <para><b>Зачем связка.</b> С отметкой оплаты у каждой правки счёта появилось три обязанности, и ни
/// одну из них адрес не выполнит по памяти: идти под замком «запись против закрытия периода»; отказать,
/// если счёт заперт закрытым периодом; у оплаченного счёта — переложить учётные даты долей и остатка.
/// Адресов, пишущих счёт, десять, и одиннадцатый забыл бы. Поэтому порядок задаёт
/// <see cref="WriteAsync{T}" />, а перепись по исходникам (<c>InvoiceWritePathTests</c>) не даёт писать
/// счёт мимо неё.</para>
///
/// <para><b>Порядок внутри связки</b> (ревизия Архитектора): замок → чтение счёта → проверка «не
/// заперт» → правка адреса → учётные даты → фиксация. Счёт читается ПОСЛЕ замка: проверка по
/// прочитанному до него проверяла бы прошлое. Журнал ядра, удаление файлов и сборка ответа — СНАРУЖИ,
/// после возврата: журнал пишется соединением ядра и зафиксировался бы раньше модуля.</para>
/// </summary>
public sealed class InvoiceDesk(
    CostsDbContext db, IModuleCatalog catalog, AllocationPlacesSource places, IModulePeriods periods,
    IHttpContextAccessor http, IModuleReferenceTargets targets)
{
    /// <summary>Заголовок, которым правка называет версию счёта, по которой она собрана.</summary>
    public const string SeenHeader = SeenVersion.Header;

    /// <summary>
    /// Выполнить правку счёта.
    /// </summary>
    /// <param name="evenLocked">Путь, которому запертый счёт не отказ сам по себе, — он решает по
    /// <see cref="InvoiceWrite.Locked" />. Таких один: приложить скан можно, заменить нельзя.</param>
    public Task<T> WriteAsync<T>(
        Guid id, Func<InvoiceWrite, Task<T>> write, CancellationToken ct, bool evenLocked = false) =>
        db.InOpenPeriodAsync(periods, async boundaries =>
        {
            var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
            EnsureSeen(InvoiceEndpoints.Label(invoice), db.VersionOf(invoice));
            var paid = invoice.Payment == InvoicePaymentState.Paid;
            var before = PostedBefore.None;
            string? locked = null;

            if (paid)
            {
                var stored = await db.InvoiceAllocations.AsNoTracking()
                    .Where(a => a.InvoiceId == invoice.Id).ToListAsync(ct);
                before = PaymentPosting.Before(invoice, stored);

                if (ClosedPeriodGuard.LockOf(invoice, stored, boundaries) is { } found)
                {
                    locked = PaymentViews.Text(found, await places.LoadAsync(ct));
                    if (!evenLocked)
                        throw new ConflictException(
                            $"{InvoiceEndpoints.Label(invoice)} заперт: {locked}. Изменить нельзя поля, строки, " +
                            "разноску и отметку оплаты — счёт оплачен, и его деньги уже вошли в закрытый период. " +
                            "Чтобы исправить, сначала отмените закрытие периода.");
                }
            }

            // Тронула ли правка ДЕНЬГИ — видит сама связка, по записям под сохранением: адрес об этом не
            // спрашивают, он бы однажды ответил неверно.
            var money = false;
            void Watch(object? sender, SavingChangesEventArgs e) => money |= MoneyTouched();

            T result;
            db.SavingChanges += Watch;
            try
            {
                result = await write(new InvoiceWrite(invoice, boundaries, locked));
            }
            finally
            {
                db.SavingChanges -= Watch;
            }
            // Правка, которую адрес не сохранил сам, уйдёт в базу сохранением связки — её считаем тоже.
            money |= MoneyTouched();

            // Учётные даты — ПОСЛЕ правки адреса и одним местом: доли создаются в трёх местах, и ни одно
            // не знает, оплачен ли счёт. Счёт, не бывший и не ставший оплаченным, сюда не заходит.
            //
            // У оплаченного — только когда тронуты деньги (ревью PR #1191). «Всё верно», платёжный
            // документ и скан расклада не меняют, а проверка «счёт обязан остаться сведённым» на них
            // отказывала бы правке, которая денег не касалась, — словами «после этой правки сумма
            // расходится».
            var now = invoice.Payment == InvoicePaymentState.Paid;
            if (paid != now || (now && money))
                await PostAsync(invoice, before, boundaries, ct);

            return result;
        }, ct);

    /// <summary>
    /// Правка собрана по той версии счёта, что лежит сейчас (issue #1176).
    ///
    /// <para>До этой проверки сервер сверял версию, прочитанную ТЕМ ЖЕ запросом: двое открыли счёт,
    /// первый сохранил строки, второй через час сохранил свои — и набор первого был заменён целиком,
    /// без отказа. То же с шапкой, разноской строки и переходами: «разобран» нажимали по тому, что
    /// видели на экране, а записывали по тому, что лежит в базе.</para>
    ///
    /// <para>⚠️ <b>Проверка стоит в связке, а не в адресах</b>, и версию связка берёт из запроса сама:
    /// пишущий адрес счёта не может её забыть, потому что его об этом не спрашивают. Отсюда и
    /// заголовок, а не поле тела, как у наборов данных и накладной: тела у этих адресов разные, у двух
    /// переходов тела нет вовсе, а скан приходит формой с файлом — поле пришлось бы разбирать в
    /// девяти местах девятью способами.</para>
    ///
    /// <para>⚠️ Без названной версии — отказ, а не «значит, свежая»: умолчание записывало бы
    /// устаревшую форму поверх чужой правки ровно так же, как раньше.</para>
    /// </summary>
    private void EnsureSeen(string label, string stored)
    {
        var request = http.HttpContext?.Request
            ?? throw new InvalidOperationException(
                "Счёт правят вне запроса: версию, по которой собрана правка, назвать некому. Связка записи " +
                "счёта рассчитана на адрес; фоновой правке нужен свой путь с явной версией.");

        // Заголовок читает общее место (кавычки и W/ снимаются, «*» — отказ о записи, а не «счёт
        // изменили»): то же правило — у записи общих данных.
        if (!SeenVersion.TryRead(request, out var seen)) throw new InvalidRequestException(SeenVersion.Malformed);
        if (seen is null)
            throw new InvalidRequestException(
                $"Не названа версия счёта, по которой собрана правка (заголовок {SeenHeader}) — она приходит " +
                "в ответе чтения полем «version». Без неё правка записалась бы поверх чужой.");

        if (seen != stored)
            throw new ConflictException(
                $"{label} тем временем изменили, и это действие не выполнено. " +
                "Перечитайте счёт и повторите: вы видели прежнее состояние, и записанная поверх правка " +
                "затёрла бы чужую.");
    }

    /// <summary>
    /// Та же проверка ДО связки — для адреса, которому дорого до неё дойти: скан сначала выгружается в
    /// хранилище, и устаревшая форма заливала бы файл целиком ради отказа (ревью PR #1208).
    ///
    /// <para>⚠️ Это не замена проверке в связке, а её ранний повтор: между ним и замком счёт могут
    /// изменить, и решает по-прежнему связка. Версия читается БЕЗ отслеживания: отслеженный здесь счёт
    /// связка получила бы обратно из контекста, а не из базы, — и проверяла бы прочитанное до замка.</para>
    /// </summary>
    public async Task EnsureSeenAsync(Guid id, CancellationToken ct) =>
        EnsureSeen("Счёт", await db.StoredInvoiceVersionAsync(id, ct) ?? throw new NotFoundException("Счёт не найден."));

    /// <summary>
    /// Меняют ли записи под сохранением деньги счёта. Денег у расклада три источника, и других нет
    /// (<see cref="PaymentPosting.Plan" /> берёт только их): сумма к оплате, строки и доли разноски.
    ///
    /// <para>⚠️ Видно только то, что идёт через отслеживание контекста. Запись мимо него
    /// (<c>ExecuteUpdate</c>, <c>ExecuteSql</c>) связка не заметила бы — поэтому в модуле её нет, и
    /// сторож по исходникам (<c>InvoiceWritePathTests</c>) не даёт ей появиться.</para>
    /// </summary>
    private bool MoneyTouched() => db.ChangeTracker.Entries().Any(entry =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
        && (entry.Entity is InvoiceLine or InvoiceAllocation
            || (entry.Entity is Invoice && entry.Property(nameof(Invoice.Total)).IsModified)));

    /// <summary>
    /// Переложить учётные даты по состоянию ПОСЛЕ правки: оплачен — каждой доле и остатку, не оплачен —
    /// никому.
    ///
    /// <para>⚠️ Оплаченный счёт обязан остаться сведённым (решение владельца 04.10.2026): иначе правило
    /// «сумма бьётся со строками» обходилось бы в два шага — оплатить сведённый, потом поправить строку.</para>
    /// </summary>
    private async Task PostAsync(Invoice invoice, PostedBefore before, PeriodBoundaries boundaries, CancellationToken ct)
    {
        var parts = await db.InvoiceAllocations.Where(a => a.InvoiceId == invoice.Id).ToListAsync(ct);

        if (invoice.Payment != InvoicePaymentState.Paid)
        {
            PaymentPosting.Clear(invoice, parts);
            await db.SaveChangesAsync(ct);
            return;
        }

        var lines = await InvoiceLineEndpoints.StoredLinesAsync(db, invoice, ct);
        if (PaymentPosting.Refusal(invoice, PaymentPosting.Balance(lines, parts, invoice.Total)) is { } why)
            throw new InvalidRequestException(
                $"{InvoiceEndpoints.Label(invoice)} оплачен, и после этой правки {why}. Оплаченный счёт обязан " +
                "оставаться сведённым: его сумма уже вошла в затраты. Сначала отмените оплату.");

        PaymentPosting.Apply(invoice, parts,
            PaymentPosting.Plan(invoice.PaidOn!.Value, invoice.Total!.Value, lines, parts, before, boundaries));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Счёт целиком: реквизиты, метки, дубликаты, строки, сверка сумм, разноска и оплата.
    ///
    /// <para>Одним помощником на все адреса — и это не про экономию строк: собери ответ каждый адрес
    /// сам, часть из них однажды вернула бы счёт без строк, и форма получила бы пустую таблицу там, где
    /// строки есть. Ошибка была бы видна только на одном действии из десяти.</para>
    ///
    /// <para>⚠️ Названия позиций номенклатуры берутся ОДНИМ обращением к справочнику на весь счёт, и
    /// «вида нет вовсе» на чтении не отказ: тип «Номенклатура» есть не в каждой установке, а счёт со
    /// строками от этого не перестаёт существовать. Но и потерей это не считается — незнание
    /// доезжает до формы незнанием (<c>InvoiceLineView.NomenclatureLost</c>).</para>
    /// </summary>
    public async Task<InvoiceView> ViewAsync(Invoice invoice, CancellationToken ct)
    {
        var lines = await db.InvoiceLines.AsNoTracking()
            .Where(l => l.InvoiceId == invoice.Id)
            .OrderBy(l => l.Ordinal)
            .ToListAsync(ct);
        var parts = await db.InvoiceAllocations.AsNoTracking()
            .Where(a => a.InvoiceId == invoice.Id)
            .ToListAsync(ct);
        var known = parts.Count == 0 ? AllocationPlaces.None : await places.LoadAsync(ct);

        // Записи справочников, на которые ссылаются шапка и строки, — одним вопросом ядру. Спрашивается
        // «есть ли запись», а не «есть ли она в списке организаций»: список не различает удалённую
        // запись, запись другого вида и вид, которого нет в установке (issue #1184).
        var records = await targets.StatesAsync(ReferenceTarget.Record,
            [.. new[] { invoice.SupplierId, invoice.PayerId }.Concat(lines.Select(l => l.NomenclatureId))
                .Concat(parts.Select(p => p.ArticleId)).OfType<Guid>().Distinct()], ct);
        known = known with { Existing = records.Where(r => r.Value != ReferenceState.Lost).Select(r => r.Key).ToHashSet() };
        var type = await targets.StatesAsync(ReferenceTarget.DocumentType, [invoice.DocumentTypeId], ct);
        string? State(Guid? id) => id is { } key ? InvoiceReferencesView.Of(records[key]) : null;

        // Названия АРХИВНЫХ сторон — с ответом счёта: в списке на выбор их нет, и форме взять
        // название было бы неоткуда. Действующую сторону форма называет по списку, и спрашивать о
        // ней на каждом чтении счёта незачем (ревью PR #1227). Типа «Организация» нет — названий нет.
        var parties = new[] { invoice.SupplierId, invoice.PayerId }.OfType<Guid>().Distinct()
            .Where(id => records[id] == ReferenceState.Archived).ToList();
        var organizations = parties.Count == 0
            ? null
            : await catalog.RefsAsync(CostsRecordTypes.OrganizationCode, parties, ct);
        string? Name(Guid? id) => organizations?.FirstOrDefault(o => o.Id == id)?.DisplayName;

        return InvoiceViews.Of(invoice, db.VersionOf(invoice),
            await InvoiceEndpoints.DuplicatesAsync(db, invoice, ct), lines,
            await InvoiceEndpoints.NomenclatureNamesAsync(catalog, lines, ct),
            InvoiceAllocations.Read(invoice, lines.Select(InvoiceAllocations.Line), parts, known),
            await PaymentAsync(invoice, lines, parts, known, ct),
            new InvoiceReferencesView(State(invoice.SupplierId), State(invoice.PayerId),
                InvoiceReferencesView.Of(type[invoice.DocumentTypeId]),
                Name(invoice.SupplierId), Name(invoice.PayerId)),
            records.Where(r => r.Value == ReferenceState.Lost).Select(r => r.Key).ToHashSet(),
            records.Where(r => r.Value == ReferenceState.Archived).Select(r => r.Key).ToHashSet());
    }

    /// <summary>
    /// Оплата в ответе счёта. Границы периодов спрашиваются только у оплаченного: неоплаченный не
    /// принадлежит ни одному периоду, а счетов без оплаты — большинство.
    /// </summary>
    private async Task<PaymentView> PaymentAsync(
        Invoice invoice, IReadOnlyList<InvoiceLine> lines, IReadOnlyList<InvoiceAllocation> parts,
        AllocationPlaces known, CancellationToken ct)
    {
        var balance = PaymentPosting.Balance(lines.Select(InvoiceAllocations.Line), parts, invoice.Total);

        if (invoice.Payment != InvoicePaymentState.Paid)
            return new PaymentView(false, null, null, null, Unpayable(invoice, balance), null, []);

        var boundaries = await periods.BoundariesAsync(ct);
        var locked = ClosedPeriodGuard.LockOf(invoice, parts, boundaries);

        // Учётные месяцы — те, куда легли ДЕНЬГИ (ревью PR #1191), и той же функцией, что у реестра.
        var months = PaymentPosting.Months(balance, invoice.Total, parts, invoice.RemainderAccountingOn);

        // Замок стройки возможен, только когда доли есть, — а тогда места уже прочитаны.
        return new PaymentView(true, invoice.PaidOn, invoice.PaymentDocument, invoice.PaidAt, null,
            locked is null ? null : PaymentViews.Text(locked, known), [.. months.Select(m => PaymentViews.Month(m.Month))]);
    }

    /// <summary>Почему неоплаченный счёт оплатить нельзя — тем же правилом, что откажет запись.</summary>
    internal static string? Unpayable(Invoice invoice, AllocationBalance balance) =>
        invoice.State == InvoiceState.Rejected ? "счёт отклонён" : PaymentPosting.Refusal(invoice, balance);
}
