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

        g.MapPost("/register", async (RegisterRequest req,
            UserManager<ApplicationUser> users) =>
        {
            var user = new ApplicationUser { UserName = req.Email, Email = req.Email, DisplayName = req.DisplayName };
            var result = await users.CreateAsync(user, req.Password);
            return result.Succeeded ? Results.Ok() : Results.BadRequest(result.Errors);
        });

        g.MapPost("/login", async (LoginRequest req,
            UserManager<ApplicationUser> users,
            IConfiguration cfg) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is null || !await users.CheckPasswordAsync(user, req.Password))
                return Results.Unauthorized();

            var token = CreateToken(user, cfg);
            return Results.Ok(new { accessToken = token });
        });
    }

    private static string CreateToken(ApplicationUser user, IConfiguration cfg)
    {
        var jwtCfg = cfg.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtCfg["Key"]!));
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email!),
            new Claim("displayName", user.DisplayName),
        };
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
