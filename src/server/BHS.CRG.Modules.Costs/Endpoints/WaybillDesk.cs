using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Правка накладной в руках адреса: сама накладная и границы периодов, неподвижные до конца записи.</summary>
public sealed record WaybillWrite(Waybill Waybill, PeriodBoundaries Boundaries);

/// <summary>
/// Связка записи накладной (issue #1285): каждая правка существующей накладной идёт через неё.
///
/// <para>До неё у накладной не было замка периода вовсе: проведённую накладную закрытого месяца
/// возвращали в черновик, правили и проводили снова, и «Материалы на объекте» за закрытый месяц
/// менялись молча. Адресов, правящих существующую накладную, пять (шапка, строки, сопоставление,
/// проведение, возврат в черновик), и шестой (удаление, #1283) забыл бы проверку так же, как забыли
/// первые пять. Поэтому порядок задаёт <see cref="WriteAsync{T}" />, а перепись по исходникам
/// (<c>InvoiceWritePathTests</c>) не даёт сохранять накладную мимо неё. Создание идёт мимо: новая
/// накладная — черновик, а черновик не принадлежит ни одному периоду.</para>
///
/// <para><b>Порядок внутри связки</b> — тот же, что у счёта (<see cref="InvoiceDesk" />): замок записи
/// против закрытия периода → чтение накладной → версия, которую видел человек → «не заперта» → правка
/// адреса. Накладная читается ПОСЛЕ замка: проверка по прочитанному до него проверяла бы прошлое.
/// Журнал и сборка ответа — снаружи, после возврата.</para>
///
/// <para>Со столом счёта не объединена нарочно: там деньги и учётные даты долей, которых у накладной
/// нет. Общее у них — порядок, и он короче, чем была бы общая абстракция.</para>
/// </summary>
public sealed class WaybillDesk(
    CostsDbContext db, IModulePeriods periods, IModuleConstructions sites, IHttpContextAccessor http)
{
    // Границы, по которым решена запись этого запроса. Ответ после правки обязан называть «заперта» по
    // ТОМУ ЖЕ снимку, а не по прочитанному заново уже вне замка (ревью PR #1296): второй снимок — и
    // лишнее чтение ядра на каждую сопоставленную строку, и возможность ответить не тем, по чему решали.
    private PeriodBoundaries? _boundaries;

    /// <summary>Выполнить правку накладной. Отказ — исключением: транзакция откатится.</summary>
    /// <param name="settled">
    /// «Накладная уже в том состоянии, куда её ведут» — правка адреса тогда ничего не пишет, и замок
    /// периода ей не отказывает (ревью PR #1296): повторное «Провести» у проведённой накладной закрытого
    /// месяца иначе получало отказ словами про возврат в черновик, а человек читал «не проведена» про
    /// проведённую. Версию такой запрос называет как любой другой. ⚠️ Назвавший это условие адрес обязан
    /// в этом случае вернуться, ничего не записав: замок здесь пропущен под его слово.
    /// </param>
    public Task<T> WriteAsync<T>(
        Guid id, Func<WaybillWrite, Task<T>> write, CancellationToken ct, Func<Waybill, bool>? settled = null) =>
        db.InOpenPeriodAsync(periods, async boundaries =>
        {
            _boundaries = boundaries;
            var waybill = await db.Waybills.FirstOrDefaultAsync(w => w.Id == id, ct)
                ?? throw new NotFoundException("Накладная не найдена.");
            EnsureSeen(waybill);

            if (settled?.Invoke(waybill) != true && WaybillLock.Of(waybill, boundaries) is { } found)
                throw new ConflictException(
                    $"{WaybillEndpoints.Label(waybill)} заперта: {WaybillLock.Text(found, await SiteNameAsync(found, ct))}. " +
                    "Вернуть её в черновик, изменить и сопоставить строки нельзя — она проведена, и её " +
                    "материалы уже вошли в закрытый период. Чтобы исправить, сначала отмените закрытие периода.");

            return await write(new WaybillWrite(waybill, boundaries));
        }, ct);

    /// <summary>Чем заперта накладная — словами для формы; <c>null</c> — не заперта.</summary>
    public async Task<string?> LockedAsync(Waybill waybill, ModuleConstruction? site, CancellationToken ct) =>
        waybill.State != WaybillState.Posted ? null
        : WaybillLock.Of(waybill, _boundaries ??= await periods.BoundariesAsync(ct)) is { } found
            ? WaybillLock.Text(found, site?.Name)
        : null;

    /// <summary>
    /// Имя стройки, чьё закрытие заперло накладную, — для отказа. Отказ читают в тосте, без формы под
    /// ним, и «у стройки накладной» не говорило, закрытие какой стройки отменять; у счёта имя в отказе
    /// есть. Список строек читается только на этом пути: пишущий запрос, которому не отказано, его не ждёт.
    /// </summary>
    private async Task<string?> SiteNameAsync(PeriodLock found, CancellationToken ct) =>
        found.ConstructionId is { } site
            ? (await sites.ListAsync(ct)).FirstOrDefault(s => s.Id == site)?.Name
            : null;

    /// <summary>
    /// Версия, по которой собрана правка, — заголовком <c>If-Match</c>, как у счёта (issue #1176).
    /// Заголовок, а не поле тела: у проведения и возврата в черновик тела нет.
    /// </summary>
    private void EnsureSeen(Waybill waybill)
    {
        if (!SeenVersion.TryRead(http.HttpContext!.Request, out var seen))
            throw new InvalidRequestException(SeenVersion.Malformed);
        if (seen is null)
            throw new InvalidRequestException(
                $"Не названа версия накладной, по которой собрана правка (заголовок {SeenVersion.Header}) — " +
                "она приходит в ответе чтения полем «version». Без неё правка записалась бы поверх чужой.");

        if (seen != db.VersionOf(waybill))
            throw new ConflictException(
                $"{WaybillEndpoints.Label(waybill)} тем временем изменили, и это действие не выполнено. " +
                "Перечитайте накладную и повторите: вы видели прежнее состояние, и записанная поверх правка " +
                "затёрла бы чужую — в том числе удалила бы строки, которых не было на вашем экране.");
    }
}
