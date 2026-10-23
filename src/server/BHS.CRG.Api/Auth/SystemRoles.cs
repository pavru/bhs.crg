namespace BHS.CRG.Api.Auth;

/// <summary>
/// Системные роли — стартовый набор, который создаётся при установке (ТЗ AUTH-4).
///
/// Без них при первой установке администратору нечего выдать, кроме «Администратора», и любой
/// сотрудник получает полный доступ «пока не разберёмся». Разбираться потом никто не приходит.
///
/// ⚠️ Имя роли ТЕХНИЧЕСКОЕ и латинское, а человеку показывается <see cref="RoleDefinition.Title" />.
/// Причина простая и неприятная: имя роли уходит в токен и в политики (<c>RequireAuthorization("Admin")</c>),
/// а имена в токенах переживают переименования плохо. Поэтому существующие роли <c>Admin</c> и
/// <c>User</c> НЕ переименовываются: <c>Admin</c> — это «Администратор», <c>User</c> — «Инженер ИД»,
/// и у обеих просто появляется состав прав. Перевод прежней роли в новую — не переименование
/// строки, а то, что за ней стоит.
///
/// ⚠️ Удалить системную роль нельзя — но запрета сейчас негде поставить: удаления ролей в API не
/// существует, оно появится вместе с редактором ролей, и проверка встанет там же. Сегодняшняя
/// защита другая и работает уже: роль, удалённая прямо в базе, возвращается при следующем старте
/// вместе со своим составом прав, и это проверено тестом. Заводить непроверяемый запрет заранее
/// значит завести уверенность без защиты.
/// </summary>
public static class SystemRoles
{
    public const string Admin = "Admin";
    public const string IdEngineer = "User";

    /// <summary>
    /// Права на ЧТЕНИЕ справочников ядра, которые получает каждая рабочая роль (решение владельца
    /// 20.09.2026, вариант «А»: у каждого справочника своё право на чтение).
    ///
    /// ⚠️ Выдаются всем восьми ролям, и это следствие выбранного варианта, а не недосмотр: типы,
    /// справочник сущностей, стройки и наборы данных нужны на экранах практически любой роли, и без
    /// них интерфейс не рисует ничего. Разграничивать этими правами сегодня нечего — они существуют
    /// ради единообразия и ради дня, когда учётную запись выдадут кому-то вне компании (issue #675).
    /// До того дня снятая галка здесь ломает экран, а не закрывает данные.
    /// </summary>
    private static readonly string[] ReferenceReads =
    [
        CorePermissions.TypesRead,
        CorePermissions.CatalogRead,
        CorePermissions.ConstructionsRead,
        CorePermissions.DataSetsRead,
    ];

    public static IReadOnlyList<RoleDefinition> All =>
    [
        new(Admin, "Администратор",
            "Все права, включая настройку системы и пользователей",
            Permissions: [], AllPermissions: true),

        new(IdEngineer, "Инженер ИД",
            "Работа с документами и комплектами исполнительной документации",
            [
                "id.document.read", "id.document.edit", "id.document.generate", "id.quality.edit",
                "core.constructions.edit", "core.reconciliation.run", CorePermissions.FilesUse,
                CorePermissions.CatalogEdit, CorePermissions.DataSetsEdit, .. ReferenceReads,
                // Виды работ инженер правил общим адресом под core.catalog.edit; с дверью видов работ
                // (WORK-59) этот адрес их не пишет, и без своего права правка пропала бы молча.
                CorePermissions.WorkTypesEdit,
                // Чужие модули: права появятся вместе с ними, до тех пор строка просто не находит
                // объявления и пропускается.
                "work.facts.read", "costs.materials.read",
            ]),

        new("Installer", "Монтажник",
            "Подача своих отчётов о работах",
            [
                "work.report.own", CorePermissions.FilesUse, .. ReferenceReads,
                // Право контракта (PLAN-23, WORK-59): наименования материалов сметы стройки без цен и
                // количеств — ярус «смета» подсказки в отчёте. Модуль `plan` монтажнику оно не открывает
                // (AUTH-8.1, D11 зонтика plan-00-epic): признак права контракта строит plan-21, объявляет
                // право plan-23 — до тех пор код пропускается при наполнении.
                "plan.estimate.hint",
            ]),

        new("ProjectManager", "Менеджер проекта",
            "Коммерческие предложения и графики, назначение монтажников на свои стройки, приёмка отчётов",
            [
                "core.employees.read", "core.constructions.edit", CorePermissions.PeriodClose, CorePermissions.FilesUse,
                .. ReferenceReads,
                "work.report.review", "work.assign", "work.crew.approve", "work.devices.manage",
                "costs.materials.read", "costs.request.read", "costs.request.edit",
                // Планирование (PLAN-23): КП, прайс-лист и графики — его дело, а не сметчика. Сметы он
                // ЧИТАЕТ — КП и график строятся из редакции сметы (решение В-1, PLAN-Q9, 22.10.2026), — но
                // без plan.estimate.cost: себестоимость видит сметчик (PLAN-2). plan.labor.read — право
                // контракта: нормы чел.-ч для «по смете — фактически» (WORK-42).
                "plan.estimate.read", "plan.offer.read", "plan.offer.edit", "plan.offer.send",
                "plan.pricelist.edit", "plan.schedule.read", "plan.schedule.edit", "plan.labor.read",
            ]),

        new("Estimator", "Сметчик",
            "Сметы и обмен с ГРАНД-Сметой, классификатор видов работ",
            [
                CorePermissions.WorkTypesEdit, "core.nomenclature.edit", CorePermissions.FilesUse, .. ReferenceReads,
                // Сметы и себестоимость (PLAN-23). Коммерческих предложений НЕТ — ни чтения, ни правки:
                // КП — дело «Менеджера проекта», а сметчик без права на КП не видит цен продажи (PLAN-2).
                // plan.offer.edit стоял здесь до plan-01 и молча пропускался, пока право не объявлено;
                // после объявления (plan-13) он выдал бы цены продажи живым сметчикам.
                "plan.estimate.read", "plan.estimate.edit", "plan.estimate.cost",
            ]),

        // «Руководитель» — роль с ОДНИМ составным правом на данные (ТЗ AUTH-5.2), и вторым правом
        // на данные её наделять нельзя. core.files.use — не право на данные, а обиходный доступ к
        // файлам, и без него у роли ломается то, что к сводкам отношения не имеет вовсе: диалог
        // «сообщить об ошибке» грузит снимок экрана ДО отправки, так что отказ съедал не вложение,
        // а всё сообщение целиком (issue #947, найдено ревью).
        //
        // ⚠️ Сначала право здесь не стояло, и в комментарии было написано, что это осознанно. Разбор
        // был неполным: я назвал следствие для ВЫДАЧИ файлов и не посмотрел на загрузку. Сама эта
        // развилка — довод к тому, что files.use вышло неудачной формы (см. CorePermissions).
        new("Executive", "Руководитель",
            "Чтение сводок по всем стройкам без права правки",
            ["*.read.all", CorePermissions.FilesUse, .. ReferenceReads]),

        new("Supplier", "Снабженец",
            "Счета, накладные, разноска, сопоставление наименований, заявки на закупку",
            [
                "core.nomenclature.edit", CorePermissions.FilesUse, .. ReferenceReads,
                "costs.invoice.read", "costs.invoice.edit", "costs.waybill.read", "costs.waybill.edit",
                // Организация из скана счёта (issue #1077): кто вводит счета, тот встречает новых поставщиков.
                "costs.organization.create",
                "costs.allocation.edit", "costs.request.read", "costs.request.edit", "plan.estimate.materials",
            ]),

        new("Accountant", "Бухгалтер",
            "Отметка оплаты, реестр счетов, отчёты по затратам, исправление счетов закрытого периода",
            [
                "core.employees.read", CorePermissions.PeriodClose, CorePermissions.FilesUse, .. ReferenceReads,
                "costs.invoice.read", "costs.invoice.pay", "costs.report.read", "costs.articles.edit",
                // Исправление счёта закрытого периода (E2, issue #1100) — право бухгалтера по решению владельца
                // 15.10.2026. Без правки счетов и разноски оно не работает (исправление пишет и то и другое),
                // поэтому они здесь же (решение владельца 16.10.2026): иначе стандартный «Бухгалтер» не
                // исправил бы ни одного счёта.
                "costs.invoice.correct", "costs.invoice.edit", "costs.allocation.edit",
            ]),

        new("WorksManager", "Производитель работ",
            "Ведение общего журнала работ, выпуск на подпись, отметка подписания",
            ["work.facts.read", "ozhr.record.edit", "ozhr.release", CorePermissions.FilesUse, .. ReferenceReads]),
    ];

    /// <summary>
    /// Право, без которого «Администратор» перестаёт быть администратором: снять его — значит
    /// оставить экземпляр без управления пользователями и без возможности это исправить.
    /// Возвращается при каждом старте, даже если его убрали.
    /// </summary>
    public const string AdminCannotLose = CorePermissions.UsersManage;
}

/// <summary>Системная роль: техническое имя, человеческое название и состав прав.</summary>
/// <param name="Name">Техническое имя — то, что уходит в токен и в политики.</param>
/// <param name="Title">Название для человека.</param>
/// <param name="Summary">Смысл роли — показывается в редакторе ролей рядом с названием.</param>
/// <param name="Permissions">
/// Коды прав. Права модулей, которых в этой сборке нет, перечислять МОЖНО: при наполнении они
/// пропускаются и придут вместе со своим модулем. Иначе состав роли пришлось бы дописывать в двух
/// местах — здесь и в модуле, — и одно из них забылось бы.
/// </param>
/// <param name="AllPermissions">
/// Роль получает всё объявленное. Так живёт «Администратор»: перечисление прав у него разошлось бы
/// со справочником на первом же новом праве.
/// </param>
public sealed record RoleDefinition(
    string Name,
    string Title,
    string Summary,
    IReadOnlyList<string> Permissions,
    bool AllPermissions = false);
