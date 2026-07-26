using System.Text.Json;
using BHS.CRG.Api.Mcp;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Reconciliation;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Журнал замечаний внешнего анализа (issue #440). Замечание — утверждение агента, требующее
/// подтверждения человеком, а НЕ результат проверки системы.
/// </summary>
[Collection("Integration")]
public class AgentObservationsTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ObservationTools Tools(IServiceScope s) => new(
        s.ServiceProvider.GetRequiredService<IMediator>(),
        s.ServiceProvider.GetRequiredService<IHttpContextAccessor>());

    private static JsonElement Refs(string raw = """{"documentIds":["d1"],"note":"акт 5"}""")
        => JsonDocument.Parse(raw).RootElement;

    private static readonly Guid SetId = Guid.NewGuid();

    /// <summary>
    /// Повтор анализа не должен плодить дубли — тот же урок, что стабильный ключ находки (P2 в #414):
    /// без него журнал перестаёт быть памятью.
    /// </summary>
    [Fact]
    public async Task SameKey_UpdatesInsteadOfDuplicating()
    {
        using var scope = fixture.Services.CreateScope();
        var tools = Tools(scope);

        await tools.ReportObservationAsync(SetId, "аоср-5.организация", "Организация не совпадает",
            Refs(), CancellationToken.None);
        var second = await tools.ReportObservationAsync(SetId, "аоср-5.организация",
            "Организация не совпадает с реестром", Refs(), CancellationToken.None,
            detail: "В акте №5 ООО «А», в реестре ООО «Б»", severity: "Error");

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<AgentObservation>>();
        var single = Assert.Single(await repo.FindAsync(o => o.ScopeId == SetId));
        Assert.Equal(second.Id, single.Id);
        Assert.Equal("Организация не совпадает с реестром", single.Title);
        Assert.Equal(ObservationSeverity.Error, single.Severity);
    }

    /// <summary>Утверждение без опоры — мнение, а не находка: проверить его человек не сможет.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task ReferencesAreRequired(string raw)
    {
        using var scope = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<McpException>(() => Tools(scope).ReportObservationAsync(
            SetId, "k", "Что-то не так", Refs(raw), CancellationToken.None));
    }

    [Fact]
    public async Task KeyAndTitleAreRequired()
    {
        using var scope = fixture.Services.CreateScope();
        var tools = Tools(scope);
        await Assert.ThrowsAsync<McpException>(() => tools.ReportObservationAsync(
            SetId, "  ", "Заголовок", Refs(), CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => tools.ReportObservationAsync(
            SetId, "k", " ", Refs(), CancellationToken.None));
    }

    /// <summary>
    /// Разбор человека переживает повторный анализ: агент, прогнав его заново, не должен возвращать в
    /// работу закрытое — иначе журнал теряет память ровно как при нестабильном ключе.
    /// </summary>
    [Fact]
    public async Task HumanReview_SurvivesRepeatedReport()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var tools = Tools(scope);

        var reported = await tools.ReportObservationAsync(
            SetId, "кабель.перерасход", "Перерасход кабеля", Refs(), CancellationToken.None);

        await m.Send(new ReviewObservationCommand(
            reported.Id, ObservationStatus.Rejected, "Согласовано, давальческий", "alex"));

        await tools.ReportObservationAsync(
            SetId, "кабель.перерасход", "Перерасход кабеля", Refs(), CancellationToken.None);

        var again = Assert.Single(await tools.ListObservationsAsync(
            CancellationToken.None, scopeId: SetId));
        Assert.Equal("Rejected", again.Status);
        Assert.Equal("Согласовано, давальческий", again.ReviewNote);
        Assert.Equal("alex", again.ReviewedBy);
    }

    /// <summary>
    /// Модель одинаково охотно шлёт объект и строку с тем же объектом внутри. На рабочих данных
    /// случилось второе — тринадцать замечаний легли со ссылками-строкой, и потребитель, ждавший
    /// объект, не увидел ни одной ссылки: находки стали непроверяемы из-за формата.
    /// </summary>
    [Fact]
    public async Task StringifiedReferences_AreUnwrapped()
    {
        using var scope = fixture.Services.CreateScope();
        var tools = Tools(scope);

        var stringified = JsonDocument.Parse(
            JsonSerializer.Serialize("""{"documentIds":["d1"],"note":"акт 5"}""")).RootElement;

        var reported = await tools.ReportObservationAsync(
            SetId, "строкой", "Ссылки пришли строкой", stringified, CancellationToken.None);

        Assert.Equal(JsonValueKind.Object, reported.References.ValueKind);
        Assert.Equal("d1", reported.References.GetProperty("documentIds")[0].GetString());

        // И на чтении тоже — иначе уже записанные строкой остались бы сломанными.
        var listed = Assert.Single(await tools.ListObservationsAsync(CancellationToken.None, scopeId: SetId));
        Assert.Equal(JsonValueKind.Object, listed.References.ValueKind);
    }

    /// <summary>Строка, не содержащая JSON, — это заметка, а не ссылки: её не выбрасываем.</summary>
    [Fact]
    public async Task PlainTextReferences_AreKept()
    {
        using var scope = fixture.Services.CreateScope();
        var refs = JsonDocument.Parse(JsonSerializer.Serialize("смотрел лист 3 глазами")).RootElement;

        var reported = await Tools(scope).ReportObservationAsync(
            SetId, "текстом", "Ссылка обычным текстом", refs, CancellationToken.None);

        Assert.Equal(JsonValueKind.String, reported.References.ValueKind);
    }

    /// <summary>
    /// Колокольчик — про «пришло что-то важное». Повтор с тем же ключом звонить не должен: это
    /// обновление известного утверждения, и звонок наказывал бы агента за повторный анализ, ради
    /// которого стабильный ключ и вводился (#457).
    /// </summary>
    [Fact]
    public async Task Notification_OnlyForNewAndSevere()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var tools = Tools(scope);
        var user = Guid.NewGuid();

        async Task<int> CountAsync() =>
            (await notifications.GetAsync(user)).Count(n => n.Source == "Сверка");

        await tools.ReportObservationAsync(SetId, "мелочь", "Внимание", Refs(),
            CancellationToken.None, severity: "Warning");
        Assert.Equal(0, await CountAsync()); // рангом ниже — только счётчики

        await tools.ReportObservationAsync(SetId, "важное", "Существенное расхождение", Refs(),
            CancellationToken.None, severity: "Error");
        Assert.Equal(1, await CountAsync());

        await tools.ReportObservationAsync(SetId, "важное", "Существенное расхождение (уточнено)",
            Refs(), CancellationToken.None, severity: "Error");
        Assert.Equal(1, await CountAsync());
    }

    /// <summary>
    /// Отзыв — свидетельство агента ПРОТИВ собственного утверждения, а не самоодобрение (#459).
    /// Замечание при этом не исчезает: закрывает его человек.
    /// </summary>
    [Fact]
    public async Task Retract_MarksRetracted_AndDropsFromCounters()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var tools = Tools(scope);

        await tools.ReportObservationAsync(SetId, "нумерация", "Пропущен подпункт 3.3", Refs(),
            CancellationToken.None);
        Assert.Equal(1, (await m.Send(new GetRelatedProblemsQuery(CatalogScope.Set, SetId))).NeedsAttention);

        var retracted = await tools.RetractObservationAsync(
            SetId, "нумерация", CancellationToken.None, note: "Пробел закрыт, подпункты 3.1–3.3 подряд");

        Assert.Equal("Retracted", retracted.Status);
        // Разбирать больше нечего, а неснимаемый счётчик обесценивает бейдж.
        Assert.Equal(0, (await m.Send(new GetRelatedProblemsQuery(CatalogScope.Set, SetId))).NeedsAttention);
        // Но из журнала оно НЕ исчезло: закрывает человек.
        Assert.Single(await tools.ListObservationsAsync(CancellationToken.None, scopeId: SetId));
    }

    /// <summary>Условие воспроизвелось снова — утверждение снова живо.</summary>
    [Fact]
    public async Task ReportingAgain_ClearsRetraction()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var tools = Tools(scope);

        await tools.ReportObservationAsync(SetId, "нумерация", "Пропущен 3.3", Refs(), CancellationToken.None);
        await tools.RetractObservationAsync(SetId, "нумерация", CancellationToken.None, note: "исправлено");
        await tools.ReportObservationAsync(SetId, "нумерация", "Снова пропущен 3.3", Refs(), CancellationToken.None);

        var again = Assert.Single(await tools.ListObservationsAsync(CancellationToken.None, scopeId: SetId));
        Assert.Equal("New", again.Status);
        Assert.Null(again.ReviewNote);
        Assert.Equal(1, (await m.Send(new GetRelatedProblemsQuery(CatalogScope.Set, SetId))).NeedsAttention);
    }

    /// <summary>
    /// Разбор ЧЕЛОВЕКА повторное сообщение не сбрасывает — в отличие от отзыва, который был словом
    /// агента. Иначе повторный анализ возвращал бы в работу закрытое человеком.
    /// </summary>
    [Fact]
    public async Task HumanVerdict_IsNotClearedByRepeatedReport()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var tools = Tools(scope);

        var o = await tools.ReportObservationAsync(SetId, "к", "Замечание", Refs(), CancellationToken.None);
        await m.Send(new ReviewObservationCommand(o.Id, ObservationStatus.Rejected, "не ошибка", "alex"));
        await tools.ReportObservationAsync(SetId, "к", "Замечание снова", Refs(), CancellationToken.None);

        var again = Assert.Single(await tools.ListObservationsAsync(CancellationToken.None, scopeId: SetId));
        Assert.Equal("Rejected", again.Status);
        Assert.Equal("не ошибка", again.ReviewNote);
    }

    [Fact]
    public async Task Retract_UnknownKey_IsRejectedClearly()
    {
        using var scope = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<McpException>(() =>
            Tools(scope).RetractObservationAsync(SetId, "нет-такого", CancellationToken.None));
    }

    [Fact]
    public async Task List_ShowsUnreviewedAndSevereFirst()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var tools = Tools(scope);

        var closed = await tools.ReportObservationAsync(
            SetId, "a", "Разобранное", Refs(), CancellationToken.None, severity: "Error");
        await m.Send(new ReviewObservationCommand(closed.Id, ObservationStatus.Confirmed, null, "alex"));
        await tools.ReportObservationAsync(SetId, "b", "Мелочь", Refs(), CancellationToken.None, severity: "Info");
        await tools.ReportObservationAsync(SetId, "c", "Важное", Refs(), CancellationToken.None, severity: "Error");

        var list = await tools.ListObservationsAsync(CancellationToken.None, scopeId: SetId);

        // Журнал читают сверху вниз: неразобранное выше разобранного, существенное выше мелочи.
        Assert.Equal(["Важное", "Мелочь", "Разобранное"], list.Select(o => o.Title));
    }

    [Fact]
    public async Task Filter_ByStatus()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var tools = Tools(scope);

        var one = await tools.ReportObservationAsync(SetId, "a", "Первое", Refs(), CancellationToken.None);
        await tools.ReportObservationAsync(SetId, "b", "Второе", Refs(), CancellationToken.None);
        await m.Send(new ReviewObservationCommand(one.Id, ObservationStatus.Confirmed, null, "alex"));

        var open = await tools.ListObservationsAsync(CancellationToken.None, scopeId: SetId, status: "New");
        Assert.Equal("Второе", Assert.Single(open).Title);
    }
}
