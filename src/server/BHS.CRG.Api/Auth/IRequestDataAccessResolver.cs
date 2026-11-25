using System.Security.Claims;
using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Api.Auth;

/// <summary>Формирует параметр доступа читающего по принципалу текущего запроса.</summary>
public interface IRequestDataAccessResolver
{
    Task<DataAccess> ForAsync(ClaimsPrincipal principal, CancellationToken ct);
}

internal static class RequestDataAccessExtensions
{
    /// <summary>Инструменты и ресурсы MCP берут принципал из текущего HTTP-запроса.</summary>
    public static Task<DataAccess> ForRequestAsync(
        this IRequestDataAccessResolver access, IHttpContextAccessor http, CancellationToken ct) =>
        access.ForAsync(
            http.HttpContext?.User
            ?? throw new InvalidOperationException(
                "Параметр доступа запрошен вне HTTP-запроса: инструменты и ресурсы MCP берут " +
                "пользователя из принципала, и без него читать строки нечьими правами нельзя."),
            ct);
}
