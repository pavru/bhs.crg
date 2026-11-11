using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Завести справочник сотрудников действием, а не перезапуском (ТЗ WORK-47, CORE-7; change
/// <c>work-01-employee-account</c>, выбор владельца по макету Р1).
///
/// <para><b>Зачем.</b> «Сотрудник» производен от «Персоны» и заводится проекцией при старте
/// (<see cref="CoreRecordTypes.Employee" />, <c>SkipWhenBlocked</c>). «Персону» заводит человек, и
/// заведённая после старта, она давала справочник только при следующем запуске, а перезапуск делает
/// оператор поставки, а не администратор заказчика. Кнопка «Завести» на пустом экране сотрудников —
/// выход без него.</para>
///
/// <para>⚠️ <b>Только «завести, если нет».</b> Проекция пишет схему мимо политики правки
/// администратора (F2), и поэтому адреса у неё нет (сторож <c>ModuleRecordTypeTests</c>). Здесь она
/// зовётся с ОДНИМ объявлением из кода и ТОЛЬКО когда типа нет: создание нового типа ничьей правки не
/// снимает, а существующий тип эта команда не трогает вовсе — ответ тогда «уже заведён». Перепроекция
/// поверх чужой правки по HTTP по-прежнему невозможна.</para>
///
/// <para>«Персону» команда не заводит: её схему ведёт заказчик (ТЗ TYPE-7.1), и завести её кодом
/// значило бы придумать её за него.</para>
/// </summary>
public sealed record CreateEmployeeDirectoryCommand : IRequest<EmployeeDirectoryOutcome>;

/// <summary>Чем кончилось «Завести».</summary>
/// <param name="TypeId">Тип справочника — новый или уже бывший; <c>null</c> — не заведён.</param>
/// <param name="Created">Завела ли его эта команда.</param>
/// <param name="Refusal">Почему не заведён: <see cref="EmployeeDirectoryOutcome.PersonMissing" /> или
/// <see cref="EmployeeDirectoryOutcome.Blocked" />.</param>
public sealed record EmployeeDirectoryOutcome(Guid? TypeId, bool Created, string? Refusal)
{
    /// <summary>Нет типа «Персона», от которого справочник производен.</summary>
    public const string PersonMissing = "person-missing";

    /// <summary>
    /// «Персона» есть, а справочник не завёлся: код или имя заняты чужим типом. Причину словами пишет
    /// журнал запуска — тем же разбором, что при старте.
    /// </summary>
    public const string Blocked = "blocked";
}

public sealed class CreateEmployeeDirectoryHandler(IRepository<DocumentType> types, IMediator mediator)
    : IRequestHandler<CreateEmployeeDirectoryCommand, EmployeeDirectoryOutcome>
{
    public async Task<EmployeeDirectoryOutcome> Handle(CreateEmployeeDirectoryCommand cmd, CancellationToken ct)
    {
        var all = await types.GetAllAsync(ct);
        if (Find(all, CoreRecordTypes.EmployeeCode) is { } existing)
            // «Уже заведён» — только тот тип, который видит дверь сотрудников: её код, буква в букву
            // (EmployeeEndpoints.Family). Тип того же кода в другом регистре дверь не
            // находит, и ответ «заведён» оставил бы экран пустым с отчётом об успехе. Проецировать
            // поверх него тоже нельзя — это перепроекция существующего типа (F2), поэтому — отказ.
            return existing.Code == CoreRecordTypes.EmployeeCode
                ? new(existing.Id, Created: false, Refusal: null)
                : new(null, Created: false, EmployeeDirectoryOutcome.Blocked);
        if (Find(all, CoreRecordTypes.PersonCode) is null)
            return new(null, Created: false, EmployeeDirectoryOutcome.PersonMissing);

        // Типа нет — значит проекция только создаёт: перепроецировать ей нечего.
        var created = await mediator.Send(new ProjectModuleTypeCommand(CoreRecordTypes.Employee), ct);
        return created is null
            ? new(null, Created: false, EmployeeDirectoryOutcome.Blocked)
            : new(created.Id, Created: true, Refusal: null);
    }

    // Поиск — без учёта регистра: так тип ищет проекция и так код стережёт редактор типов; найденный
    // в другом регистре проекция приняла бы за свой и перепроецировала.
    private static DocumentType? Find(IReadOnlyList<DocumentType> all, string code) =>
        all.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));
}
