using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BHS.CRG.Api.Auth;

/// <summary>
/// Отдельный token-провайдер для подтверждения / смены email (issue #148) со сроком жизни 24 часа —
/// в отличие от дефолтного (1 час, используется сбросом пароля). Подключается как именованный
/// провайдер «EmailConfirmDP» и назначается на EmailConfirmation/ChangeEmail токены в IdentityOptions.
/// </summary>
public sealed class EmailConfirmTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public EmailConfirmTokenProviderOptions()
    {
        Name = "EmailConfirmDP";
        TokenLifespan = TimeSpan.FromHours(24);
    }
}

public sealed class EmailConfirmTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<EmailConfirmTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<ApplicationUser>> logger)
    : DataProtectorTokenProvider<ApplicationUser>(dataProtectionProvider, options, logger);
