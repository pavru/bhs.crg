using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace BHS.CRG.Api.Endpoints.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");

        // Bootstrap: регистрация открыта ТОЛЬКО когда в системе ещё нет пользователей.
        // Первый зарегистрированный становится администратором. Дальше пользователей
        // заводит администратор через /api/users.
        g.MapPost("/register", async (RegisterRequest req,
            UserManager<ApplicationUser> users) =>
        {
            if (users.Users.Any())
                return Results.Problem("Регистрация закрыта. Обратитесь к администратору.", statusCode: 403);

            var user = new ApplicationUser { UserName = req.Email, Email = req.Email, DisplayName = req.DisplayName };
            var result = await users.CreateAsync(user, req.Password);
            if (!result.Succeeded) return Results.BadRequest(result.Errors);
            await users.AddToRoleAsync(user, "Admin");
            return Results.Ok();
        });

        g.MapGet("/registration-open", (UserManager<ApplicationUser> users) =>
            Results.Ok(new { open = !users.Users.Any() }));

        g.MapPost("/login", async (LoginRequest req,
            UserManager<ApplicationUser> users,
            IConfiguration cfg) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is null || !await users.CheckPasswordAsync(user, req.Password))
                return Results.Unauthorized();

            var roles = await users.GetRolesAsync(user);
            var token = CreateToken(user, roles, cfg);
            return Results.Ok(new { accessToken = token });
        });
        // Смена пароля переехала в /api/account/change-password (issue #148).
    }

    private static string CreateToken(ApplicationUser user, IList<string> roles, IConfiguration cfg)
    {
        var jwtCfg = cfg.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtCfg["Key"]!));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email!),
            new("displayName", user.DisplayName),
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

    record RegisterRequest(string Email, string Password, string DisplayName);
    record LoginRequest(string Email, string Password);
}
