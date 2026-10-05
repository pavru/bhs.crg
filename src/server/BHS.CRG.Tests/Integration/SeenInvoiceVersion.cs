using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Клиент теста называет версию счёта так, как её назвала бы форма, только что перечитавшая счёт
/// (issue #1176).
///
/// <para>Правка счёта обязана назвать версию, по которой собрана (заголовок <c>If-Match</c>), иначе —
/// отказ. Сотни проверок о версии не думают: им нужна «форма, которая видит свежее». Этот обработчик
/// и есть такая форма: перед правкой читает счёт тем же пользователем и подставляет его версию.</para>
///
/// <para>⚠️ <b>Заголовок, названный самим тестом, не трогается</b> — так проверяются устаревшая и
/// пропущенная версии. Пропуск называют <see cref="Omit" />: молча «не подставить» обработчик не
/// умеет, и это намеренно — иначе проверку «без версии отказ» было бы нечем отличить от забытой.</para>
/// </summary>
public sealed partial class SeenInvoiceVersion : DelegatingHandler
{
    public const string Header = "If-Match";

    /// <summary>Значение заголовка, означающее «версию не называть»: обработчик заголовок снимает.</summary>
    public const string Omit = "-";

    /// <summary>
    /// Клиент фабрики с этим обработчиком — тем же набором, что даёт её <c>CreateClient()</c> (переходы и
    /// куки). Для фабрики, полученной через <c>WithWebHostBuilder</c>: сокрытый метод фикстуры ей не достаётся.
    /// </summary>
    public static HttpClient ClientOf(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) =>
        factory.CreateDefaultClient(
            new Microsoft.AspNetCore.Mvc.Testing.Handlers.RedirectHandler(),
            new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler(),
            new SeenInvoiceVersion());

    [GeneratedRegex(@"^/api/costs/invoices/(?<id>[0-9a-fA-F-]{36})(/|$)")]
    private static partial Regex InvoicePath();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.TryGetValues(Header, out var named))
        {
            if (named.SingleOrDefault() == Omit) request.Headers.Remove(Header);
            return await base.SendAsync(request, ct);
        }

        var match = InvoicePath().Match(request.RequestUri?.AbsolutePath ?? "");
        if (request.Method == HttpMethod.Get || !match.Success) return await base.SendAsync(request, ct);

        using var read = new HttpRequestMessage(HttpMethod.Get,
            new Uri(request.RequestUri!, $"/api/costs/invoices/{match.Groups["id"].Value}"));
        read.Headers.Authorization = request.Headers.Authorization;
        using var seen = await base.SendAsync(read, ct);

        // Счёт не прочитался (нет права, нет счёта) — версию взять неоткуда, и запрос уходит без неё:
        // свой отказ сервер назовёт сам, а подменять его здесь значило бы прятать то, что тест проверяет.
        if (seen.IsSuccessStatusCode)
        {
            var version = (await seen.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("version").GetString()!;
            request.Headers.TryAddWithoutValidation(Header, version);
        }

        return await base.SendAsync(request, ct);
    }
}
