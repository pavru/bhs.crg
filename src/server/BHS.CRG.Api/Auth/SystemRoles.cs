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
                // Чужие модули: права появятся вместе с ними, до тех пор строка просто не находит
                // объявления и пропускается.
                "work.facts.read", "costs.materials.read",
            ]),

        new("Installer", "Монтажник",
            "Подача своих отчётов о работах",
            ["work.report.own", CorePermissions.FilesUse]),

        new("ProjectManager", "Менеджер проекта",
            "Назначение монтажников на свои стройки, приёмка отчётов, графики",
            [
                "core.employees.read", "core.constructions.edit", "core.period.close", CorePermissions.FilesUse,
                "work.report.review", "work.assign", "work.crew.approve", "work.devices.manage",
                "costs.materials.read", "costs.request.read", "costs.request.edit",
            ]),

        new("Estimator", "Сметчик",
            "Сметы и обмен с ГРАНД-Сметой, классификатор видов работ",
            ["core.worktypes.edit", "core.nomenclature.edit", "plan.estimate.edit", "plan.offer.edit",
             CorePermissions.FilesUse]),

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
            ["*.read.all", CorePermissions.FilesUse]),

        new("Supplier", "Снабженец",
            "Счета, накладные, разноска, сопоставление наименований, заявки на закупку",
            [
                "core.nomenclature.edit", CorePermissions.FilesUse,
                "costs.invoice.read", "costs.invoice.edit", "costs.waybill.read", "costs.waybill.edit",
                "costs.allocate", "costs.request.read", "costs.request.edit", "plan.estimate.materials",
            ]),

        new("Accountant", "Бухгалтер",
            "Отметка оплаты, реестр счетов, отчёты по затратам",
            [
                "core.employees.read", "core.period.close", CorePermissions.FilesUse,
                "costs.invoice.read", "costs.invoice.pay", "costs.report", "costs.articles.edit",
            ]),

        new("WorksManager", "Производитель работ",
            "Ведение общего журнала работ, выпуск на подпись, отметка подписания",
            ["work.facts.read", "ozhr.record.edit", "ozhr.release", CorePermissions.FilesUse]),
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
