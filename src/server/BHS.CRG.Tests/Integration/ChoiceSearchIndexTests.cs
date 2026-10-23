using System.Data.Common;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Поиск «на выбор» идёт по триграммному индексу и отвечает тем же, что проход (issue #1247).
///
/// <para>⚠️ Обычный тест поиска до индекса не доходит: на десятках строк планировщик берёт проход, и
/// тест зелёный при любом индексе — и без него, и с негодным. Поэтому путь здесь принуждается, а план
/// читается — у НАСТОЯЩЕГО запроса хранилища, с его параметром, <c>ESCAPE</c> и отбором по типу и
/// фасете (ревью PR #1307): план рукописного запроса о настоящем не говорит ничего.</para>
/// </summary>
[Collection("Integration")]
public class ChoiceSearchIndexTests(IntegrationTestFixture fixture)
{
    private const string Index = "IX_domain_objects_SearchText";

    [Fact]
    public async Task Запрос_хранилища_идёт_по_индексу_и_отвечает_тем_же_что_проход()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (type, byName, byAlias) = await SeedAsync(tag);

        var plans = new PlanProbe();
        await using var db = Context(plans);
        await using var tx = await db.Database.BeginTransactionAsync();
        var repository = new DomainObjectRepository(db);

        // Проход и обход по ключу запрещены, индексы по типу сняты (до отката транзакции): из путей
        // к строкам остаётся индекс поиска. Не может запрос им воспользоваться — в плане его не будет.
        // Индексов по типу два: второй — окно подсказок (#1530), частичный по «не в архиве», и выбор с
        // тем же условием ходит им так же охотно, как первым.
        await db.Database.ExecuteSqlRawAsync("""
            SET LOCAL enable_seqscan = off; SET LOCAL enable_indexscan = off;
            DROP INDEX "IX_domain_objects_CompositeTypeId";
            DROP INDEX "IX_domain_objects_CompositeTypeId_UpdatedAt_Live";
            """);
        // Регистр набранного — третий, не совпадающий ни с названием, ни с именем.
        var viaIndex = await repository.SearchForChoiceAsync([type], $"кАбЕлЬ {tag}", 10);
        Assert.Contains(Index, plans.Last);

        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = on; SET LOCAL enable_bitmapscan = off");
        var viaScan = await repository.SearchForChoiceAsync([type], $"кАбЕлЬ {tag}", 10);
        Assert.DoesNotContain(Index, plans.Last);

        Assert.Equal([byName, byAlias], viaScan.Items.Select(i => i.Id));
        Assert.Equal(viaScan.Items, viaIndex.Items);
        Assert.Equal($"Кабель {tag} ВВГ", viaIndex.Items[1].MatchedAlias);
    }

    /// <summary>
    /// Образец из двух знаков триграмм не даёт — и обязан находить то же: порога длины нет, «ПВ» и
    /// «АС» — законные запросы. Каким путём — здесь не проверяется: на таблице теста выбор
    /// планировщика ничего не значит (замер на 200 000 строк — в DEV_NOTES).
    /// </summary>
    [Fact]
    public async Task Образец_из_двух_знаков_находит_по_названию_и_по_имени()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (type, byName, byAlias) = await SeedAsync(tag);
        await using var db = Context(new PlanProbe());

        var found = await new DomainObjectRepository(db).SearchForChoiceAsync([type], "кА", 10);

        Assert.Equal([byName, byAlias], found.Items.Select(i => i.Id));
    }

    /// <summary>
    /// Перевод строки в набранном не сшивает название с именем. В тексте поиска он — разделитель, и
    /// образец «конец названия + перевод строки + начало имени» совпал бы с записью, в которой
    /// набранного нет ни в названии, ни в одном имени.
    /// </summary>
    [Fact]
    public async Task Перевод_строки_в_запросе_не_находит_через_границу_названия_и_имени()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (type, _, _) = await SeedAsync(tag);
        await using var db = Context(new PlanProbe());

        // У второй записи: название «Провод {tag}», имя «Кабель {tag} ВВГ».
        var found = await new DomainObjectRepository(db).SearchForChoiceAsync([type], $"{tag}\nКабель", 10);

        Assert.Empty(found.Items);
    }

    /// <summary>
    /// Две записи одного типа: у первой слово в НАЗВАНИИ (заглавными), у второй — только в
    /// альтернативном имени. Имя вписано сырым запросом нарочно: текст поиска обязан следовать и за
    /// правкой мимо приложения — он генерируется базой.
    /// </summary>
    private async Task<(Guid Type, Guid ByName, Guid ByAlias)> SeedAsync(string tag)
    {
        using var scope = fixture.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var type = (await mediator.Send(new CreateDocumentTypeCommand(
            $"TRGM_{tag}", $"TRGM_{tag}", DocumentTypeKind.Composite, null, JsonDocument.Parse("""{"fields":[]}""")))).Id;

        async Task<Guid> EntryAsync(string name) => (await mediator.Send(new CreateCommonDataEntryCommand(
            name, type, JsonDocument.Parse("{}"), CatalogScope.System, null))).Id;

        var byName = await EntryAsync($"КАБЕЛЬ {tag} медный");
        var byAlias = await EntryAsync($"Провод {tag}");
        var alias = $"Кабель {tag} ВВГ";
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync(
            $"""UPDATE domain_objects SET "Aliases" = ARRAY[{alias}] WHERE "Id" = {byAlias}""");
        return (type, byName, byAlias);
    }

    /// <summary>Свой контекст на базу хоста — со своей транзакцией и с чтением планов.</summary>
    private AppDbContext Context(PlanProbe plans) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(fixture.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres"))
        .AddInterceptors(plans)
        .Options);

    /// <summary>
    /// Читает план запроса выбора перед тем, как тот уйдёт в базу: тот же текст, те же параметры, то
    /// же соединение и транзакция. Берётся запрос с отсечением — он выбирает строки; подсчёт архива
    /// идёт рядом и своим путём.
    /// </summary>
    private sealed class PlanProbe : DbCommandInterceptor
    {
        public string Last { get; private set; } = "";

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken ct = default)
        {
            if (!command.CommandText.Contains("\"SearchText\"") || !command.CommandText.Contains("LIMIT"))
                return result;

            await using var explain = command.Connection!.CreateCommand();
            explain.CommandText = "EXPLAIN " + command.CommandText;
            explain.Transaction = command.Transaction;
            foreach (NpgsqlParameter parameter in command.Parameters) explain.Parameters.Add(parameter.Clone());

            var lines = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) lines.Add(reader.GetString(0));
            Last = string.Join('\n', lines);
            return result;
        }
    }
}
