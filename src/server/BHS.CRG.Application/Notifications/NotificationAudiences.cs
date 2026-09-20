namespace BHS.CRG.Application.Notifications;

/// <summary>
/// Кому адресуются общесистемные уведомления ядра (ТЗ AUTH-13, CORE-27).
///
/// ⚠️ Коды написаны здесь строками, а не взяты из <c>CorePermissions</c>, и это не небрежность:
/// издатели живут в Infrastructure и Application, а объявление прав — в Api, ссылаться туда снизу
/// вверх нельзя. Чтобы строка не разошлась с объявлением молча, сделаны две проверки:
/// <list type="bullet">
/// <item>публикация с необъявленной аудиторией отказывает (<c>INotificationAudience.EnsureDeclared</c>) —
/// иначе уведомление ушло бы в пустоту, выглядя отправленным;</item>
/// <item>тест сверяет каждое значение отсюда с каталогом прав и реестром модулей.</item>
/// </list>
/// </summary>
public static class NotificationAudiences
{
    /// <summary>Обслуживание системы: обновления, резервные копии, отказы фоновых задач.</summary>
    public const string SystemManage = "core.system.manage";

    /// <summary>Разбор обращений пользователей.</summary>
    public const string SupportReview = "core.support.review";

    /// <summary>Наборы данных и их распознавание.</summary>
    public const string DataSetsEdit = "core.datasets.edit";

    /// <summary>Прогон сверки на непротиворечивость.</summary>
    public const string ReconciliationRun = "core.reconciliation.run";

    /// <summary>Все значения — для сверки с каталогом прав в тесте.</summary>
    public static IReadOnlyList<string> All =>
        [SystemManage, SupportReview, DataSetsEdit, ReconciliationRun];
}
