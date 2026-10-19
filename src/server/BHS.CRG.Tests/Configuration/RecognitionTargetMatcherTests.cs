using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Recognition;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Сопоставление «поле профиля → поле записи» (issue #1483): объявленное модулем плюс поля, которые
/// заказчик дописал в тип. Раскладку значений проверяет модуль (<c>InvoiceScanHeaderTests</c>).
/// </summary>
public class RecognitionTargetMatcherTests
{
    private static readonly RecognitionTargets Declared = new("Тип",
    [
        new("Номер", RecognitionTargetUse.Field, null, ["НомерСчёта"]),
        new("ИНН", RecognitionTargetUse.Input, null, []),
        new("Срок", RecognitionTargetUse.Unavailable, "срок считает правило", []),
    ], new HashSet<string> { "Номер", "Срок" });

    private static readonly PrimitiveType Code = PrimitiveType.Create(
        "Код договора", "contract", "string", null, JsonDocument.Parse("""{"pattern":"^Д-\\d+$"}"""));
    private static readonly PrimitiveType Flag = PrimitiveType.Create("Флаг", "flag", "bool", null, JsonDocument.Parse("{}"));

    private static readonly IReadOnlyList<SchemaFieldInfo> Fields =
    [
        new("Номер", "string", null, "Номер счёта"),
        new("Срок", "date", null, "Оплатить до"),
        new("Договор", "string", null, "Договор"),
        new("Объём", "number", null),
        new("КодДоговора", "primitive", Code.Id, "Код договора"),
        new("Флажок", "primitive", Flag.Id),
        new("Вид", "enum", null),
        new("Склад", "complex", Guid.NewGuid()),
        new("Расчёт", "number", null, Computed: true, Expression: "1"),
        new("Состояние", "string", null, Locked: true),
    ];

    private static IReadOnlyDictionary<string, ResolvedRecognitionTarget?> Resolve(params string[] keys) =>
        RecognitionTargetMatcher.Resolve(keys, Declared, Fields,
            new Dictionary<Guid, PrimitiveType> { [Code.Id] = Code, [Flag.Id] = Flag });

    [Fact]
    public void Объявленное_прежний_ключ_и_вход()
    {
        var targets = Resolve("Номер", "НомерСчёта", "ИНН", "Срок");
        Assert.Equal(new ResolvedRecognitionTarget("Номер", RecognitionTargetOrigin.Declared, "string", Title: "Номер счёта"), targets["Номер"]);
        // Прежний ключ ведёт в нынешнее поле.
        Assert.Equal(targets["Номер"], targets["НомерСчёта"]);
        Assert.Equal(RecognitionTargetOrigin.Input, targets["ИНН"]!.Origin);
        Assert.Equal("срок считает правило", targets["Срок"]!.Unavailable);
    }

    /// <summary>
    /// Ревью PR #1523: заказчик назвал своё поле так же, как служебный вход или прежний ключ, — ключ совпал с
    /// его полем точно, и поле заполняется. Объявленное старше только своих полей модуля.
    /// </summary>
    [Fact]
    public void Поле_заказчика_старше_входа_и_прежнего_ключа()
    {
        IReadOnlyList<SchemaFieldInfo> fields = [.. Fields, new("ИНН", "string", null), new("НомерСчёта", "string", null)];
        var targets = RecognitionTargetMatcher.Resolve(["ИНН", "НомерСчёта", "Номер"], Declared, fields,
            new Dictionary<Guid, PrimitiveType>());
        Assert.Equal(new ResolvedRecognitionTarget("ИНН", RecognitionTargetOrigin.Type, "string", Title: "ИНН"), targets["ИНН"]);
        Assert.Equal("НомерСчёта", targets["НомерСчёта"]!.Key);
        Assert.Equal(RecognitionTargetOrigin.Declared, targets["Номер"]!.Origin);
    }

    [Fact]
    public void Поля_заказчика_по_виду()
    {
        var targets = Resolve("Договор", "Объём", "КодДоговора");
        Assert.Equal(new ResolvedRecognitionTarget("Договор", RecognitionTargetOrigin.Type, "string", Title: "Договор"), targets["Договор"]);
        Assert.Equal("number", targets["Объём"]!.Type);
        // Тип из реестра — базовым видом: шаблон проверит охрана записи.
        Assert.Equal(new ResolvedRecognitionTarget("КодДоговора", RecognitionTargetOrigin.Type, "string", Title: "Код договора"),
            targets["КодДоговора"]);
    }

    [Theory]
    [InlineData("Флажок", "базовый вид")]
    [InlineData("Вид", "вариант выбирает человек")]
    [InlineData("Склад", "ссылка на запись")]
    [InlineData("Расчёт", "расчётное")]
    [InlineData("Состояние", "кладёт код модуля")]
    public void Не_заполняемое_названо_с_причиной(string key, string why) =>
        Assert.Contains(why, Resolve(key)[key]!.Unavailable);

    /// <summary>Совпадение точное: регистр и пробелы — другой ключ. Опечатку видно в итоге распознавания.</summary>
    [Fact]
    public void Совпадение_точное()
    {
        var targets = Resolve("номер", "Номер ", "Нет");
        Assert.All(targets.Values, Assert.Null);
    }

    [Fact]
    public void Без_объявления_целей_нет()
    {
        var targets = RecognitionTargetMatcher.Resolve(["Номер", "Договор"], null, Fields, new Dictionary<Guid, PrimitiveType>());
        Assert.All(targets.Values, Assert.Null);
    }

    /// <summary>
    /// Сторож: каждый вид поля, который знает редактор схемы, решён — заполняется или нет и почему. Новый
    /// вид без решения иначе прошёл бы молча в «распознаванию не знаком», а заполнять его, может быть, и
    /// надо было.
    /// </summary>
    [Fact]
    public void Каждый_вид_поля_редактора_решён()
    {
        var schema = File.ReadAllText(RepoPaths.File("src", "client", "src", "shared", "api", "schema.ts"));
        var line = Regex.Match(schema, @"^\s*type: ('[^;]+);", RegexOptions.Multiline);
        Assert.True(line.Success, "В schema.ts не найдено объединение видов поля `type: '…' | …;` — сторож ослеп.");
        var kinds = Regex.Matches(line.Groups[1].Value, "'([^']+)'").Select(m => m.Groups[1].Value).Order();
        Assert.Equal(kinds, RecognitionTargetMatcher.Kinds.Keys.Order());
    }

    [Theory]
    [InlineData("Чужой", "Номер", "называют тип «Чужой»")]
    [InlineData("Тип", "НетТакого", "поля «НетТакого» у типа «Тип» нет")]
    public void Негодное_объявление_останавливает_старт(string recordType, string key, string fault)
    {
        var module = new Declaring(new ModuleRecognitionTargets(recordType, [new(key)]));
        var refusal = Assert.Throws<InvalidOperationException>(
            () => ModuleRecognitionCollector.Build(new ModuleRegistry([module], [])));
        Assert.Contains(fault, refusal.Message);
    }

    [Fact]
    public void Повтор_и_прежний_ключ_совпавший_с_целью_останавливают_старт()
    {
        var module = new Declaring(new ModuleRecognitionTargets("Тип",
        [
            new("Номер", LegacyKeys: ["Дата"]),
            new("Дата"),
            new("Номер"),
            new("Срок", ModuleTargetUse.Unavailable),
        ]));
        var refusal = Assert.Throws<InvalidOperationException>(
            () => ModuleRecognitionCollector.Build(new ModuleRegistry([module], [])));
        Assert.Contains("ключ «Номер» объявлен 2 раза", refusal.Message);
        Assert.Contains("прежний ключ «Дата» совпадает с ключом цели", refusal.Message);
        Assert.Contains("у недоступного поля «Срок» не названа причина", refusal.Message);
    }

    /// <summary>Модуль с одним типом и одним профилем — ровно то, что сверяет коллектор.</summary>
    private sealed class Declaring(ModuleRecognitionTargets targets) : IAppModule
    {
        public string Code => "probe";
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => ["/api/probe"];

        public IReadOnlyList<ModuleRecordType> RecordTypes =>
        [
            new("Тип", "Тип", ModuleSchemaLevel.Extendable,
                [new("Номер", "Номер", "string"), new("Дата", "Дата", "date"), new("Срок", "Срок", "date")]),
        ];

        public IReadOnlyList<ModuleRecognitionProfile> RecognitionProfiles =>
            [new("probe", "Проба", "Invoice", [new("Номер", "Номер")], Targets: targets)];

        public void RegisterServices(
            Microsoft.Extensions.DependencyInjection.IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
