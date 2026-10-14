using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Domain.Tables;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Tables;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Api.Modules.Tables;

/// <summary>
/// Сохранённые представления таблиц: свои и общие (ТЗ CORE-33; задача G3a, issue #1095).
///
/// <para><b>Кто что вправе.</b> Своё личное заводит, правит и удаляет любой, кому открыта таблица.
/// Общее заводит, правит и удаляет держатель права <see cref="CorePermissions.ViewsShare" /> — в
/// том числе автор, но только пока право у него есть: иначе отнятое право продолжало бы действовать
/// на всё, что человек успел завести. «Вернуть в свои» — только автору: держатель права чужое общее
/// правит и удаляет, но себе не забирает.</para>
///
/// <para><b>Представление модуля заперто.</b> В базе его нет — оно объявлено кодом модуля, — поэтому
/// правка по его коду отвечает не «не найдено», а отказом с выходом: сохранить как своё.</para>
///
/// <para><b>Чужое личное неотличимо от удалённого</b> — оба отвечают одним 404. Разные ответы
/// подтверждали бы, что представление с таким идентификатором у кого-то есть.</para>
/// </summary>
public sealed class SavedTableViewService(ModuleTableService tables, AppDbContext db, IActivityLog journal)
{
    /// <summary>Сколько личных представлений одной таблицы у одного человека и сколько общих у таблицы.</summary>
    public const int OwnMax = 100;
    public const int SharedMax = 200;

    private const int Invalid = StatusCodes.Status400BadRequest;
    private const int Forbidden = StatusCodes.Status403Forbidden;
    private const int Missing = StatusCodes.Status404NotFound;
    private const int Conflict = StatusCodes.Status409Conflict;

    /// <summary>
    /// Представления таблицы, видимые спрашивающему: общие и его личные. У выключенного модуля —
    /// пусто: представления скрыты, но не удалены (ТЗ CORE-33), и вернутся вместе с модулем.
    /// </summary>
    public async Task<(IReadOnlyList<SavedTableViewDto>? Views, TableRefusal? Refusal)> ListAsync(
        string address, DataAccess access, CancellationToken ct)
    {
        var (gate, denied) = await tables.GateAsync(address, access, ct);
        if (gate is null) return (null, denied);
        if (gate.Off is not null) return ([], null);

        var me = access.UserId!.Value;
        var rows = await db.SavedTableViews.AsNoTracking()
            .Where(v => v.TableAddress == gate.Address && (v.Shared || v.OwnerId == me))
            .OrderBy(v => v.Title).ThenBy(v => v.Id)
            .ToListAsync(ct);

        var ids = rows.Where(r => r.OwnerId is not null).Select(r => r.OwnerId!.Value).Distinct().ToList();
        var authors = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName, ct);
        return ([.. rows.Select(r => Dto(r, access, r.OwnerId is { } id ? authors.GetValueOrDefault(id) : null))], null);
    }

    public async Task<(SavedTableViewDto? View, TableRefusal? Refusal)> CreateAsync(
        string address, DataAccess access, SaveTableView body, CancellationToken ct)
    {
        var (gate, denied) = await WritableGateAsync(address, access, ct);
        if (gate is null) return (null, denied);
        var me = access.UserId!.Value;

        var (title, state, invalid) = Checked(body);
        if (invalid is not null) return (null, invalid);

        string? basedOn = null;
        if (!string.IsNullOrWhiteSpace(body.BasedOn))
        {
            var declared = gate.ModuleViews.FirstOrDefault(
                v => string.Equals(v.Code, body.BasedOn.Trim(), StringComparison.OrdinalIgnoreCase));
            if (declared is null)
                return (null, new(Invalid,
                    $"Не сохранено: представления «{body.BasedOn.Trim()}» у таблицы «{gate.Title}» модуль не объявляет."));
            basedOn = declared.Code;
        }

        if (body.Shared && !access.Allows(CorePermissions.ViewsShare)) return (null, NoRightToShare(access));
        if (await GroupProblemAsync(gate, me, body.Shared, title, null, ct) is { } taken) return (null, taken);

        var row = SavedTableView.Create(gate.Address, basedOn, me, title, body.Shared, SavedTableViewRules.Store(state));
        db.SavedTableViews.Add(row);
        var failed = await SaveAsync(row.Shared
            ? () => journal.RecordAsync(ActivityActions.TableViewShared, row.Id.ToString(), Label(gate, title),
                after: row.State, ct: ct)
            : () => db.SaveChangesAsync(ct), title);
        return failed is null ? (Dto(row, access, access.Who), null) : (null, failed);
    }

    /// <summary>Правка заменяет представление целиком: название, «общее» и состояние.</summary>
    /// <param name="view">Идентификатор сохранённого представления — или код представления модуля,
    /// и тогда ответ — отказ с выходом.</param>
    /// <param name="seen">Версия, по которой правка собрана.</param>
    public async Task<(SavedTableViewDto? View, TableRefusal? Refusal)> UpdateAsync(
        string address, string view, string seen, DataAccess access, SaveTableView body, CancellationToken ct)
    {
        var (gate, denied) = await WritableGateAsync(address, access, ct);
        if (gate is null) return (null, denied);
        var me = access.UserId!.Value;

        var (row, refusal) = await FindWritableAsync(gate, view, seen, access, ct);
        if (row is null) return (null, refusal);

        var (title, state, invalid) = Checked(body);
        if (invalid is not null) return (null, invalid);

        if (body.Shared && !row.Shared && !access.Allows(CorePermissions.ViewsShare)) return (null, NoRightToShare(access));
        if (!body.Shared && row.Shared && row.OwnerId != me)
            return (null, new(Conflict,
                $"Представление «{row.Title}» осталось общим: вернуть его в свои может только автор. " +
                "Чужое общее представление можно править и удалить; чтобы настройка стала вашей, сохраните её как своё."));

        if (await GroupProblemAsync(gate, me, body.Shared, title, row, ct) is { } taken) return (null, taken);

        var (wasShared, before) = (row.Shared, $"{row.Title}: {row.State}");
        row.Change(title, body.Shared, SavedTableViewRules.Store(state));

        // В журнал — всё, что было или стало общим: личное меняет экран одному человеку.
        var action = (wasShared, row.Shared) switch
        {
            (false, true) => ActivityActions.TableViewShared,
            (true, false) => ActivityActions.TableViewUnshared,
            (true, true) => ActivityActions.TableViewChanged,
            _ => null,
        };
        var failed = await SaveAsync(action is null
            ? () => db.SaveChangesAsync(ct)
            : () => journal.RecordAsync(action, row.Id.ToString(), Label(gate, title), before, $"{title}: {row.State}", ct), title);
        if (failed is not null) return (null, failed);

        var author = row.OwnerId is { } id
            ? await db.Users.AsNoTracking().Where(u => u.Id == id)
                .Select(u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        return (Dto(row, access, author), null);
    }

    public async Task<TableRefusal?> DeleteAsync(
        string address, string view, string seen, DataAccess access, CancellationToken ct)
    {
        var (gate, denied) = await WritableGateAsync(address, access, ct);
        if (gate is null) return denied;

        var (row, refusal) = await FindWritableAsync(gate, view, seen, access, ct);
        if (row is null) return refusal;

        db.SavedTableViews.Remove(row);
        return await SaveAsync(row.Shared
            ? () => journal.RecordAsync(ActivityActions.TableViewDeleted, row.Id.ToString(), Label(gate, row.Title),
                before: row.State, ct: ct)
            : () => db.SaveChangesAsync(ct), row.Title);
    }

    /// <summary>Ворота записи: таблица открыта и её модуль включён.</summary>
    private async Task<(TableGate? Gate, TableRefusal? Refusal)> WritableGateAsync(
        string address, DataAccess access, CancellationToken ct)
    {
        var (gate, denied) = await tables.GateAsync(address, access, ct);
        if (gate is null) return (null, denied);
        return gate.Off is null
            ? (gate, null)
            : (null, new(Conflict,
                $"Представление не изменено: {gate.Off}. Сохранённые представления выключенного модуля скрыты, " +
                "но не удалены, — они вернутся, когда модуль включат."));
    }

    /// <summary>Представление, которое спрашивающий вправе менять, под версией, которую он видел.</summary>
    private async Task<(SavedTableView? Row, TableRefusal? Refusal)> FindWritableAsync(
        TableGate gate, string view, string seen, DataAccess access, CancellationToken ct)
    {
        if (!Guid.TryParse(view, out var id))
        {
            var locked = gate.ModuleViews.FirstOrDefault(v => string.Equals(v.Code, view, StringComparison.OrdinalIgnoreCase));
            return (null, locked is null
                ? Gone(gate)
                : new(Conflict,
                    $"Представление «{locked.Title}» поставляет модуль, и оно заперто: править и удалять его на " +
                    "месте нельзя. Сохраните свою настройку как своё представление."));
        }

        var row = await db.SavedTableViews.FirstOrDefaultAsync(v => v.Id == id && v.TableAddress == gate.Address, ct);
        if (row is null || (!row.Shared && row.OwnerId != access.UserId)) return (null, Gone(gate));

        if (row.Shared && !access.Allows(CorePermissions.ViewsShare))
            return (null, new(Forbidden,
                $"Общее представление «{row.Title}» правит и удаляет тот, у кого есть право " +
                $"«{CorePermissions.ViewsShare}», а у «{access.Who}» его нет. Сохраните свою настройку как своё представление."));

        return seen == row.RowVersion.ToString() ? (row, null) : (null, Stale(row.Title));
    }

    private static (string Title, TableViewState State, TableRefusal? Refusal) Checked(SaveTableView body)
    {
        var (title, titleProblem) = SavedTableViewRules.Title(body.Title);
        var (state, stateProblem) = SavedTableViewRules.Normalize(body.State);
        return (titleProblem ?? stateProblem) is { } problem
            ? (title, state, new(Invalid, $"Представление не сохранено: {problem}."))
            : (title, state, null);
    }

    /// <summary>
    /// Чем группа, в которую представление ложится, его не принимает: имя занято либо группа полна.
    /// Группы две — общие таблицы и личные одного человека; у каждой свой счёт имён.
    /// </summary>
    /// <param name="self">Правимое представление — себя оно не занимает и в предел не входит.</param>
    private async Task<TableRefusal?> GroupProblemAsync(
        TableGate gate, Guid me, bool shared, string title, SavedTableView? self, CancellationToken ct)
    {
        var selfId = self?.Id;
        var group = db.SavedTableViews.AsNoTracking().Where(v => v.TableAddress == gate.Address && v.Id != selfId);
        group = shared ? group.Where(v => v.Shared) : group.Where(v => !v.Shared && v.OwnerId == me);

        // Сравнение без учёта регистра: «Мои счета» и «мои счета» в одном списке человек не различит.
        var lowered = title.ToLower();
        if (await group.AnyAsync(v => v.Title.ToLower() == lowered, ct)) return Taken(title, shared);

        var max = shared ? SharedMax : OwnMax;
        return await group.CountAsync(ct) >= max
            ? new(Conflict, $"Представление не сохранено: {(shared ? "общих представлений у таблицы" : "своих представлений этой таблицы")} " +
                            $"уже {max} — это предел. Удалите ненужные.")
            : null;
    }

    /// <summary>
    /// Сохранение с переводом отказов базы в ответ человеку: гонку двух правок ловит версия строки,
    /// гонку двух одинаковых имён — уникальный индекс.
    /// </summary>
    private static async Task<TableRefusal?> SaveAsync(Func<Task> save, string title)
    {
        try
        {
            await save();
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale(title);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Taken(title, null);
        }
    }

    private static TableRefusal Gone(TableGate gate) => new(Missing,
        $"Представления нет: оно удалено или больше не общее. Таблицу «{gate.Title}» можно открыть целиком.");

    private static TableRefusal Stale(string title) => new(Conflict,
        $"Представление «{title}» тем временем изменили: правка собрана по прежней версии и затёрла бы чужую. " +
        "Откройте представление заново и повторите.");

    private static TableRefusal Taken(string title, bool? shared) => new(Conflict,
        $"Представление «{title}» уже есть{(shared switch { true => " среди общих", false => " среди ваших", _ => "" })}. Назовите иначе.");

    private static TableRefusal NoRightToShare(DataAccess access) => new(Forbidden,
        $"Представление не сохранено общим: общие представления заводит тот, у кого есть право " +
        $"«{CorePermissions.ViewsShare}», а у «{access.Who}» его нет. Сохраните его своим.");

    private static string Label(TableGate gate, string title) => $"{gate.Title}: {title}";

    private static SavedTableViewDto Dto(SavedTableView row, DataAccess access, string? author) => new(
        row.Id, row.Title, row.Shared, row.OwnerId == access.UserId, author,
        row.Shared ? access.Allows(CorePermissions.ViewsShare) : row.OwnerId == access.UserId,
        row.BasedOn, SavedTableViewRules.Read(row.State), row.RowVersion.ToString());
}
