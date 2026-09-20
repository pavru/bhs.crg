using System.Security.Claims;
using BHS.CRG.Modules;

namespace BHS.CRG.Tests.Support;

/// <summary>
/// Права для тестового хоста, поднимающего модули без учётных записей и базы.
///
/// Существует потому, что ворота модуля обязаны у кого-то спросить о правах, и приложение без
/// ответчика на этот вопрос собираться не должно (см. <c>MapAppModules</c>). Тест, которому права
/// не важны, называет здесь свой ответ вслух — вместо того чтобы получать его по умолчанию и не
/// знать, что именно он проверяет.
/// </summary>
public sealed class StubUserPermissions(params string[] granted) : IUserPermissions
{
    public Task<IReadOnlyCollection<string>> ForAsync(ClaimsPrincipal principal, CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<string>>(granted);
}
