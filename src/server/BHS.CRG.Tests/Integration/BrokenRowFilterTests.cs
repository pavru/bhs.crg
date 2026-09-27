using System.Text;
using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Битый отбор отказывает на живых путях чтения (issue #966).
///
/// <para>Юнит-тесты исполнителя проверяют само правило; здесь — что отказ ДОХОДИТ, и доходит
/// понятным. Два пути, за которые стоит бояться: печатная форма (отказ обязан снять документ с
/// выпуска, а не дать пустую таблицу) и предпросмотр (человек правит настройку именно здесь).</para>
///
/// <para><b>Негодный отбор ставится тем же путём, которым он попадает в живую базу</b> — службой,
/// сохраняющей настройку источника: она принимает объект и складывает его как есть, ничего не
/// проверяя. Ломаного ТЕКСТА в базе не бывает вовсе — колонка <c>jsonb</c> его не принимает, — а вот
/// годный JSON с оператором, которого нет, ложится молча. Поэтому здесь именно он: сторож, поставленный
/// на недостижимое состояние, ничего не стережёт.</para>
/// </summary>
[Collection("Integration")]
public class BrokenRowFilterTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Годный JSON, негодное условие: оператора «betwen» нет — прежде он считался истиной.</summary>
    private const string UnknownOp =
        """{"type":"group","logic":"and","children":[{"type":"condition","column":"A","op":"betwen","value":"5"}]}""";

    private sealed record Seed(Guid InstanceId, Guid SourceId, string SourceName);

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    /// <summary>Документ комплекта, CSV-источник с привязкой на табличное поле и битый отбор в базе.</summary>
    private async Task<Seed> SeedAsync(IServiceScope scope, string filterJson)
    {
        var m = M(scope);
        var rowType = await m.Send(new CreateDocumentTypeCommand("Строка", $"ROW{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Composite, null, J("{'fields':[{'key':'Наименование','type':'string'}]}")));
        var docType = await m.Send(new CreateDocumentTypeCommand("Ведомость", $"VED{Guid.NewGuid():N}"[..12],
            DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'Материалы','type':'array','typeId':'{rowType.Id}'}}]}}")));

        var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));
        var instance = await m.Send(new AddDocumentToSetCommand(set.Id, docType.Id));

        var svc = Svc(scope);
        var file = await svc.UploadFileAsync(new UploadFileInput(
            Encoding.UTF8.GetBytes("A\nКабель\n"), "mats.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, TestAccess.All, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id,
            new CreateSourceInput("Материалы", candidate.SheetOrPath, null), TestAccess.All, default);
        await svc.SetMaterializationAsync(source.Id, rowType.Id,
            new Dictionary<string, string> { ["Наименование"] = "A" }, discriminator: null, byIdColumn: null, default);
        await svc.CreateBindingAsync(new CreateBindingInput(instance.Id, source.Id, "Материалы", null), default);

        // Настройка ставится штатной службой — той же, что зовёт клиент: она принимает отбор
        // объектом и складывает как есть, не проверяя ни операторов, ни вида узлов.
        await svc.SetSourceProcessingAsync(source.Id,
            new SetSourceProcessingInput(JsonDocument.Parse(filterJson).RootElement, null, null), default);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Негодный отбор ЛЁГ в базу — значит, состояние достижимо, а не выдумано тестом. Дословно
        // текст не сверяем: jsonb переписывает его по-своему (пробелы, порядок ключей в объекте,
        // запись не-ASCII), и сравнение ловило бы формат хранения вместо самого факта.
        var stored = (await db.DataSetSources.AsNoTracking().FirstAsync(s => s.Id == source.Id)).RowFilter;
        Assert.NotNull(stored);

        return new Seed(instance.Id, source.Id, source.Name);
    }

    [Fact]
    public async Task Печатная_форма_снимается_с_выпуска_а_не_печатает_пустую_таблицу()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope, UnknownOp);

        var inst = await M(scope).Send(new GetDocumentInstanceQuery(seed.InstanceId));
        var view = DocumentView.From(inst!);
        var ctx = await scope.ServiceProvider.GetRequiredService<IEntityResolver>().ResolveAsync(view);
        var diagnostics = new List<ResolutionDiagnostic>();
        await scope.ServiceProvider.GetRequiredService<IDataSetResolver>()
            .InjectAsync(ctx, view, TestAccess.All, diagnostics, default);

        // Error, а не Warning: любая ошибка снимает документ с выпуска целиком
        // (GenerateDocumentHandler бросает ResolutionValidationException). Пустая таблица в
        // подписанном акте — та же неправда, что лишние строки, только тише.
        var d = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("Материалы", d.Path);

        // Наш текст доходит ДОСЛОВНО и называет источник. Прежняя приставка «Источник данных
        // недоступен» отправила бы человека проверять хранилище: источник доступен, негодна настройка.
        Assert.Contains(seed.SourceName, d.Message);
        Assert.Contains("Отбор строк", d.Message);
        Assert.Contains("betwen", d.Message);   // условие названо, а не «что-то с источником»
        Assert.DoesNotContain("недоступен", d.Message);

        // Значения в контекст не попали: молча-неполная таблица хуже отсутствующей.
        Assert.False(ctx.Data.ContainsKey("Материалы"));
    }

    [Fact]
    public async Task Предпросмотр_отказывает_а_не_показывает_все_строки()
    {
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope, UnknownOp);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => Svc(scope).PreviewSourceAsync(seed.SourceId, 50, TestAccess.All, default));
        Assert.Contains(seed.SourceName, refusal.Message);
    }

    [Fact]
    public async Task Выгрузка_отказывает_тем_же_текстом()
    {
        // Выгрузка уходит из системы и живёт своей жизнью: файл со ВСЕМИ строками вместо отобранных
        // не отличить от правильного ни по виду, ни по содержимому.
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope, UnknownOp);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => Svc(scope).ExportSourceAsync(seed.SourceId, "xlsx", TestAccess.All, default));
        Assert.Contains(seed.SourceName, refusal.Message);
    }
    [Fact]
    public async Task Агент_видит_ПРИЧИНУ_а_не_пустое_число_строк()
    {
        // Табличное поле документа в ответе MCP: прежде отказ чтения и «значения нет» приходили
        // агенту одинаково — пустым rowCount, — и сверка объявила бы таблицу пустой при живых
        // строках (ревью PR #1058). Теперь рядом стоит причина.
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope, UnknownOp);

        var doc = await scope.ServiceProvider.GetRequiredService<IDomainSnapshotService>()
            .GetDocumentAsync(seed.InstanceId, TestAccess.All, ct: default);

        var table = Assert.Single(doc!.TableFields, t => t.Key == "Материалы");
        Assert.Null(table.RowCount);
        Assert.NotNull(table.RowsError);
        Assert.Contains("betwen", table.RowsError);          // наш отказ доходит дословно
        Assert.Contains(seed.SourceName, table.RowsError);
    }

    [Fact]
    public async Task Чужая_форма_описания_тоже_отказ()
    {
        // Годный JSON, но не дерево условий: отбор, сохранённый строкой. В базу такое ложится (jsonb
        // проверяет синтаксис, а не смысл), а прежде разбор падал — и падение считалось «отбора нет».
        using var scope = fixture.Services.CreateScope();
        var seed = await SeedAsync(scope, "\"A = Кабель\"");

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => Svc(scope).PreviewSourceAsync(seed.SourceId, 50, TestAccess.All, default));
        Assert.Contains(seed.SourceName, refusal.Message);
    }
}
