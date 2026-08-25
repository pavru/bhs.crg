using Microsoft.AspNetCore.Identity;

namespace BHS.CRG.Infrastructure.Persistence;

public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Аватар профиля (issue #245) — data-URI уменьшенной картинки (~256px), null = нет.</summary>
    public string? AvatarDataUri { get; set; }

    /// <summary>
    /// Когда заведена учётная запись. Нужна списку уведомлений: общесистемные, выпущенные ДО
    /// появления пользователя, ему не показываются. Без этого новый сотрудник открывал бы
    /// колокольчик с сотнями непрочитанных «MinIO: восстановлен» из чужого прошлого — раньше это
    /// не всплывало, потому что признак «прочитано» был общим на всех (issue #821).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
