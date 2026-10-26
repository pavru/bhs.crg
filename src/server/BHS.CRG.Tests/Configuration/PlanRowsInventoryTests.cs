using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись обращений к строкам смет: строки для СУММ плана читает только <c>PlanRows.Current</c>
/// (change <c>plan-06-estimate-model</c>, ТЗ PLAN-5.2, D3 зонтика <c>plan-00-epic</c>), — по образцу
/// <see cref="InvoiceBookInventoryTests" />.
///
/// <para><b>От чего это.</b> План стройки — сумма ДЕЙСТВУЮЩИХ редакций всех её смет. Запрос, который
/// суммирует <c>EstimateRows</c> напрямую, работает и выглядит правильным — и молча удваивает объём
/// прежней редакцией или пропускает несопоставленные мимо правила PLAN-4. Отличить «чтение для суммы»
/// от «чтения для показа» по исходнику нельзя, поэтому перепись знает КАЖДОЕ место, где строки сметы
/// упоминаются, и зачем; новое место — красный тест и решение, пока оно дёшево.</para>
///
/// <para>Таблица названа, а не только набор: до строк дотягивается и сырой запрос.</para>
/// </summary>
public class PlanRowsInventoryTests
{
    private const string Module = "BHS.CRG.Modules.Plan";

    /// <summary>Кому позволено обращаться к строкам смет — и почему.</summary>
    private static readonly Dictionary<string, string> MayTouchTheRows = new()
    {
        [$"{Module}/Data/PlanRows.cs"] = "единственное чтение строк для сумм плана: действующие редакции, сопоставленные строки",
        [$"{Module}/Data/PlanDbContext.cs"] = "объявление набора",
        [$"{Module}/Data/EstimateModel.cs"] = "отображение сущности на таблицу: запросов нет",
        [$"{Module}/Data/EstimateRevisionWriter.cs"] = "единственное место записи строк — новой редакцией целиком",
        [$"{Module}/PlanReferences.cs"] = "объявление колонок с идентификаторами ядра: запросов нет",
        [$"{Module}/Endpoints/EstimateRowsEndpoints.cs"] =
            "строки ОДНОЙ редакции для показа, страницами — не план стройки",
        [$"{Module}/Matching/RowMatching.cs"] =
            "сопоставление строк ОДНОЙ редакции: строка или раздел ЛСР на запись (plan-08) — не план стройки",
        [$"{Module}/Endpoints/MatchEndpoints.cs"] =
            "строка ОДНОЙ редакции, которую сопоставляют (plan-08), — не план стройки",
        [$"{Module}/Endpoints/ResourceMatchEndpoints.cs"] =
            "строка ОДНОЙ редакции, чей материал сопоставляют или подбирают ему похожие (plan-08), — не план стройки",
        [$"{Module}/Endpoints/EstimateViews.cs"] =
            "счётчики и итог ОДНОЙ редакции в ответе сметы (строк, не сопоставлено, итог под правом) — не план стройки",
        [$"{Module}/Endpoints/RevisionSectionsEndpoints.cs"] =
            "разделы ЛСР ОДНОЙ редакции со счётчиками для показа (plan-31) — не план стройки",
        [$"{Module}/Endpoints/RevisionMaterialsEndpoints.cs"] =
            "материалы ОДНОЙ редакции по материалу файла, показ и сопоставление группы (plan-31) — не план стройки",
        [$"{Module}/Endpoints/CurrentRevisionPreview.cs"] =
            "объёмы двух редакций ОДНОЙ сметы «было → станет» до смены действующей (plan-31) — не план стройки",
        [$"{Module}/Matching/RateMarks.cs"] =
            "счётчик «единица изменилась» редакций в ответе сметы (plan-31): шифры несопоставленных строк — не план стройки",
    };

    private static readonly Regex Rows = new(
        @"\bEstimateRows\b|\bestimate_rows\b|\bSet<EstimateRow>", RegexOptions.Compiled);

    [Fact]
    public void К_строкам_смет_обращаются_только_названные_места()
    {
        var outsiders = SourceTree.Files(Module)
            .Select(f => (Rel: SourceTree.Relative(f), Text: File.ReadAllText(f)))
            .SelectMany(f => Outsiders(f.Rel, f.Text))
            .ToList();

        Assert.True(outsiders.Count == 0,
            "К строкам смет обратились мимо названных мест:\n" + string.Join("\n", outsiders) + "\n\n" +
            "Строки для сумм плана читает только PlanRows.Current: план стройки — сумма действующих редакций, " +
            "и прямое чтение удвоило бы объём прежней редакцией. Если обращение не для сумм (показ, запись) — " +
            "впишите файл в MayTouchTheRows с причиной.");
    }

    /// <summary>Сторож обязан срабатывать: образец прямой суммы строк в постороннем файле — находка.</summary>
    [Fact]
    public void Образец_прямой_суммы_строк_краснеет()
    {
        const string sample = """
            var planned = await db.EstimateRows
                .Where(r => r.WorkTypeId == workType)
                .SumAsync(r => r.Volume ?? 0m, ct);
            """;

        Assert.Single(Outsiders($"{Module}/Endpoints/WorkProgressEndpoints.cs", sample));
        Assert.Empty(Outsiders($"{Module}/Data/PlanRows.cs", sample));
    }

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var stale = MayTouchTheRows.Keys
            .Where(rel => !File.Exists(Path.Combine(SourceTree.SolutionDir, rel))
                          || !Rows.IsMatch(File.ReadAllText(Path.Combine(SourceTree.SolutionDir, rel))))
            .ToList();
        Assert.True(stale.Count == 0, "В переписи строк смет файлы, где обращения больше нет: " + string.Join(", ", stale));
    }

    /// <summary>Места обращения к строкам в файле, которому оно не позволено; комментарии не в счёт.</summary>
    private static IEnumerable<string> Outsiders(string rel, string source)
    {
        if (MayTouchTheRows.ContainsKey(rel)) yield break;
        var text = string.Join("\n", source.Split('\n').Select(l => l.TrimStart().StartsWith("//") ? "" : l));
        foreach (Match m in Rows.Matches(text))
            yield return $"{rel}:{SourceTree.LineOf(text, m.Index)}";
    }
}
