using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Обработка источника правится по частям (issue #1139): отбор, вычисляемые колонки и сортировка —
/// каждая сама по себе. Чего в запросе нет, то остаётся как есть; присланное пустым — сбрасывается.
///
/// <para>До этого запрос нёс обработку целиком, и диалог сортировки досылал отбор из своей копии
/// источника на странице. Копия устаревала — и сохранение сортировки молча затирало отбор, который
/// тем временем поправил другой человек. Отказа при этом не было: ответ «сохранено» был правдой.</para>
/// </summary>
public sealed class SourceProcessingPartsTests(InvoiceLineHost host) : SourceProcessingTestBase(host)
{
    private const string Sort = """[{"column":"Номер","direction":"asc"}]""";
    private const string Computed = """[{"alias":"К","expr":"1"}]""";

    private static string Between(int from, int to) =>
        $$"""{"type":"group","logic":"and","children":[{"type":"condition","column":"Итого","op":"between","values":["{{from}}","{{to}}"]}]}""";

    /// <summary>
    /// Правка одной части не трогает остальные — в том числе ту, что сменилась между двумя правками.
    ///
    /// <para>Правку с УСТАРЕВШЕЙ страницы тест больше не изображает: с issue #1141 она называет версию и
    /// получает отказ (<see cref="SourceProcessingConflictTests" />). Здесь — что остаётся правдой для
    /// правки со свежей страницы и для входов без версии: чего в запросе нет, то не тронуто.</para>
    /// </summary>
    [Fact]
    public async Task Правка_сортировки_не_трогает_отбор_и_вычисляемые_колонки()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        await OkAsync(await PutAsync(client, id, $$"""{"rowFilter":{{Between(80, 110)}},"computedColumns":{{Computed}}}"""));
        // Отбор сменился уже после первой правки — в запросе сортировки его нет вовсе.
        await OkAsync(await PutAsync(client, id, $$"""{"rowFilter":{{Between(1, 2)}}}"""));

        var sorted = await PutAsync(client, id, $$"""{"sortSpec":{{Sort}}}""");
        await OkAsync(sorted);

        var stored = await StoredAsync(id);
        Assert.Contains("\"1\"", stored.RowFilter);
        Assert.DoesNotContain("110", stored.RowFilter);
        Assert.Contains("К", stored.ComputedColumns);
        Assert.Contains("Номер", stored.SortSpec);
        // И в ответе — источник, каким он стал, со всеми тремя частями.
        var answered = await sorted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, answered.GetProperty("rowFilter").ValueKind);
        Assert.Equal(1, answered.GetProperty("computedColumns").GetArrayLength());
    }

    [Fact]
    public async Task Часть_присланная_пустой_сбрасывается_а_остальные_остаются()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        await OkAsync(await PutAsync(client, id,
            $$"""{"rowFilter":{{Between(80, 110)}},"computedColumns":{{Computed}},"sortSpec":{{Sort}}}"""));

        await OkAsync(await PutAsync(client, id, """{"rowFilter":null}"""));

        var stored = await StoredAsync(id);
        Assert.Null(stored.RowFilter);
        Assert.NotNull(stored.ComputedColumns);
        Assert.NotNull(stored.SortSpec);

        // Имена полей — без оглядки на регистр, как их читала привязка к записи.
        await OkAsync(await PutAsync(client, id, """{"SortSpec":null,"COMPUTEDCOLUMNS":null}"""));
        stored = await StoredAsync(id);
        Assert.Null(stored.ComputedColumns);
        Assert.Null(stored.SortSpec);
    }

    /// <summary>
    /// При правке по частям опечатка в имени поля дала бы «сохранено», не сохранив ничего, — раньше
    /// она сбрасывала часть, и это было видно. Поэтому запрос, из которого взять нечего, — отказ.
    /// </summary>
    [Theory]
    [InlineData("{}", "менять нечего")]
    [InlineData("""{"rowFiltr":null}""", "«rowFiltr»")]
    [InlineData("""{"sortSpec":null,"filter":null}""", "«filter»")]
    [InlineData("""{"sortSpec":null,"SortSpec":[]}""", "прислано дважды")]
    [InlineData("[]", "не объект")]
    public async Task Запрос_из_которого_взять_нечего_отклоняется_и_источник_цел(string body, string named)
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        await OkAsync(await PutAsync(client, id, $$"""{"rowFilter":{{Between(80, 110)}},"sortSpec":{{Sort}}}"""));

        var refused = await PutAsync(client, id, body);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(named, await refused.Content.ReadAsStringAsync());
        var stored = await StoredAsync(id);
        Assert.NotNull(stored.RowFilter);
        Assert.NotNull(stored.SortSpec);
    }

    /// <summary>
    /// Правка без отбора к поставщику не идёт вовсе: проверять нечего. Кому источник закрыт, тот
    /// сортировку всё равно поправит — как и снимет отбор (см. <see cref="SourceFilterOnSaveTests" />).
    ///
    /// <para>Отбор у источника при этом ЕСТЬ, и годный по форме: без него проверка вышла бы раньше,
    /// чем дошла до поставщика, и тест был бы зелёным при любом коде службы.</para>
    /// </summary>
    [Fact]
    public async Task Правка_без_отбора_поставщика_не_спрашивает()
    {
        var (admin, _) = await SignInAsync("Admin");
        var id = await SourceAsync(admin);
        await StoreAsync(id, Between(80, 110));
        var (_, outsider) = await SignInAsync("Installer");

        var saved = await AsAsync(outsider, (svc, access) => svc.SetSourceProcessingAsync(
            id, new SetSourceProcessingInput { SortSpec = ProcessingPart.Of(JsonDocument.Parse(Sort).RootElement) }, access, default));

        Assert.NotNull(saved);
        var stored = await StoredAsync(id);
        Assert.Contains("Номер", stored.SortSpec);
        Assert.Contains("110", stored.RowFilter);
    }

    /// <summary>
    /// Правку без единой части отклоняет сама служба, а не только разбор тела: входов больше одного,
    /// и «сохранено» в ответ на правку, из которой взять нечего, не должен получить ни один.
    /// </summary>
    [Fact]
    public async Task Правка_без_единой_части_отклоняется_службой()
    {
        var (client, user) = await SignInAsync("Admin");
        var id = await SourceAsync(client);
        await OkAsync(await PutAsync(client, id, $$"""{"sortSpec":{{Sort}}}"""));
        var before = (await StoredAsync(id)).UpdatedAt;

        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => AsAsync(user, (svc, access) =>
            svc.SetSourceProcessingAsync(id, new SetSourceProcessingInput(), access, default)));

        Assert.Contains("менять нечего", refusal.Message);
        Assert.Equal(before, (await StoredAsync(id)).UpdatedAt);
    }
}
