using Microsoft.AspNetCore.Identity;

namespace BHS.CRG.Infrastructure.Persistence;

public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Аватар профиля (issue #245) — data-URI уменьшенной картинки (~256px), null = нет.</summary>
    public string? AvatarDataUri { get; set; }
}
