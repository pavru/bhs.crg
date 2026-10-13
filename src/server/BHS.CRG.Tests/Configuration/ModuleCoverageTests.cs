using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Покрытие потребностей модуля справочниками ядра (ТЗ TYPE-23.1, issue #1101) — то, что видит
/// администратор на экране «Сопоставление полей».
///
/// <para>Потребности здесь — настоящие, модуля счетов: сторож задачи говорит о текстах, которые
/// прочтёт человек, и проверять его на выдуманных значило бы проверять выдумку.</para>
/// </summary>
public class ModuleCoverageTests
{
    private static readonly TagCatalog Tags = TagCatalog.Build(TagRegistry.Core, []);
    private static readonly IAppModule Costs = new Declaring(CostsDirectories.Needs);

    private const string TaxIdField = """{"key":"ИНН","title":"ИНН","type":"string","tags":["org.inn"]}""";
    private const string NameField = """{"key":"Наименование","title":"Наименование","type":"string","tags":["org.name"]}""";

    [Fact]
    public void Всё_назначено_незакрытого_нет()
    {
        var view = ModuleCoverage.Of(Costs, Tags, [Organizations(TaxIdField, NameField), Nomenclature()]);

        Assert.Equal((4, 0), (view.Total, view.Unmet));
        Assert.Empty(view.NotWorking);
        var row = Row(view, FunctionalTag.OrgTaxId);
        Assert.Equal((CoverageState.Mapped, "ИНН", null), (row.State, row.Key, row.Reason));
    }

    /// <summary>
    /// Сторож задачи: непокрытый обязательный тэг назван ВМЕСТЕ с тем, что из-за него не работает.
    /// Строка «не назначено» без следствия не говорит администратору, срочно ли это.
    /// </summary>
    [Fact]
    public void Непокрытый_обязательный_тэг_назван_вместе_с_тем_что_не_работает()
    {
        var view = ModuleCoverage.Of(Costs, Tags, [Organizations(NameField), Nomenclature()]);

        var row = Row(view, FunctionalTag.OrgTaxId);
        Assert.Equal(CoverageState.Absent, row.State);
        Assert.True(row.Required);
        Assert.Contains("ИНН организации", row.Reason);
        Assert.Contains("по ИНН из файла счёта", row.WithoutIt);

        Assert.Equal((4, 1), (view.Total, view.Unmet));
        Assert.Equal([row.WithoutIt], view.NotWorking);
        // Соседняя строка того же справочника от этого не страдает.
        Assert.Equal(CoverageState.Mapped, Row(view, FunctionalTag.OrgName).State);
    }

    /// <summary>
    /// Вторая половина сторожа: два поля с одиночным тэгом — нарушение, а не «взяли первое». Обычное
    /// сохранение схемы такого не допустит, а восстановление копии и включение модуля — допустят.
    /// </summary>
    [Fact]
    public void Два_поля_с_одиночным_тэгом_нарушение()
    {
        var view = ModuleCoverage.Of(Costs, Tags,
        [
            Organizations(TaxIdField, """{"key":"ИННСтарый","title":"ИНН (старый)","type":"string","tags":["org.inn"]}""",
                NameField),
            Nomenclature(),
        ]);

        var row = Row(view, FunctionalTag.OrgTaxId);
        Assert.Equal(CoverageState.Violated, row.State);
        Assert.Null(row.Key);
        Assert.Contains("назначено дважды", row.Reason);
        Assert.Equal(new[] { "ИНН", "ИННСтарый" }, row.Fields.Where(f => f.Tagged).Select(f => f.Key));
        Assert.Equal(1, view.Unmet);
    }

    /// <summary>
    /// Поле с тэгом сделали составным, тэг остался: сервер вид при сохранении не сверяет. Поле в
    /// ответе остаётся — помеченным и негодным: экран обязан назвать его, а не показать пустой выбор.
    /// </summary>
    [Fact]
    public void Поле_неподходящего_вида_нарушение_и_поле_названо()
    {
        var view = ModuleCoverage.Of(Costs, Tags,
        [
            Organizations("""{"key":"ИНН","title":"ИНН","type":"complex","tags":["org.inn"]}""", NameField),
            Nomenclature(),
        ]);

        var row = Row(view, FunctionalTag.OrgTaxId);
        Assert.Equal(CoverageState.Violated, row.State);
        var field = Assert.Single(row.Fields, f => f.Tagged);
        Assert.Equal(("ИНН", false), (field.Key, field.Fits));
        // «Наименование» — строка: ИНН на него назначить можно.
        Assert.True(Assert.Single(row.Fields, f => f.Key == "Наименование").Fits);
    }

    /// <summary>
    /// Роль не несёт никто: строк полей нет вовсе — сопоставлять поле не в чем, и три красные строки
    /// про одну причину были бы шумом. Следствие одно — самого справочника; в счёт идут и его поля.
    /// </summary>
    [Fact]
    public void Справочник_не_назначен_строк_полей_нет_а_назначить_предлагаются_годные_типы()
    {
        var free = Type("Контрагент", """{"fields":[]}""");
        var document = DocumentType.Create("Акт", "Акт", DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
        var nomenclature = Nomenclature();
        var position = Type("Позиция", """{"fields":[]}""", nomenclature.Id);

        var view = ModuleCoverage.Of(Costs, Tags, [free, document, nomenclature, position]);

        var group = Assert.Single(view.Groups, g => g.Role == FunctionalTag.RoleOrganization);
        Assert.Equal(CoverageState.Absent, group.State);
        Assert.Null(group.Type);
        Assert.Empty(group.Rows);
        Assert.Contains("не назначена ни одному типу", group.Reason);
        // Документ справочником не бывает; номенклатура и её подтип — уже другой справочник.
        Assert.Equal(["Контрагент"], group.Candidates.Select(c => c.Code));

        Assert.Equal((4, 3), (view.Total, view.Unmet));
        Assert.Equal([group.WithoutIt], view.NotWorking);
    }

    /// <summary>
    /// Роль несут двое — это не «не назначено»: назначить третьего здесь нельзя, сначала снимают
    /// лишнее. Поэтому выбирать не из чего.
    /// </summary>
    [Fact]
    public void Два_носителя_роли_нарушение_и_назначить_нечего()
    {
        var view = ModuleCoverage.Of(Costs, Tags,
            [Organizations(TaxIdField, NameField), Type("Контрагент", """{"tags":["role.organization"],"fields":[]}"""),
                Type("Свободный", """{"fields":[]}"""), Nomenclature()]);

        var group = Assert.Single(view.Groups, g => g.Role == FunctionalTag.RoleOrganization);
        Assert.Equal(CoverageState.Violated, group.State);
        Assert.Contains("несут несколько типов", group.Reason);
        Assert.Empty(group.Candidates);
        Assert.Empty(group.Rows);
    }

    /// <summary>
    /// Тэг унаследованного поля лежит в схеме предка — туда же экран и запишет. Назови мы носителя
    /// роли, запись ушла бы в схему, где этого поля нет.
    /// </summary>
    [Fact]
    public void Унаследованное_поле_названо_вместе_с_типом_где_оно_объявлено()
    {
        var party = Type("Сторона", $$"""{"fields":[{{TaxIdField}}]}""");
        var organizations = Type("Организация",
            $$"""{"tags":["role.organization"],"fields":[{{NameField}}]}""", party.Id);

        var view = ModuleCoverage.Of(Costs, Tags, [party, organizations, Nomenclature()]);

        var row = Row(view, FunctionalTag.OrgTaxId);
        Assert.Equal(CoverageState.Mapped, row.State);
        Assert.Equal("Сторона", Assert.Single(row.Fields, f => f.Key == "ИНН").DeclaredIn.Code);
        Assert.Equal("Организация", Assert.Single(row.Fields, f => f.Key == "Наименование").DeclaredIn.Code);
    }

    private static CoverageRow Row(ModuleCoverageView view, string tag) =>
        Assert.Single(view.Groups.SelectMany(g => g.Rows), r => r.Tag == tag);

    private static DocumentType Organizations(params string[] fields) =>
        Type("Организация", $$"""{"tags":["role.organization"],"fields":[{{string.Join(",", fields)}}]}""");

    private static DocumentType Nomenclature() =>
        Type("Номенклатура", """{"tags":["role.nomenclature"],"fields":[]}""");

    private static DocumentType Type(string code, string schema, Guid? parent = null) =>
        DocumentType.Create(code, code, DocumentTypeKind.Composite, parent, JsonDocument.Parse(schema),
            TypeOwner.Core, TypeVisibility.Shared);

    private sealed class Declaring(IReadOnlyList<ModuleRoleNeed> needs) : IAppModule
    {
        public string Code => "costs";
        public string Title => "Счета и накладные";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => [];
        public IReadOnlyList<ModuleRoleNeed> Needs => needs;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
