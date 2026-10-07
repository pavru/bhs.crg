using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порт распознавания для модулей (ТЗ CORE-Q6, задача B1b, issue #1077).
///
/// <para>Сторожит главное правило задачи: неудача — отказ с причиной, а не пустой результат. Модуль
/// раскладывает ответ по полям своей записи, и пустота под видом ответа стала бы записью, которая
/// выглядит распознанной.</para>
///
/// <para>Хост — настоящее приложение с <c>Modules__Enabled=costs</c>: профиль «Счёт на оплату»
/// объявляет модуль счетов, и читается он из базы, как его прочтёт обработчик модуля. Подменены
/// только движок и предполётная проверка — настоящие ходят в сеть.</para>
/// </summary>
[Collection(CostsOnlyCollection.Name)]
public class ModuleRecognitionPortTests(CostsOnlyHost host)
{
    private const string Code = CostsRecognitionProfiles.InvoiceCode;
    private static readonly byte[] Scan = [1, 2, 3];

    private ModuleRecognitionPort Port(IServiceScope scope, IDocumentRecognizer recognizer, RecognitionBlock? block = null)
    {
        _ = host.CreateClient();
        return new ModuleRecognitionPort(
            scope.ServiceProvider.GetRequiredService<RecognitionProfileCatalog>(),
            scope.ServiceProvider.GetRequiredService<IRecognitionProfileProvider>(),
            recognizer, new Preflight(block));
    }

    /// <summary>Порт отдаётся приложением: модуль получит его из контейнера, а не соберёт сам.</summary>
    [Fact]
    public void Port_is_served_by_the_application()
    {
        _ = host.CreateClient();
        using var scope = host.Services.CreateScope();

        Assert.IsType<ModuleRecognitionPort>(scope.ServiceProvider.GetRequiredService<IModuleRecognition>());
    }

    [Fact]
    public async Task Answer_comes_by_the_keys_of_the_profile_with_what_was_asked()
    {
        using var scope = host.Services.CreateScope();
        var recognizer = new Answering(new()
        {
            [CostsRecognitionProfiles.Number] = " СЧ-17 ",
            [CostsRecognitionProfiles.Total] = "1200.50",
            [CostsRecognitionProfiles.Supplier] = "   ",
            ["Лишнее"] = "модель дописала",
            [InvoiceFields.LineItemsPath] =
                """[{"Наименование":"Кабель","Количество":3,"Цена":"400"},{"Наименование":null},"мусор"]""",
        });

        var result = await Port(scope, recognizer).RecognizeAsync(Code, Scan, "application/pdf");

        Assert.Equal(CostsRecognitionProfiles.InvoiceHeader.Select(f => f.Name), result.AskedFields);
        Assert.Equal(CostsRecognitionProfiles.InvoiceLines.Select(f => f.Name), result.AskedColumns);
        Assert.Equal("СЧ-17", result.Fields[CostsRecognitionProfiles.Number]);
        // Пробелы — не значение: «спросили и не нашли».
        Assert.Null(result.Fields[CostsRecognitionProfiles.Supplier]);
        Assert.Null(result.Fields[CostsRecognitionProfiles.Date]);
        Assert.DoesNotContain("Лишнее", result.Fields.Keys);
        // Строка без единого значения и не-объект строками не становятся.
        var row = Assert.Single(result.Rows);
        Assert.Equal("Кабель", row[CostsRecognitionProfiles.LineName]);
        Assert.Equal("3", row[CostsRecognitionProfiles.LineQuantity]);
        Assert.Null(row[CostsRecognitionProfiles.LineUnit]);
        Assert.Null(result.RowsProblem);
        Assert.Equal("Проба · модель", result.Engine);
        // Запрос — тот же, которым счёт читают наборы данных.
        Assert.Equal(RecognitionShared.BuildInvoicePrompt(TestRecognition.InvoiceCall), recognizer.Prompt);
    }

    /// <summary>
    /// Сторож задачи. Модель ответила, а значений нет — ни в шапке, ни в таблице. Это отказ.
    ///
    /// Ломается, если вернуть такой ответ результатом (убрать проверку «ни одного значения» в порте):
    /// получатель завёл бы черновик из пустых полей.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("не json")]
    public async Task Answer_without_a_single_value_is_a_refusal_not_an_empty_result(string? table)
    {
        using var scope = host.Services.CreateScope();
        var values = new Dictionary<string, string?> { [CostsRecognitionProfiles.Number] = "", ["Лишнее"] = "x" };
        if (table is not null) values[InvoiceFields.LineItemsPath] = table;

        var refusal = await Assert.ThrowsAsync<RecognitionRefusedException>(() =>
            Port(scope, new Answering(values)).RecognizeAsync(Code, Scan, "application/pdf"));

        Assert.Equal(RecognitionRefusal.NoAnswer, refusal.Reason);
        Assert.Contains("Счёт на оплату", refusal.Message);
    }

    /// <summary>
    /// «Таблицу не разобрали» и «строк нет» — разные исходы, и оба выглядели бы нулём строк.
    /// Ломается, если ReadRows перестанет называть причину (вернёт null на битый ответ).
    /// </summary>
    [Theory]
    [InlineData(null, "модель не вернула таблицу")]
    [InlineData("не json", "таблицу в ответе модели не разобрать")]
    [InlineData("""{"не":"список"}""", "таблица в ответе модели — не список строк")]
    [InlineData("[]", null)]
    public async Task Unreadable_table_is_named_and_an_empty_one_is_not(string? table, string? problem)
    {
        using var scope = host.Services.CreateScope();
        var values = new Dictionary<string, string?> { [CostsRecognitionProfiles.Number] = "СЧ-17" };
        if (table is not null) values[InvoiceFields.LineItemsPath] = table;

        var result = await Port(scope, new Answering(values)).RecognizeAsync(Code, Scan, "application/pdf");

        Assert.Empty(result.Rows);
        Assert.Equal(problem, result.RowsProblem);
    }

    /// <summary>
    /// Три причины отказа различимы: чинятся они по-разному. «Некому» и «не справились» цепочка
    /// бросает одним типом — различает их предполётная проверка.
    /// </summary>
    [Fact]
    public async Task Refusals_of_the_engine_carry_their_reason()
    {
        using var scope = host.Services.CreateScope();
        var noEngine = new RecognitionBlock(RecognitionBlock.NoEngine, "Нет включённых движков.");

        var silent = await Assert.ThrowsAsync<RecognitionRefusedException>(() =>
            Port(scope, new Failing(new RecognitionSilentException("движок вернул пустой ответ.")))
                .RecognizeAsync(Code, Scan, "application/pdf"));
        var down = await Assert.ThrowsAsync<RecognitionRefusedException>(() =>
            Port(scope, new Failing(new RecognitionLimitException("достигнут лимит запросов.")))
                .RecognizeAsync(Code, Scan, "application/pdf"));
        var unset = await Assert.ThrowsAsync<RecognitionRefusedException>(() =>
            Port(scope, new Failing(new RecognitionUnavailableException("некому")), noEngine)
                .RecognizeAsync(Code, Scan, "application/pdf"));

        Assert.Equal(RecognitionRefusal.NoAnswer, silent.Reason);
        Assert.Equal(RecognitionRefusal.Unavailable, down.Reason);
        Assert.Contains("достигнут лимит", down.Message);
        Assert.Equal(RecognitionRefusal.NotConfigured, unset.Reason);
        Assert.Equal(noEngine.Message, unset.Message);
    }

    /// <summary>До постановки задачи: «не настроено» — ответом на нажатие; настроено — молчит.</summary>
    [Fact]
    public async Task Readiness_is_refused_before_the_work_is_queued()
    {
        using var scope = host.Services.CreateScope();
        var blind = new RecognitionBlock(RecognitionBlock.Blind, "Ollama: модель не видит изображений.");

        await Port(scope, new Answering([])).EnsureReadyAsync(Code);
        var refusal = await Assert.ThrowsAsync<RecognitionRefusedException>(() =>
            Port(scope, new Answering([]), blind).EnsureReadyAsync(Code));

        Assert.Equal(RecognitionRefusal.NotConfigured, refusal.Reason);
        Assert.Equal(blind.Message, refusal.Message);
    }

    /// <summary>
    /// Профиль правит администратор. Убранное поле не спрашивают — и в ответе его нет вовсе, а не
    /// «пусто»: получатель отличает «не спрашивали» от «не нашли».
    /// </summary>
    [Fact]
    public async Task Field_removed_by_the_administrator_is_not_asked_and_not_answered()
    {
        _ = host.CreateClient();
        using var scope = host.Services.CreateScope();
        // Правка — подменой поставщика профилей, а не строкой в базе: база у хоста общая с соседними
        // классами, и правленый профиль читали бы они же.
        var stored = await scope.ServiceProvider.GetRequiredService<IRecognitionProfileProvider>()
            .GetDefaultAsync(RecognitionProfileKind.Invoice);
        var edited = stored with
        {
            Fields = [.. stored.Fields.Where(f => f.Name != CostsRecognitionProfiles.SupplierTaxId)],
        };
        var port = new ModuleRecognitionPort(
            scope.ServiceProvider.GetRequiredService<RecognitionProfileCatalog>(), new Edited(edited),
            new Answering(new()
            {
                [CostsRecognitionProfiles.Number] = "СЧ-17",
                [CostsRecognitionProfiles.SupplierTaxId] = "7700000000",
            }),
            new Preflight(null));

        var result = await port.RecognizeAsync(Code, Scan, "application/pdf");

        Assert.DoesNotContain(CostsRecognitionProfiles.SupplierTaxId, result.AskedFields);
        // Модель вернула и то, о чём не спрашивали, — в ответ это не попадает.
        Assert.DoesNotContain(CostsRecognitionProfiles.SupplierTaxId, result.Fields.Keys);
        Assert.Contains(CostsRecognitionProfiles.Supplier, result.AskedFields);
    }

    /// <summary>
    /// Модуль счетов выключен (установка по умолчанию): его профиль объявлен, но не читается — отказ
    /// называет модуль. Ворота стоят до движка: распознаватель не спрошен.
    /// </summary>
    [Fact]
    public async Task Profile_of_a_disabled_module_refuses_by_its_name()
    {
        var recognizer = new Answering(new() { [CostsRecognitionProfiles.Number] = "СЧ-17" });
        var port = new ModuleRecognitionPort(TestRecognition.WithoutCosts, null!, recognizer, new Preflight(null));

        var onCheck = await Assert.ThrowsAsync<InvalidRequestException>(() => port.EnsureReadyAsync(Code));
        var onRead = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            port.RecognizeAsync(Code, Scan, "application/pdf"));

        Assert.Contains("Счета и накладные", onCheck.Message);
        Assert.Contains("Счета и накладные", onRead.Message);
        Assert.Null(recognizer.Prompt);
    }

    /// <summary>
    /// Код, которого никто не объявлял, и вид, который одним вызовом не читается, — ошибка модуля,
    /// а не человека: отказ не доменный.
    /// </summary>
    [Fact]
    public async Task Unknown_code_and_page_wise_kind_are_the_modules_mistake()
    {
        var port = new ModuleRecognitionPort(TestRecognition.Catalog, null!, new Answering([]), new Preflight(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => port.EnsureReadyAsync("нет-такого"));
        var pageWise = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            port.EnsureReadyAsync(BHS.CRG.Api.Modules.IdRecognitionProfiles.TitleBlockCode));
        Assert.Contains("одним вызовом", pageWise.Message);
    }

    /// <summary>Поставщик, у которого заводской профиль вида уже правлен. Остальное порт не зовёт.</summary>
    private sealed class Edited(ResolvedRecognitionProfile profile) : IRecognitionProfileProvider
    {
        public Task<ResolvedRecognitionProfile> GetDefaultAsync(RecognitionProfileKind kind, CancellationToken ct = default) =>
            Task.FromResult(profile);

        public void RequireKind(RecognitionProfileKind kind) => throw new NotSupportedException();
        public Task<ResolvedRecognitionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResolvedRecognitionProfile?> GetForTagAsync(string tag, CancellationToken ct = default) => throw new NotSupportedException();
        public bool IsTableTag(string tag) => throw new NotSupportedException();
        public Task<bool> IsTableGroupAsync(Guid? profileId, IReadOnlyList<string>? tags, CancellationToken ct = default) => throw new NotSupportedException();
        public RecognitionKindInfo DescribeKind(RecognitionProfileKind kind) => throw new NotSupportedException();
        public IReadOnlyList<RecognitionKindInfo> ListKinds() => throw new NotSupportedException();
        public Task ReseedBuiltInAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Preflight(RecognitionBlock? block) : IRecognitionPreflight
    {
        public Task<RecognitionBlock?> CheckAsync(CancellationToken ct = default) => Task.FromResult(block);
    }

    private sealed class Answering(Dictionary<string, string?> values) : IDocumentRecognizer
    {
        public string? Prompt { get; private set; }

        public Task<RecognitionResult> RecognizeAsync(
            byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
            Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
        {
            Prompt = promptBuilder?.Invoke(fields);
            return Task.FromResult(new RecognitionResult(values, null, Engine: "Проба · модель"));
        }
    }

    private sealed class Failing(Exception refusal) : IDocumentRecognizer
    {
        public Task<RecognitionResult> RecognizeAsync(
            byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
            Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default) =>
            Task.FromException<RecognitionResult>(refusal);
    }
}
