namespace BHS.CRG.Tests.Support;

/// <summary>
/// Фабрика, отдающая один и тот же клиент под любым именем. Для тестов служб, которые берут
/// клиент своего сервиса по имени (issue #936), когда сам путь до сервиса тесту не важен.
/// </summary>
public sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
