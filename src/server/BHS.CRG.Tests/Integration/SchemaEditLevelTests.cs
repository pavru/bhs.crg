using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Уровень правки схемы доезжает до адреса сохранения (issue #956, ТЗ CORE-19, CORE-19.1).
///
/// Сама таблица правил проверена отдельно и целиком (<c>SchemaEditPolicyTests</c>) — здесь
/// проверяется другое: что правило вообще спрашивают, и что отказ доезжает до того, кто правил.
/// Разница не формальная: правило, не подключённое к адресу, зелено в своих тестах и не
/// останавливает ничего.
/// </summary>
[Collection("Integration")]
public class SchemaEditLevelTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string WithModuleField = """
        {"fields":[{"key":"Табельный","title":"Табельный номер","type":"string","origin":"module"}]}
        """;

    private const string RenamedModuleField = """
        {"fields":[{"key":"ТабельныйНомер","title":"Табельный номер","type":"string","origin":"module"}]}
        """;

    /// <summary>«Готово» задачи: попытка переименовать поле модуля отказывает с внятной причиной.</summary>
    [Fact]
    public async Task Переименовать_поле_модуля_в_расширяемом_типе_нельзя()
    {
        var type = await SeedTypeAsync("EXT_RENAME", SchemaEditLevel.Extendable, WithModuleField);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => SendAsync(
            new UpdateDocumentTypeSchemaCommand(type, JsonDocument.Parse(RenamedModuleField))));

        Assert.Contains("Табельный номер", refusal.Message);
        Assert.Contains("опирается", refusal.Message);

        // И правка не доехала: отказ, который «почти сохранил», хуже отсутствия отказа.
        var saved = await SendAsync(new GetDocumentTypeQuery(type));
        Assert.Contains("\"Табельный\"", saved!.Schema.RootElement.GetRawText());
    }

    /// <summary>
    /// Тот же тип, но открытый: правка проходит. Без этой половины тест доказывал бы только то,
    /// что сохранение схемы вообще отказывает, — и остался бы зелёным, запрети мы её всем.
    /// </summary>
    [Fact]
    public async Task В_открытом_типе_то_же_переименование_проходит()
    {
        var type = await SeedTypeAsync("OPEN_RENAME", SchemaEditLevel.Open, WithModuleField);

        var saved = await SendAsync(
            new UpdateDocumentTypeSchemaCommand(type, JsonDocument.Parse(RenamedModuleField)));

        Assert.Contains("ТабельныйНомер", saved.Schema.RootElement.GetRawText());
    }

    // ── Девятая строка таблицы: производный тип ───────────────────────────────

    [Theory]
    [InlineData(SchemaEditLevel.Open, true)]
    [InlineData(SchemaEditLevel.Extendable, true)]
    [InlineData(SchemaEditLevel.Closed, false)]
    public async Task Производный_тип_разрешён_везде_кроме_закрытого(SchemaEditLevel level, bool allowed)
    {
        var parent = await SeedTypeAsync($"PARENT_{level}", level, """{"fields":[]}""");

        var create = () => SendAsync(new CreateDocumentTypeCommand(
            $"Наследник {level}", $"CHILD_{level}", DocumentTypeKind.Composite, parent,
            JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core));

        if (allowed)
        {
            var child = await create();
            Assert.Equal(parent, child.ParentId);
        }
        else
        {
            var refusal = await Assert.ThrowsAsync<ConflictException>(create);
            Assert.Contains("производный", refusal.Message);
        }
    }

    /// <summary>
    /// Последняя строка таблицы, и она же главная: уровень ограничивает ФОРМУ ДАННЫХ, а не печать.
    /// Печатную форму закрытого типа заказчик рисует сам — иначе «закрытый» означало бы «чужой», и
    /// первое же требование к бумаге упиралось бы в выпуск приложения.
    /// </summary>
    [Fact]
    public async Task Печатная_форма_закрытого_типа_сохраняется()
    {
        var type = await SeedTypeAsync("CLOSED_PRINT", SchemaEditLevel.Closed, WithModuleField);

        var template = await SendAsync(new CreateTemplateCommand(type, "Бланк", "#set page()"));

        Assert.Equal(type, template.DocumentTypeId);
    }

    private async Task<Guid> SeedTypeAsync(string code, SchemaEditLevel level, string schema)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Уровень назначается прямо в базе: адреса «сменить уровень» нет и не будет — его
        // объявляет модуль, а модулей, заводящих типы, пока нет.
        var type = DocumentType.Create(code, code, DocumentTypeKind.Document, null,
            JsonDocument.Parse(schema), TypeOwner.Core, TypeVisibility.Shared, editLevel: level);
        db.DocumentTypes.Add(type);
        await db.SaveChangesAsync();
        return type.Id;
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
