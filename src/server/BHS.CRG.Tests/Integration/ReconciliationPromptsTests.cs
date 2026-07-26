using System.Reflection;
using System.Text.Json;
using BHS.CRG.Api.Mcp;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Промпты сверки (issue #429). Проверяем не формулировки — они будут меняться, — а то, без чего отчёт
/// агента становится вредным: контекст комплекта в тексте и правила работы с данными в обоих промптах.
/// </summary>
[Collection("Integration")]
public class ReconciliationPromptsTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Guid> SeedSetAsync(IMediator m)
    {
        var type = await m.Send(new CreateDocumentTypeCommand(
            "Акт", $"ACT_{Guid.NewGuid():N}"[..12], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")));
        var construction = await m.Send(new CreateConstructionCommand("ДНС ЕЦДМ", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));
        await m.Send(new AddDocumentToSetCommand(set.Id, type.Id));
        return set.Id;
    }

    private static ReconciliationPrompts Prompts(IServiceScope s) =>
        new(s.ServiceProvider.GetRequiredService<IDomainSnapshotService>());

    [Fact]
    public async Task Reconcile_CarriesSetContext_AndDataRules()
    {
        using var scope = fixture.Services.CreateScope();
        var setId = await SeedSetAsync(scope.ServiceProvider.GetRequiredService<IMediator>());

        var text = await Prompts(scope).ReconcileDocumentSetAsync(setId, CancellationToken.None);

        // Без имён стройки и раздела агент начинает с уточняющих вопросов вместо работы.
        Assert.Contains("250701.ЭОМ-1", text);
        Assert.Contains("ЭОМ", text);
        Assert.Contains("ДНС ЕЦДМ", text);
        Assert.Contains(setId.ToString(), text);

        // Сопоставление карты связей со строками источников — то, ради чего #423 и делался.
        Assert.Contains("list_material_quality_links", text);
        Assert.Contains("get_rows", text);
    }

    [Fact]
    public async Task Readiness_TellsToCiteSystemCheck_AndNotToRelease()
    {
        using var scope = fixture.Services.CreateScope();
        var setId = await SeedSetAsync(scope.ServiceProvider.GetRequiredService<IMediator>());

        var text = await Prompts(scope).CheckSetReadinessAsync(setId, CancellationToken.None);

        Assert.Contains("validate_document", text);
        // Промпт готовности не должен провоцировать выпуск: единственный пишущий инструмент вызывается
        // только по отдельной просьбе.
        Assert.Contains("Ничего не выпускай", text);
    }

    /// <summary>
    /// Правила полноты, происхождения и адресации обязаны быть в ОБОИХ промптах: разойдись они —
    /// один отчёт был бы аккуратен, а другой выдумывал при внешне одинаковой постановке задачи.
    /// </summary>
    [Theory]
    [InlineData("truncated")]
    [InlineData("origin")]
    [InlineData("stale")]
    [InlineData("Не выдумывайте")]
    public async Task BothPrompts_CarryDataRules(string fragment)
    {
        using var scope = fixture.Services.CreateScope();
        var setId = await SeedSetAsync(scope.ServiceProvider.GetRequiredService<IMediator>());
        var p = Prompts(scope);

        Assert.Contains(fragment, await p.ReconcileDocumentSetAsync(setId, CancellationToken.None));
        Assert.Contains(fragment, await p.CheckSetReadinessAsync(setId, CancellationToken.None));
    }

    /// <summary>Несуществующий комплект — внятный отказ, а не промпт с пустыми именами.</summary>
    [Fact]
    public async Task MissingSet_Throws()
    {
        using var scope = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<McpException>(
            () => Prompts(scope).ReconcileDocumentSetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>
    /// SDK принимает от промпт-функции только строку, PromptMessage или ChatMessage (и их
    /// последовательности); произвольный тип компилируется и падает уже в рантайме. На ресурсах мы на
    /// этом уже обожглись (#427) — здесь закрываем тем же способом.
    /// </summary>
    [Fact]
    public void PromptMethods_ReturnSupportedType()
    {
        var methods = typeof(ReconciliationPrompts)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() is not null)
            .ToList();

        Assert.NotEmpty(methods);
        foreach (var m in methods)
        {
            var t = m.ReturnType;
            if (t.IsGenericType &&
                (t.GetGenericTypeDefinition() == typeof(Task<>) || t.GetGenericTypeDefinition() == typeof(ValueTask<>)))
                t = t.GetGenericArguments()[0];

            Assert.True(
                t == typeof(string) || t == typeof(PromptMessage) || t == typeof(ChatMessage),
                $"{m.Name} возвращает {t.Name}: SDK примет только string / PromptMessage / ChatMessage.");
        }
    }
}
