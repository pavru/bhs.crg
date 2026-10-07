using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Recognition;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Каталог заводских профилей распознавания: объявления модулей и ворота (задача B1a, issue #1075).
/// Без базы и без хоста — здесь проверяется само правило, а что приложение ему следует, проверяет
/// <c>RecognitionWithoutIdModuleTests</c>.
/// </summary>
public class RecognitionProfileCatalogTests
{
    private static RecognitionProfileCatalog Build(IAppModule[] enabled, IAppModule[]? disabled = null) =>
        ModuleRecognitionCollector.Build(new ModuleRegistry(enabled, disabled ?? []));

    /// <summary>
    /// В поставке у каждого вида есть владелец. Вид без заводского профиля недоступен никому: читать
    /// им нечем до первой привязки своего, а свой создать нельзя — владельца нет. Новый вид,
    /// добавленный в ядро без объявления, проявился бы пунктом, который не выбрать.
    /// </summary>
    [Fact]
    public void Every_kind_of_the_delivery_has_an_owner()
    {
        var catalog = Build([new IdModule(), new CostsModule()]);

        Assert.All(RecognitionKinds.All, kind => Assert.True(
            catalog.IsAvailable(kind.Kind), $"у вида {kind.Kind} нет заводского профиля"));
    }

    /// <summary>
    /// Кто чем владеет. «Счёт на оплату» — у ядра: отступление от ТЗ до задачи B1b, принятое
    /// владельцем 07.10.2026. Тест покраснеет, когда профиль переедет в модуль счетов, — и это тот
    /// случай, когда его надо переписать вместе с переездом, а не чинить.
    /// </summary>
    [Fact]
    public void Owners_of_the_delivered_profiles()
    {
        var catalog = Build([new IdModule(), new CostsModule()]);

        Assert.Equal(RecognitionProfileCatalog.CoreOwner, catalog.Find(CoreRecognitionProfiles.InvoiceCode)!.Owner);
        Assert.All(
            new[]
            {
                IdRecognitionProfiles.TitleBlockCode, IdRecognitionProfiles.CoverTitleCode,
                IdRecognitionProfiles.SpecificationTableCode, IdRecognitionProfiles.CableJournalCode,
            },
            code => Assert.Equal("id", catalog.Find(code)!.Owner));
    }

    /// <summary>Модуль выключен: его объявления в каталоге есть, но вид недоступен, и ворота
    /// отвечают отказом с названием модуля. Вид ядра доступен при любом составе.</summary>
    [Fact]
    public void Disabled_module_keeps_declarations_and_refuses_by_name()
    {
        var catalog = TestRecognition.WithoutId;

        Assert.NotNull(catalog.Find(IdRecognitionProfiles.TitleBlockCode));
        Assert.False(catalog.IsAvailable(RecognitionProfileKind.TitleBlock));
        Assert.True(catalog.IsAvailable(RecognitionProfileKind.Invoice));

        var refusal = Assert.Throws<InvalidRequestException>(() => catalog.Require(RecognitionProfileKind.TitleBlock));
        Assert.Contains("Исполнительная документация", refusal.Message);
        Assert.Contains("Штамп ГОСТ", refusal.Message);
        catalog.Require(RecognitionProfileKind.Invoice);
    }

    /// <summary>
    /// Свой профиль принадлежит владельцу своего вида, что бы ни лежало в колонке: пусто (копия,
    /// снятая до её появления) или чужой код (копия экземпляра с другим составом модулей). Иначе
    /// такая строка была бы невидимой и неудаляемой навсегда.
    ///
    /// Встроенный — тому, кто записан в колонке: строка с кодом, которого никто не объявляет, и с
    /// владельцем, которого в сборке нет, недоступна и названа его кодом.
    /// </summary>
    [Fact]
    public void Own_profile_follows_the_kind_and_built_in_follows_the_column()
    {
        var catalog = TestRecognition.Catalog;
        var fields = RecognitionProfileJson.WriteFields([new RecognitionProfileField("Шифр")]);

        foreach (var column in new[] { "", "plan", RecognitionProfileCatalog.CoreOwner })
        {
            var own = RecognitionProfile.Create("Свой", RecognitionProfileKind.TitleBlock, column, fields);
            Assert.Equal("id", catalog.OwnerOf(own)!.Code);
            Assert.True(catalog.IsAvailable(own));
        }

        var foreign = RecognitionProfile.CreateBuiltIn(
            "estimate", "Смета", RecognitionProfileKind.TitleBlock, "plan", fields, null, null, "хеш");
        Assert.False(catalog.IsAvailable(foreign));
        Assert.Contains("«plan»", Assert.Throws<InvalidRequestException>(() => catalog.Require(foreign)).Message);
    }

    /// <summary>
    /// Вид числом — тоже неизвестный вид. Разбор перечисления принимает «1» и «7», и без отдельной
    /// проверки профиль с видом, которого нет, доехал бы до базы.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("7")]
    [InlineData("0")]
    public void Numeric_kind_stops_the_start(string kind)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(
            [new Declaring("plan", "Планирование", new ModuleRecognitionProfile("estimate", "Смета", kind,
                [new ModuleRecognitionField("Номер", "Номер сметы")]))]));

        Assert.Contains("«estimate»", error.Message);
    }

    /// <summary>Модуль, назвавшийся кодом ядра, — в общий отказ, а не «An item with the same key».</summary>
    [Fact]
    public void Owner_code_taken_twice_stops_the_start_and_names_both()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(
            [new Declaring(RecognitionProfileCatalog.CoreOwner, "Самозванец")]));

        Assert.Contains("Объявления профилей распознавания негодны", error.Message);
        Assert.Contains("«Самозванец»", error.Message);
    }

    // ── Негодные объявления останавливают старт ──────────────────────────────────────────────────

    [Fact]
    public void Unknown_kind_stops_the_start_and_names_the_profile()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(
            [new Declaring("plan", "Планирование", new ModuleRecognitionProfile("estimate", "Смета", "Estimate",
                [new ModuleRecognitionField("Номер", "Номер сметы")]))]));

        Assert.Contains("«estimate»", error.Message);
        Assert.Contains("«Estimate»", error.Message);
    }

    /// <summary>Два владельца одного кода — и строка в базе досталась бы тому, кого сидер обошёл
    /// последним. Проверяются и выключенные модули: их строки лежат в той же таблице.</summary>
    [Fact]
    public void Code_declared_twice_stops_the_start_even_when_one_owner_is_disabled()
    {
        var twin = new ModuleRecognitionProfile(IdRecognitionProfiles.CoverTitleCode, "Обложка", "CoverTitle",
            [new ModuleRecognitionField("Шифр", "Шифр")]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            Build([new IdModule()], [new Declaring("plan", "Планирование", twin)]));

        Assert.Contains($"«{IdRecognitionProfiles.CoverTitleCode}»", error.Message);
    }

    /// <summary>Второй заводской профиль того же вида: умолчание вида стало бы выбором наугад.</summary>
    [Fact]
    public void Second_factory_profile_of_a_kind_stops_the_start()
    {
        var second = new ModuleRecognitionProfile("waybill", "Расходная накладная", "Invoice",
            [new ModuleRecognitionField("Номер", "Номер накладной")]);

        var error = Assert.Throws<InvalidOperationException>(() => Build([new Declaring("plan", "Планирование", second)]));

        Assert.Contains("«Invoice»", error.Message);
        Assert.Contains("«waybill»", error.Message);
    }

    /// <summary>Все изъяны — одним отказом: иначе администратор чинил бы их по одному на перезапуск.</summary>
    [Fact]
    public void Faults_are_reported_together()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(
            [new Declaring("plan", "Планирование",
                new ModuleRecognitionProfile("estimate", "Смета", "Estimate", [new ModuleRecognitionField("Номер", "Номер")]),
                new ModuleRecognitionProfile("empty", "Пустой", "CoverTitle"))]));

        Assert.Contains("«estimate»", error.Message);
        Assert.Contains("«empty» пуст", error.Message);
    }

    /// <summary>Модуль, который только объявляет профили: остальное у него пусто.</summary>
    private sealed class Declaring(string code, string title, params ModuleRecognitionProfile[] profiles) : IAppModule
    {
        public string Code => code;
        public string Title => title;
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => [];
        public IReadOnlyList<ModuleRecognitionProfile> RecognitionProfiles => profiles;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
