using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.IdentityModel.Tokens;

namespace BHS.CRG.Api.Auth;

/// <summary>
/// Выпуск access-JWT. Кроме sub/email/displayName/role кладём <c>sstamp</c> — текущий
/// SecurityStamp пользователя (issue #148 follow-up). Он проверяется на каждом запросе
/// (см. JwtBearer OnTokenValidated в Program.cs): сброс/смена пароля меняют стамп → ранее
/// выданные токены становятся недействительными.
/// </summary>
public static class JwtTokens
{
    public const string SecurityStampClaim = "sstamp";

    public static string Create(ApplicationUser user, IList<string> roles, string securityStamp, IConfiguration cfg)
    {
        var jwtCfg = cfg.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtCfg["Key"]!));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email!),
            new("displayName", user.DisplayName),
            new(SecurityStampClaim, securityStamp),
        };
        foreach (var role in roles)
            claims.Add(new Claim("role", role));

        var token = new JwtSecurityToken(
            issuer: jwtCfg["Issuer"],
            audience: jwtCfg["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
