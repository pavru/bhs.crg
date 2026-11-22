using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Photos;
using BHS.CRG.Modules.Work.Review;
using BHS.CRG.Tests.Integration;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Перепись пишущих адресов отчёта (ТЗ AUTH-8, AUTH-9, MOB-15; <c>work-17-photos</c>): каждый адрес, который меняет
/// отчёт, требует версию заголовком <c>If-Match</c> — без него 400 со словами, где её взять. Приём
/// <c>InvoiceStaleFormTests</c>: адреса берутся из живого приложения, и новый пишущий адрес попадает сюда сам.
///
/// <para>Загрузка фото — без версии. У остальных именованных исключений версия обязательна после проверки
/// охвата и периода. Порядок и требование версии проверяют ReportClosedPeriodTests и ReportCorrectionTests.</para>
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReportVersionCensusTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly ReportWorld world = new(host);

    /// <summary>Пишущие адреса отчёта, которые версию не спрашивают, — с причиной. Ключ — «МЕТОД путь».</summary>
    private static readonly Dictionary<string, string> WithoutVersion = new()
    {
        [$"PUT {PhotoEndpoints.LinePath}"] =
            "загрузка фото строки (work-17): фото догоняют отчёт отдельной очередью (WORK-34), и версия отчёта к их " +
            "приходу сдвинута подачей, правкой менеджера или приёмкой. Загрузка ничего не перезаписывает: " +
            "идентификатор от клиента, повтор отвечает тем же, занятый другой строкой — 409",
    };

    private static readonly Dictionary<string, string> VersionAfterNeed = new()
    {
        [$"PUT {ReportEndpoints.Path}/{{id:guid}}"] = "свой отчёт: сначала охват и открытый период",
        [$"DELETE {ReportEndpoints.Path}/{{id:guid}}"] = "свой отчёт: сначала охват и открытый период",
        [$"POST {ReportEndpoints.Path}/{{id:guid}}/submit"] = "свой отчёт: сначала охват и открытый период",
        [$"PUT {EntryReportEndpoints.Path}/{{id:guid}}"] = "отчёт стройки менеджера: сначала охват и открытый период",
        [$"POST {EntryReportEndpoints.Path}/{{id:guid}}/submit"] = "отчёт стройки менеджера: сначала охват и открытый период",
        [$"POST {ReportReviewEndpoints.AcceptPath}"] = "приёмка: сначала охват и открытый период",
        [$"POST {ReportReviewEndpoints.ReturnPath}"] = "возврат: сначала охват и открытый период",
        [$"DELETE {PhotoEndpoints.LinePath}"] = "снятие фото: сначала видимость строки и открытый период",
        [$"POST {ReportCorrection.Path}"] = "исправление: сначала охват и необходимость исправления в закрытом периоде",
    };

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Пишущий_адрес_отчёта_без_версии_отвечает_400()
    {
        var routes = WritingRoutes();
        Assert.NotEmpty(routes);
        var employee = await world.EmployeeAsync("Ревизоров Роман Романович");
        var (userId, client) = await world.Assignments.UserAsync("Ревизоров Роман Романович",
            WorkModule.ReportOwn, WorkModule.ReportReview);
        await world.Assignments.LinkAsync(employee, userId);

        var silent = new List<string>();
        foreach (var (method, pattern) in routes.Where(r => !VersionAfterNeed.ContainsKey($"{r.Method} {r.Pattern}")))
        {
            var url = Regex.Replace(pattern, @"\{[^}]+\}", _ => Guid.NewGuid().ToString());
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method != "DELETE") request.Content = JsonContent.Create(new { });
            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.BadRequest || !text.Contains("If-Match"))
                silent.Add($"{method} {pattern} → {(int)response.StatusCode} {text}");
        }

        Assert.True(silent.Count == 0,
            "Пишущие адреса отчёта приняли запрос без версии (If-Match):\n" + string.Join("\n", silent) + "\n\n" +
            "Правка без версии записалась бы поверх чужой. Спросите версию первой (SeenVersions.Required) — или впишите " +
            "адрес в WithoutVersion или VersionAfterNeed с причиной и проверкой порядка отказов.");
    }

    /// <summary>Исключение называет живой адрес: переименованный адрес не прячет за собой забытый.</summary>
    [Fact]
    public void Исключения_называют_живые_адреса()
    {
        _ = host.CreateClient();
        var live = Endpoints().Select(e => $"{e.Method} {e.Pattern}").ToHashSet();
        Assert.All(WithoutVersion.Keys.Concat(VersionAfterNeed.Keys), key => Assert.Contains(key, live));
    }

    private List<(string Method, string Pattern)> WritingRoutes()
    {
        _ = host.CreateClient();
        return
        [
            .. Endpoints().Where(e => e.Method != "GET"
                                      && (e.Pattern.StartsWith(ReportEndpoints.Path + "/{id:guid}", StringComparison.Ordinal)
                                          || e.Pattern.StartsWith(EntryReportEndpoints.Path + "/{id:guid}", StringComparison.Ordinal))
                                      && !WithoutVersion.ContainsKey($"{e.Method} {e.Pattern}")),
        ];
    }

    private IEnumerable<(string Method, string Pattern)> Endpoints() =>
        host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(route => (route.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (method, "/" + route.RoutePattern.RawText?.Trim('/'))));
}
