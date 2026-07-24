using System.Text.Json;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>Общий применитель маппинга (issue #374): колонка/@@inline/@@ref-делегат + парсер @@inline.</summary>
public class DataSetMappingApplierTests
{
    private static readonly Guid TypeId = Guid.NewGuid();

    private static string Inline(Dictionary<string, string> fields)
        => DataSetMappingValue.InlinePrefix + JsonSerializer.Serialize(new DataSetInlineMapping(TypeId, fields));

    // Фейковый @@ref-резолвер: помечает, что делегат вызван, значением колонки.
    private static Task<object?> FakeRef(DataSetRefMapping rm, IReadOnlyDictionary<string, string?> row, string path, CancellationToken ct)
        => Task.FromResult<object?>($"REF:{(rm.Column is not null && row.TryGetValue(rm.Column, out var v) ? v : null)}");

    private static Task<object?> Apply(string token, Dictionary<string, string?> row)
        => DataSetMappingApplier.ApplyAsync(token, row, FakeRef, "path", default);

    // ── ParseInline ────────────────────────────────────────────────────────────

    [Fact]
    public void ParseInline_ValidToken_Parsed()
    {
        var map = DataSetMappingValue.ParseInline(Inline(new() { ["Наим"] = "КолНаим" }));
        Assert.NotNull(map);
        Assert.Equal(TypeId, map!.TypeId);
        Assert.Equal("КолНаим", map.Fields["Наим"]);
    }

    [Fact]
    public void ParseInline_EmptyFieldsOrNonInline_Null()
    {
        Assert.Null(DataSetMappingValue.ParseInline(Inline(new())));       // нет под-полей
        Assert.Null(DataSetMappingValue.ParseInline("КолонкаНаим"));       // обычная колонка
        Assert.Null(DataSetMappingValue.ParseInline("@@ref:{}"));          // это ref, не inline
    }

    // ── ApplyAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_PlainColumn_ReturnsCellValue()
    {
        var v = await Apply("Цена", new() { ["Цена"] = "100" });
        Assert.Equal("100", v);
    }

    [Fact]
    public async Task Apply_Inline_BuildsObjectFromColumns()
    {
        var token = Inline(new() { ["Наименование"] = "КолНаим", ["Код"] = "КолКод" });
        var v = await Apply(token, new() { ["КолНаим"] = "ООО Ромашка", ["КолКод"] = "7701" });

        var obj = Assert.IsType<Dictionary<string, object?>>(v);
        Assert.Equal("ООО Ромашка", obj["Наименование"]);
        Assert.Equal("7701", obj["Код"]);
    }

    [Fact]
    public async Task Apply_Inline_AllSubFieldsEmpty_ReturnsNull()
    {
        var token = Inline(new() { ["Наименование"] = "КолНаим" });
        var v = await Apply(token, new() { ["КолНаим"] = null }); // пусто → объект пуст → null
        Assert.Null(v);
    }

    [Fact]
    public async Task Apply_Inline_WithRefSubField_DelegatesToRefResolver()
    {
        // Под-поле «Материал» — @@ref: должно уйти в делегат (в генерации → $ref, здесь → фейк-маркер).
        var refToken = DataSetMappingValue.RefPrefix + JsonSerializer.Serialize(
            new DataSetRefMapping("КолМат", null, Guid.NewGuid(), "Name"));
        var token = Inline(new() { ["Кол"] = "КолКол", ["Материал"] = refToken });
        var v = await Apply(token, new() { ["КолКол"] = "3", ["КолМат"] = "Кабель" });

        var obj = Assert.IsType<Dictionary<string, object?>>(v);
        Assert.Equal("3", obj["Кол"]);
        Assert.Equal("REF:Кабель", obj["Материал"]); // @@ref-под-поле прошло через делегат
    }
}
