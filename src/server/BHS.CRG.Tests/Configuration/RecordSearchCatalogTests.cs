using BHS.CRG.Modules.Search;
using BHS.CRG.Api.Search;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Exchange;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись групп поиска при старте (shell-05, задача 1.6; ТЗ AUTH-8.2): группа без ключа доступа, с ключом,
/// которого нет среди прав, или с чужим кодом — отказ старта, и отказ называет группу и модуль. Без этого
/// забытый ключ означал бы группу, отвечающую любому вошедшему.
/// </summary>
public class RecordSearchCatalogTests
{
    private static readonly PermissionCatalog Permissions = new(
    [
        new AppPermission("costs.invoice.read", "Видеть счета", "Счета"),
        new AppPermission("core.constructions.read", "Видеть стройки", "Стройки"),
    ]);

    private static RecordSearchGroup Group(string code, string requires = "costs.invoice.read", string? type = null) =>
        new(code, "Группа", type ?? $"Тип{code}", requires, RecordSearchArchive.None);

    private static InvalidOperationException Refusal(params (string Module, RecordSearchGroup Group)[] groups) =>
        Assert.Throws<InvalidOperationException>(() => new RecordSearchCatalog(
            groups.Select(g => (g.Module, (IReadOnlyList<RecordSearchGroup>)[g.Group])), Permissions));

    [Fact]
    public void Годные_группы_собираются_в_порядке_поставки()
    {
        var catalog = new RecordSearchCatalog(
            [("costs", [Group("invoices")]), (CoreRecordSearch.Module, CoreRecordSearch.Declared)],
            new PermissionCatalog([
                new AppPermission("costs.invoice.read", "Видеть счета", "Счета"),
                new AppPermission("core.constructions.read", "Видеть стройки", "Стройки"),
                new AppPermission("core.datasets.read", "Видеть наборы", "Наборы данных"),
            ]));

        Assert.Equal(["invoices", "constructions", "datasets"], catalog.Sources.Select(s => s.Group.Code));
        Assert.Equal("costs", catalog.ForRecordType("Типinvoices")!.Module);
        Assert.Null(catalog.Find("nothing"));
    }

    [Fact]
    public void Группа_без_ключа_доступа_отказ_старта_с_её_именем()
    {
        var error = Refusal(("probe", Group("secrets", requires: " ")));

        Assert.Contains("«secrets» модуля «probe»", error.Message);
        Assert.Contains("ключ доступа", error.Message);
    }

    [Fact]
    public void Ключ_который_не_право_отказ_старта()
    {
        var error = Refusal(("probe", Group("secrets", requires: "probe.secrets.read")));

        Assert.Contains("«probe.secrets.read» — не объявленное право", error.Message);
    }

    [Fact]
    public void Два_владельца_кода_группы_или_типа_записи_отказ_старта()
    {
        Assert.Contains("код группы «invoices» объявлен несколько раз",
            Refusal(("costs", Group("invoices")), ("probe", Group("invoices", type: "Другой"))).Message);
        Assert.Contains("тип записи «Счёт» у нескольких групп",
            Refusal(("costs", Group("invoices", type: "Счёт")), ("probe", Group("bills", type: "Счёт"))).Message);
    }

    [Fact]
    public void Нерешённый_архив_отказ_старта()
    {
        var error = Refusal(("probe", Group("secrets") with { Archive = (RecordSearchArchive)42 }));

        Assert.Contains("архив", error.Message);
    }

    [Theory]
    [InlineData("  Щитовая   ЁЛКА ", new[] { "щитовая", "елка" })]
    [InlineData("418 418", new[] { "418" })]
    [InlineData("", new string[0])]
    // Знаки по краям сняты — так набирают с карточки; знак внутри слова — часть номера.
    [InlineData("№ 418 · «КабельТорг»,", new[] { "418", "кабельторг" })]
    [InlineData("№418 РОЛ-7", new[] { "418", "рол-7" })]
    [InlineData("— · №", new string[0])]
    public void Слова_запроса_без_регистра_и_ё(string query, string[] words) =>
        Assert.Equal(words, RecordSearchText.Words(query));

    [Fact]
    public void Образец_экранирует_знаки_подстановки() =>
        Assert.Equal(@"%50\%\_x\\%", RecordSearchText.LikePattern(@"50%_x\"));
}
