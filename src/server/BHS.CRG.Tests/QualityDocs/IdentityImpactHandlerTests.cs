using System.Linq.Expressions;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.QualityDocs;

/// <summary>
/// Предупреждение о правке полей-идентификаторов (issue #584): изменение состава ИЛИ порядка
/// компонентов ключа осиротит все заведённые связки разом, и молча — документ выпустится без
/// сертификатов, а ошибки в UI не будет.
/// </summary>
public class IdentityImpactHandlerTests
{
    private sealed class FakeRepo<T>(IReadOnlyList<T> items) : IRepository<T> where T : Domain.Common.Entity
    {
        public Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(items);
        public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> p, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddAsync(T e, CancellationToken ct = default) => throw new NotImplementedException();
        public void Update(T e) => throw new NotImplementedException();
        public void Remove(T e) => throw new NotImplementedException();
        public Task SaveChangesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static DocumentType Material(string schema)
        => DocumentType.Create("Материал", "MAT", DocumentTypeKind.Composite, null, JsonDocument.Parse(schema));

    private const string TwoKeys = """
        { "fields": [
            { "key": "Наименование", "tags": ["identity"] },
            { "key": "Артикул", "tags": ["identity"] },
            { "key": "Кач", "type": "complex", "tags": ["material.qualityDocLink"] } ] }
        """;

    private static IdentityImpactHandler Handler(DocumentType type, int linkCount)
        => new(new FakeRepo<DocumentType>([type]),
            new FakeRepo<MaterialQualityLink>([.. Enumerable.Range(0, linkCount)
                .Select(i => MaterialQualityLink.Create(CatalogScope.System, null, $"ключ-{i}", Guid.NewGuid()))]));

    private static Task<IdentityImpact> Run(DocumentType type, string draft, int linkCount = 3)
        => Handler(type, linkCount).Handle(new IdentityImpactQuery(type.Id, JsonDocument.Parse(draft)), default);

    [Fact]
    public async Task UnchangedSchema_NothingToWarnAbout()
    {
        var impact = await Run(Material(TwoKeys), TwoKeys);
        Assert.False(impact.Changed);
        Assert.Equal(0, impact.AffectedLinks);
    }

    /// <summary>Правка, не касающаяся идентичности (заголовок, новое обычное поле), тревоги не поднимает —
    /// иначе предупреждение обесценилось бы и его перестали бы читать.</summary>
    [Fact]
    public async Task UnrelatedFieldAdded_NotChanged()
    {
        var draft = """
            { "fields": [
                { "key": "Наименование", "tags": ["identity"] },
                { "key": "Артикул", "tags": ["identity"] },
                { "key": "Примечание" },
                { "key": "Кач", "type": "complex", "tags": ["material.qualityDocLink"] } ] }
            """;
        Assert.False((await Run(Material(TwoKeys), draft)).Changed);
    }

    [Fact]
    public async Task IdentityFieldAdded_AllLinksAffected()
    {
        var draft = """
            { "fields": [
                { "key": "Наименование", "tags": ["identity"] },
                { "key": "Артикул", "tags": ["identity"] },
                { "key": "Производитель", "tags": ["identity"] },
                { "key": "Кач", "type": "complex", "tags": ["material.qualityDocLink"] } ] }
            """;
        var impact = await Run(Material(TwoKeys), draft);
        Assert.True(impact.Changed);
        Assert.Equal(["Наименование", "Артикул"], impact.Before);
        Assert.Equal(["Наименование", "Артикул", "Производитель"], impact.After);
        Assert.Equal(3, impact.AffectedLinks);   // осиротеют ВСЕ: ключ у материалов общий
    }

    [Fact]
    public async Task TagRemoved_AllLinksAffected()
    {
        var draft = """
            { "fields": [
                { "key": "Наименование", "tags": ["identity"] },
                { "key": "Артикул" },
                { "key": "Кач", "type": "complex", "tags": ["material.qualityDocLink"] } ] }
            """;
        var impact = await Run(Material(TwoKeys), draft);
        Assert.True(impact.Changed);
        Assert.Equal(["Наименование"], impact.After);
    }

    /// <summary>Перенумерация состав не меняет, но меняет ПОРЯДОК склейки — то есть все ключи.</summary>
    [Fact]
    public async Task Renumbering_ChangesKeysToo()
    {
        var draft = """
            { "fields": [
                { "key": "Наименование", "tags": ["identity:2"] },
                { "key": "Артикул", "tags": ["identity:1"] },
                { "key": "Кач", "type": "complex", "tags": ["material.qualityDocLink"] } ] }
            """;
        var impact = await Run(Material(TwoKeys), draft);
        Assert.True(impact.Changed);
        Assert.Equal(["Артикул", "Наименование"], impact.After);
    }

    /// <summary>Черновик — ПРОЕКЦИЯ: сохранённая схема типа после расчёта та же, иначе предпросчёт
    /// записал бы несохранённое в базу первым же чужим SaveChanges.</summary>
    [Fact]
    public async Task DraftDoesNotTouchStoredSchema()
    {
        var type = Material(TwoKeys);
        var before = type.Schema.RootElement.GetRawText();
        await Run(type, """{ "fields": [] }""");
        Assert.Equal(before, type.Schema.RootElement.GetRawText());
    }
}
