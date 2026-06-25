using Microsoft.AspNetCore.Identity;

namespace BHS.CRG.Infrastructure.Persistence;

public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
}
