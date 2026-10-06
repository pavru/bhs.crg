using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Отправить запись справочника в архив или вернуть из него (issue #1185, ТЗ CORE-34.4).
///
/// <para>Отдельная команда, а не поле правки: тело правки — запись «как на форме», и форма, которая
/// о признаке не знает, сняла бы архив обычным сохранением.</para>
/// </summary>
public record SetRecordArchiveCommand(Guid Id, bool Archived) : IRequest<RecordArchiveResult>;

/// <param name="Changed">
/// Состояние действительно сменилось. <c>false</c> — запись уже была в нужном состоянии: повтор
/// (двойное нажатие, вторая вкладка) — не ошибка, но и не событие, в журнал он не идёт.
/// </param>
public record RecordArchiveResult(Guid Id, string DisplayName, bool Archived, bool Changed);

/// <summary>
/// Можно ли предложить архив вместо удаления (issue #1185). Отказ в удалении занятой записи несёт
/// ответ полем, а не словами в тексте: разбирать фразу клиент не должен, а предложить действие, у
/// которого нет пути (запись уже в архиве, профиль уровня, справочник модуля), — значит обмануть.
/// </summary>
public record CanArchiveRecordQuery(Guid Id) : IRequest<bool>;

public class RecordArchiveHandlers(
    IRepository<DomainObject> repo,
    IRepository<DocumentType> types,
    IRepository<WorkPlanItem> plan,
    IRecordArchive archive,
    IActivityLog journal) :
    IRequestHandler<SetRecordArchiveCommand, RecordArchiveResult>,
    IRequestHandler<CanArchiveRecordQuery, bool>
{
    public async Task<RecordArchiveResult> Handle(SetRecordArchiveCommand cmd, CancellationToken ct)
    {
        // Читаем ради названия и типа — они от архива не зависят. Сам признак здесь НЕ смотрим:
        // «уже в архиве», «документ» и «профиль» решает условное обновление в службе, иначе между
        // проверкой и записью осталась бы щель.
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        var type = await types.GetByIdAsync(entry.CompositeTypeId, ct);
        // Владельца спрашиваем только на пути В архив. Вернуть можно любую запись (ревью PR #1226):
        // тип мог перейти к модулю уже после архива, признак мог приехать копией — и запись, которую
        // нечем вернуть, осталась бы в архиве навсегда. Снять признак — всегда благо.
        if (cmd.Archived) EnsureCoreType(type);

        switch (await archive.SetAsync(cmd.Id, cmd.Archived, ct))
        {
            case ArchiveOutcome.Changed:
                // Журнал — после действия и только когда оно что-то изменило (см. IActivityLog). Это
                // первая запись журнала о данных справочника: «кто и когда убрал из выбора» иначе
                // спросить не у кого — отдельного поля «кто» у записи нет нарочно.
                //
                // ⚠️ Без токена отмены запроса (ревью PR #1226): признак уже записан своим
                // обновлением, и оборванный запрос — человек ушёл со страницы — не должен оставить
                // архив без следа. Повтор его не допишет: он вернёт «без изменений». Сбой самой
                // записи журнала этим не закрыт — общей транзакции у действия и журнала нет, и это
                // плата, названная в IActivityLog для всех действий.
                await journal.RecordAsync(
                    cmd.Archived ? ActivityActions.RecordArchived : ActivityActions.RecordUnarchived,
                    entry.Id.ToString(), Label(entry, type), ct: CancellationToken.None);
                return new RecordArchiveResult(entry.Id, entry.DisplayName ?? "", cmd.Archived, true);
            case ArchiveOutcome.Unchanged:
                return new RecordArchiveResult(entry.Id, entry.DisplayName ?? "", cmd.Archived, false);
            case ArchiveOutcome.Document:
                throw new ConflictException(
                    "Документ в архив не отправляется: архив — для записей справочников.");
            case ArchiveOutcome.LevelProfile:
                throw new ConflictException(
                    "Это профиль уровня — в архив он не отправляется: на него опираются документы " +
                    "уровня. Он редактируется на странице «Общие данные» уровня.");
            default:
                // Запись удалили между чтением и обновлением.
                throw new NotFoundException();
        }
    }

    public async Task<bool> Handle(CanArchiveRecordQuery q, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(q.Id, ct);
        if (entry is null) return false;
        var type = await types.GetByIdAsync(entry.CompositeTypeId, ct);
        if (!IsCoreType(type) || !await archive.AllowsAsync(q.Id, ct)) return false;
        // Запись держит перечень работ — архив рядом с отказом не предлагаем (ревью PR #1226): тот
        // отказ сам говорит «сообщите о находке, запись останется на месте», и кнопка под ним была
        // бы вторым, противоположным указанием. Само действие при этом не запрещено.
        return (await plan.FindAsync(p => p.WorkTypeId == q.Id || p.UnitId == q.Id, ct)).Count == 0;
    }

    /// <summary>
    /// Справочник модуля общим адресом в архив не уходит. Где его запись предлагается на выбор,
    /// знает только модуль, и событие в журнале называется его словами («статья»), — поэтому путь у
    /// модуля свой, своим портом (<c>IModuleOwnCatalog.SetArchivedAsync</c>) и под своим правом.
    /// </summary>
    private static void EnsureCoreType(DocumentType? type)
    {
        if (IsCoreType(type)) return;
        throw new ConflictException(
            $"Записи справочника «{type!.Name}» ведёт модуль «{type.Module}» — общим путём в архив " +
            "они не отправляются. Отправьте запись в архив там, где модуль ведёт этот справочник.");
    }

    // Запись без типа (тип удалён) считаем записью ядра: модуль свой тип не удаляет, а отказать
    // здесь значило бы оставить осиротевшую запись в выборе навсегда.
    private static bool IsCoreType(DocumentType? type) => type is null || TypeOwner.IsCore(type.Module);

    private static string Label(DomainObject entry, DocumentType? type) =>
        type is null ? entry.DisplayName ?? "" : $"{type.Name}: {entry.DisplayName}";
}
