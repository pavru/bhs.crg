using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Modules;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Каждая аудитория уведомлений названа ОБЪЯВЛЕННЫМ правом (issue #949, ТЗ AUTH-13).
///
/// Сторож нужен потому, что коды в <see cref="NotificationAudiences" /> написаны строками: издатели
/// живут ниже по стеку, чем объявление прав, и сослаться на <see cref="CorePermissions" /> оттуда
/// нельзя. Опечатка или переименованное право адресуют уведомление никому — и это худший вид
/// поломки: отправка выглядит удавшейся, а в списке записи нет ни у кого.
///
/// ⚠️ Проверка сверяет с каталогом ПРАВ ядра, а не с модулями: аудиторию-модуль называет сам
/// модуль, её сверяет включённый реестр в <c>PermissionAudience.EnsureDeclared</c>.
/// </summary>
public class NotificationAudienceCatalogTests
{
    [Fact]
    public void Каждая_аудитория_ядра_объявлена_правом()
    {
        var catalog = new PermissionCatalog(CorePermissions.All);

        var unknown = NotificationAudiences.All.Where(a => !catalog.Declares(a)).ToList();

        Assert.True(unknown.Count == 0,
            "Уведомления адресованы правам, которых нет в каталоге: " + string.Join(", ", unknown) +
            ". Такое уведомление не получит никто, а публикация будет выглядеть удавшейся.");
    }
}
