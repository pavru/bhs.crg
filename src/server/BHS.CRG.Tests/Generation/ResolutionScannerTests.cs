using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Schema;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Гейт полноты обязательных при генерации (issue #296, фаза 0b): пустые/отсутствующие обязательные
/// поля после резолва → ошибки; заполненные и необязательные — нет.
/// </summary>
public class ResolutionScannerTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void ScanMissingRequired_FlagsEmptyAndMissing_NotFilledNotOptional()
    {
        var ctx = new GenerationContext();
        ctx.Set("Заполнено", Json("\"значение\""));
        ctx.Set("Пусто", Json("\"  \""));           // пустая строка (пробелы)
        ctx.Set("ПустойМассив", Json("[]"));         // пустой массив
        // «Отсутствует» — не в контексте вовсе

        var fields = new List<SchemaFieldInfo>
        {
            new("Заполнено", "string", null, "Заполнено", Required: true),
            new("Пусто", "string", null, "Пусто", Required: true),
            new("ПустойМассив", "array", null, "Массив", Required: true),
            new("Отсутствует", "string", null, "Отсутствует", Required: true),
            new("Необязательное", "string", null, "Необязательное", Required: false),
        };

        var diags = new List<ResolutionDiagnostic>();
        ResolutionScanner.ScanMissingRequired(ctx, fields, diags);

        Assert.All(diags, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains(diags, d => d.Path == "Пусто");
        Assert.Contains(diags, d => d.Path == "ПустойМассив");
        Assert.Contains(diags, d => d.Path == "Отсутствует");
        Assert.DoesNotContain(diags, d => d.Path == "Заполнено");
        Assert.DoesNotContain(diags, d => d.Path == "Необязательное");
        Assert.Equal(3, diags.Count);
        // issue #332: незаполненное обязательное помечается кодом missing-required (не leftover-ref).
        Assert.All(diags, d => Assert.Equal("missing-required", d.Code));
    }

    [Fact]
    public void ScanLeftoverRefs_FlagsUnresolvedRef_WithLeftoverRefCode()
    {
        // Ссылка, оставшаяся неразрешённой после резолва (цель удалена) — issue #332 code=leftover-ref.
        var ctx = new GenerationContext();
        ctx.Set("Подрядчик", Json("{\"$ref\":\"catalog\",\"entryId\":\"00000000-0000-0000-0000-000000000001\"}"));
        ctx.Set("Обычное", Json("\"текст\""));

        var diags = new List<ResolutionDiagnostic>();
        ResolutionScanner.ScanLeftoverRefs(ctx, diags);

        var d = Assert.Single(diags);
        Assert.Equal("Подрядчик", d.Path);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("leftover-ref", d.Code);
    }
}
