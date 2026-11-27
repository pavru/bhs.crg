using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Work;

/// <summary>
/// Колонки модуля, в которых лежат идентификаторы записей ядра (ТЗ CORE-34.1; changes <c>work-11-assignments</c>,
/// <c>work-13-report-model</c>, <c>work-14-report-journal</c>).
///
/// <para>Список — не то, чем ядро НАХОДИТ держателей: оно сканирует схему само, и колонка, забытая здесь,
/// удержит запись всё равно. Отсюда берутся слова отказа («Учёт работ: назначения — 3») вместо адреса
/// таблицы. Полноту стережёт <c>ModuleReferenceInventoryTests</c>.</para>
///
/// <para>Назначение держит и сотрудника, и стройку: снятое молча, оно оставило бы монтажнику отчёты по
/// стройке, которой нет, а менеджеру — «свою» стройку без записи. Сотрудника с назначениями отправляют в
/// архив (CORE-34.4): новое назначение на архивного отвергается, существующие остаются.</para>
/// </summary>
public static class WorkReferences
{
    public static IReadOnlyList<ModuleReference> All =>
    [
        ModuleReference.Remembering("device_invitations", "EmployeeId", ReferenceTarget.Record,
            "приглашение можно погасить только для существующего сотрудника; история приглашений сохраняется"),
        ModuleReference.Remembering("device_sessions", "EmployeeId", ReferenceTarget.Record,
            "полевой доступ требует существующего сотрудника; запись устройства хранит историю отзыва"),
        ModuleReference.Remembering("device_sessions", "RevokedBy", ReferenceTarget.User,
            "идентификатор автора отзыва; удаление учётной записи не меняет историю"),

        ModuleReference.Holding("assignments", "employee_id", ReferenceTarget.Record, "назначения"),
        ModuleReference.Holding("assignments", "construction_id", ReferenceTarget.Construction,
            "назначения на эту стройку"),

        // Отчёт монтажника и его строки (work-13). Новая ссылка на архивную запись отвергается
        // (ReportReferenceCheck, CORE-34.4), уже стоящая — остаётся.
        ModuleReference.Holding("reports", "employee_id", ReferenceTarget.Record, "отчёты монтажников"),
        ModuleReference.Holding("reports", "construction_id", ReferenceTarget.Construction,
            "отчёты монтажников по этой стройке"),
        ModuleReference.Holding("report_lines", "work_type_id", ReferenceTarget.Record, "строки отчётов монтажников"),
        ModuleReference.Holding("report_lines", "variant_id", ReferenceTarget.Record,
            "строки отчётов монтажников с этим вариантом"),
        ModuleReference.Holding("report_lines", "unit_id", ReferenceTarget.Record,
            "строки отчётов монтажников с этой единицей"),
        ModuleReference.Holding("report_lines", "section_id", ReferenceTarget.Section,
            "строки отчётов монтажников по этому разделу"),
        ModuleReference.Holding("report_lines", "work_plan_item_id", ReferenceTarget.WorkPlanItem,
            "строки отчётов монтажников по этой позиции"),
        // Сведения строки — по схеме типа «СтрокаРаботыСведения»: поле заказчика может сослаться на запись
        // ядра, и цели в JSON разные.
        ModuleReference.Holding("report_lines", "details", target: null, "сведения строк отчётов монтажников"),
        ModuleReference.Holding("report_line_materials", "nomenclature_id", ReferenceTarget.Record,
            "материалы в отчётах монтажников"),
        ModuleReference.Holding("report_line_participants", "employee_id", ReferenceTarget.Record,
            "участие в бригадных работах отчётов монтажников"),

        ModuleReference.Holding("crew_groups", "construction_id", ReferenceTarget.Construction, "группы сверки бригад"),
        ModuleReference.Holding("crew_groups", "work_type_id", ReferenceTarget.Record, "группы сверки по этому виду работ"),
        ModuleReference.Holding("crew_groups", "unit_id", ReferenceTarget.Record, "итоги сверки бригад в этой единице"),
        ModuleReference.Remembering("crew_groups", "approved_by", ReferenceTarget.User,
            "автор утверждения итогового объёма с именем снимком"),
        ModuleReference.Remembering("crew_groups", "input_revision", target: null, "версия состава и строк группы"),
        ModuleReference.Remembering("crew_groups", "approved_revision", target: null, "версия состава и строк при утверждении"),

        // Протокол изменений — узкая запись для повтора и курсора: держит отчёт сам, а после удаления черновика
        // запись остаётся нарочно — по ней клиент узнаёт об удалении.
        ModuleReference.Remembering("report_changes", "report_id", target: null,
            "свой отчёт модуля; удалённый черновик остаётся в протоколе — по записи клиент узнаёт об удалении"),
        ModuleReference.Remembering("report_changes", "change_id", target: null,
            "идентификатор изменения от клиента — ключ повтора, а не ссылка"),
        ModuleReference.Remembering("report_changes", "employee_id", ReferenceTarget.Record,
            "копия сотрудника отчёта для курсора «свои изменения»; держит сотрудника сам отчёт"),
        ModuleReference.Remembering("report_changes", "construction_id", ReferenceTarget.Construction,
            "копия стройки отчёта для курсора по стройкам; держит стройку сам отчёт"),
        // Автор ввода за сотрудника (WORK-31): имя хранится снимком рядом, удаление учётной записи отчёта не касается.
        ModuleReference.Remembering("reports", "entered_by_user_id", ReferenceTarget.User,
            "кто ввёл отчёт за сотрудника — справочное поле с именем снимком; удаление учётной записи отчёта не касается"),

        // Незаполненное к автоподаче (work-15): номер и идентификатор строки этого же отчёта и код поля, а не ссылки.
        ModuleReference.Remembering("reports", "required_missing", target: null,
            "что не заполнено к автоподаче — строки этого же отчёта и коды полей, а не ссылки на записи"),

        // Журнал отчёта (work-14) — свидетельство, а не держатель: только дописывается, имена — снимком, и после
        // удаления черновика или записи ядра запись остаётся такой, какой была (WORK-12). Держит записи сам отчёт.
        ModuleReference.Remembering("report_journal", "report_id", target: null,
            "свой отчёт модуля; удалённый черновик остаётся в журнале записью «удалён»"),
        ModuleReference.Remembering("report_journal", "change_id", target: null,
            "идентификатор изменения от клиента — тот же, что у протокола, а не ссылка"),
        ModuleReference.Remembering("report_journal", "author_id", ReferenceTarget.User,
            "автор записи журнала — с именем снимком; удаление учётной записи журнал не меняет"),
        ModuleReference.Remembering("report_journal", "executor_employee_id", ReferenceTarget.Record,
            "исполнитель — сотрудник отчёта с именем снимком; держит сотрудника сам отчёт"),
        ModuleReference.Remembering("report_journal", "changes", target: null,
            "прежние и новые значения изменённых полей — снимок, а не ссылки; действующие ссылки держат отчёт и строки"),

        ModuleReference.Remembering("report_versions", "author_id", ReferenceTarget.User,
            "автор исправления с именем снимком; удаление учётной записи не меняет историю"),
        ModuleReference.Remembering("report_versions", "closing_id", target: null,
            "идентификатор неизменяемой записи о закрытии дня"),
        ModuleReference.Remembering("report_versions", "content", target: null,
            "снимок отчёта и строк до исправления; действующие ссылки хранятся в отчёте и строках"),
        ModuleReference.Remembering("line_photos", "declared_in_closing_id", target: null,
            "неизменяемое закрытие, в котором исправление объявило фото"),
        ModuleReference.Remembering("line_photos", "stored_in_closing_id", target: null,
            "неизменяемое закрытие, в котором получен файл фото"),
    ];
}
