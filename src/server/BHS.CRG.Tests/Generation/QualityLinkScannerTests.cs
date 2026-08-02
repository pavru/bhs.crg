using System.Text.Json;
using BHS.CRG.Application.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Диагностика «материал без документа качества» (issue #585). Составной ключ строже прежнего
/// «совпало любое поле», поэтому несопоставленных материалов больше по построению — и пользователь
/// должен их видеть, раз решение (исправить материал или завести вторую связку) за ним.
/// </summary>
public class QualityLinkScannerTests
{
    private static readonly string[] IdentityFields = ["Наименование", "Артикул"];
    private const string Target = "ДокументПодтверждающийКачество";

    private static List<ResolutionDiagnostic> Scan(string materialsJson)
    {
        var ctx = new GenerationContext();
        ctx.Set("Материалы", JsonDocument.Parse(materialsJson).RootElement.Clone());
        var diagnostics = new List<ResolutionDiagnostic>();
        QualityLinkScanner.Scan(ctx, IdentityFields, Target, diagnostics);
        return diagnostics;
    }

    [Fact]
    public void MaterialWithoutQualityDoc_IsWarning()
    {
        var d = Assert.Single(Scan("""[ { "Наименование": "Трубка", "Артикул": "T-1" } ]"""));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);   // выпуск не блокируем
        Assert.Equal(QualityLinkScanner.Code, d.Code);
        Assert.Equal($"Материалы[0].{Target}", d.Path);
        Assert.Contains("трубка | t-1", d.Message);             // в тексте — ключ, по которому искали
    }

    [Fact]
    public void MaterialWithQualityDoc_IsSilent()
        => Assert.Empty(Scan($$"""
            [ { "Наименование": "Трубка", "{{Target}}": { "НомерДокумента": "РОСС RU.0001" } } ]
            """));

    /// <summary>Пустой объект — не документ: резолвер такое поле считает незаполненным, и проверка
    /// обязана отвечать так же.</summary>
    [Fact]
    public void EmptyQualityDocObject_CountsAsMissing()
        => Assert.Single(Scan($$"""[ { "Наименование": "Трубка", "{{Target}}": {} } ]"""));

    /// <summary>Строка без единого поля идентичности материалом не считается — иначе предупреждение
    /// доставалось бы строкам, которые сопоставлять и не собирались (примечания, итоги).</summary>
    [Fact]
    public void RowWithoutIdentityValues_IsNotAMaterial()
        => Assert.Empty(Scan("""[ { "Примечание": "итого по разделу" }, { "Наименование": "  " } ]"""));

    /// <summary>Материал без артикула — обычный случай (пустой слот в ключе), и он тоже должен быть
    /// виден: именно такие теряются при переходе на составной ключ.</summary>
    [Fact]
    public void PartialIdentity_StillReported()
    {
        var d = Assert.Single(Scan("""[ { "Наименование": "Трубка" } ]"""));
        Assert.Contains("трубка | ", d.Message);
    }

    [Fact]
    public void TagsNotConfigured_ScannerStaysQuiet()
    {
        var ctx = new GenerationContext();
        ctx.Set("Материалы", JsonDocument.Parse("""[ { "Наименование": "Трубка" } ]""").RootElement.Clone());
        var diagnostics = new List<ResolutionDiagnostic>();
        QualityLinkScanner.Scan(ctx, [], Target, diagnostics);
        QualityLinkScanner.Scan(ctx, IdentityFields, null, diagnostics);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void IndexInPath_PointsAtTheRow()
    {
        var diagnostics = Scan("""
            [ { "Наименование": "Есть", "ДокументПодтверждающийКачество": { "Н": "1" } },
              { "Наименование": "Нет" } ]
            """);
        Assert.Equal($"Материалы[1].{Target}", Assert.Single(diagnostics).Path);
    }
}
