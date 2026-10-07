using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Установка «только счета» распознаёт без модуля исполнительной документации, а его профили на ней
/// не отдаются (ТЗ CORE-Q6, задача B1a, issue #1075).
///
/// <para>Сторожит не один список. Профиль выбирают в двух местах — в общем списке профилей и при
/// выборе профиля PDF у набора данных, — а читают им из четырёх: адрес, инструмент MCP, фоновая
/// задача, старый путь по источнику. Проверка одного списка была бы зелёной при любой из остальных
/// дверей нараспашку, поэтому здесь проверяется и список, и выбор, и само чтение.</para>
///
/// <para>Хост — настоящее приложение с <c>Modules__Enabled=costs</c> на своей базе: поддельный каталог
/// проверил бы механизм, а не то, что ядро не опирается на модуль, которого может не быть.</para>
/// </summary>
public class RecognitionWithoutIdModuleTests(CostsOnlyHost host) : IClassFixture<CostsOnlyHost>
{
    private static readonly string[] IdCodes =
    [
        IdRecognitionProfiles.TitleBlockCode, IdRecognitionProfiles.CoverTitleCode,
        IdRecognitionProfiles.SpecificationTableCode, IdRecognitionProfiles.CableJournalCode,
    ];

    private Task<HttpClient> AdminAsync() => GrantedSignIn.WithPermissionsAsync(
        host, CorePermissions.DataSetsRead, CorePermissions.DataSetsEdit, CorePermissions.RecognitionSettings);

    /// <summary>
    /// Общий список и перечень видов: профилей и видов выключенного модуля в них нет, «Счёт на
    /// оплату» — есть.
    ///
    /// Ломается, если вернуть профиль ИД в общий список: убрать отбор по владельцу в обработчике
    /// списка или объявить профиль штампа у ядра.
    /// </summary>
    [Fact]
    public async Task Profiles_and_kinds_of_the_disabled_module_are_not_offered()
    {
        var client = await AdminAsync();

        var profiles = await client.GetFromJsonAsync<JsonElement>("/api/recognition-profiles");
        var codes = profiles.EnumerateArray().Select(p => p.GetProperty("code").GetString()).ToList();
        Assert.Contains(CoreRecognitionProfiles.InvoiceCode, codes);
        Assert.Empty(codes.Intersect(IdCodes));

        var kinds = await client.GetFromJsonAsync<JsonElement>("/api/recognition-profiles/kinds");
        Assert.Equal(["Invoice"], kinds.EnumerateArray().Select(k => k.GetProperty("kind").GetString()));
    }

    /// <summary>
    /// Профили выключенного модуля лежат в базе и подписаны его кодом: выключение их не удаляет, и
    /// включённый обратно модуль найдёт их там же — вместе с правками администратора.
    ///
    /// <para>Строки сначала удаляются, и заводит их сидер ЭТОГО хоста: база у хоста своя и живёт
    /// между прогонами, поэтому «строки на месте» само по себе не говорит ничего — они могли остаться
    /// от прошлого раза (поймано поломкой: сидер, обходящий выключенных владельцев, проходил).</para>
    /// </summary>
    [Fact]
    public async Task Profiles_of_the_disabled_module_are_seeded_and_stay_in_the_database()
    {
        _ = host.CreateClient();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.RecognitionProfiles.Where(p => p.Code == IdRecognitionProfiles.CableJournalCode).ExecuteDeleteAsync();

        await scope.ServiceProvider.GetRequiredService<IRecognitionProfileProvider>().ReseedBuiltInAsync();

        var stored = await db.RecognitionProfiles.AsNoTracking().Where(p => p.Code != null).ToListAsync();

        Assert.All(IdCodes, code => Assert.Equal("id", stored.Single(p => p.Code == code).Module));
        Assert.Equal(RecognitionProfileCatalog.CoreOwner,
            stored.Single(p => p.Code == CoreRecognitionProfiles.InvoiceCode).Module);
    }

    /// <summary>
    /// Профиль выключенного модуля нельзя ни править, ни сбросить, ни удалить по прямому
    /// идентификатору, а свой профиль его вида — создать. Отказ называет модуль.
    /// </summary>
    [Fact]
    public async Task Hidden_profile_refuses_direct_edits_and_names_the_module()
    {
        var client = await AdminAsync();
        Guid stampId;
        using (var scope = host.Services.CreateScope())
            stampId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().RecognitionProfiles
                .AsNoTracking().SingleAsync(p => p.Code == IdRecognitionProfiles.TitleBlockCode)).Id;

        var body = new { name = "Штамп", kind = "TitleBlock", fields = new[] { new { name = "Шифр" } } };

        await AssertNamesModuleAsync(await client.PutAsJsonAsync($"/api/recognition-profiles/{stampId}", body));
        await AssertNamesModuleAsync(await client.PostAsync($"/api/recognition-profiles/{stampId}/reset", null));
        await AssertNamesModuleAsync(await client.DeleteAsync($"/api/recognition-profiles/{stampId}"));
        await AssertNamesModuleAsync(await client.PostAsJsonAsync("/api/recognition-profiles", body));
    }

    /// <summary>
    /// Второй список — профиль PDF у набора данных. «ГОСТ» требует вида выключенного модуля и
    /// отвечает отказом; «счёт» выбирается. Неизвестное название — отказ, а не молчаливый ГОСТ:
    /// прежде всё, что не «счёт», становилось ГОСТом.
    /// </summary>
    [Fact]
    public async Task Pdf_profile_of_the_disabled_module_cannot_be_chosen()
    {
        var client = await AdminAsync();
        var fileId = await SeedPdfAsync(profile: null);

        await AssertNamesModuleAsync(await client.PostAsJsonAsync(
            $"/api/datasets/files/{fileId}/pdf-sources", new { name = "x", profile = PdfProfiles.GostTitleBlock }));

        var unknown = await client.PostAsJsonAsync(
            $"/api/datasets/files/{fileId}/pdf-sources", new { name = "x", profile = "gost" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Неизвестный профиль", await unknown.Content.ReadAsStringAsync());

        var invoice = await client.PostAsJsonAsync(
            $"/api/datasets/files/{fileId}/pdf-sources", new { name = "x", profile = PdfProfiles.Invoice });
        Assert.Equal(HttpStatusCode.NoContent, invoice.StatusCode);
    }

    /// <summary>
    /// Набор, которому профиль ГОСТ выбрали, пока модуль был включён: запуск распознавания — отказ
    /// с названием модуля ДО постановки задачи, а не строка в журнале задач через минуту.
    /// </summary>
    [Fact]
    public async Task Recognition_by_a_profile_of_the_disabled_module_refuses_before_the_job()
    {
        var client = await AdminAsync();
        var fileId = await SeedPdfAsync(PdfProfiles.GostTitleBlock);

        await AssertNamesModuleAsync(await client.PostAsync($"/api/datasets/files/{fileId}/recognize", null));

        using var scope = host.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs
            .AnyAsync(j => j.TargetId == fileId));
    }

    /// <summary>
    /// Признак готовности задачи: путь «скан → результат» работает без модуля ИД. Счёт распознаётся,
    /// его источники получают строки, и поставщика профилей ни разу не спросили о виде, которым
    /// владеет выключенный модуль.
    ///
    /// Последнее — не украшение: спроси ядро по дороге профиль штампа «на всякий случай», оно
    /// получило бы отказ и уронило бы распознавание счёта на установке без модуля ИД.
    /// </summary>
    [Fact]
    public async Task Invoice_is_recognized_without_the_id_module()
    {
        _ = host.CreateClient();
        var fileId = await SeedPdfAsync(PdfProfiles.Invoice, PdfProfiles.InvoiceHeaderMarker, PdfProfiles.InvoiceLineItemsMarker);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asked = new AskedProvider(scope.ServiceProvider.GetRequiredService<IRecognitionProfileProvider>());
        var service = new DataSetPdfRecognitionService(
            db, scope.ServiceProvider.GetRequiredService<IBlobStorage>(), new InvoiceRecognizer(),
            scope.ServiceProvider.GetRequiredService<INotificationService>(), asked,
            NullLogger<DataSetPdfRecognitionService>.Instance);

        await service.RecognizeFileAsync(fileId, confirm: false, default);

        db.ChangeTracker.Clear();
        var sources = await db.DataSetSources.AsNoTracking().Where(s => s.FileId == fileId).ToListAsync();
        Assert.Contains("СЧ-17", sources.Single(s => s.SheetOrPath == PdfProfiles.InvoiceHeaderMarker).CachedData);
        Assert.Contains("Кабель", sources.Single(s => s.SheetOrPath == PdfProfiles.InvoiceLineItemsMarker).CachedData);
        Assert.Equal([RecognitionProfileKind.Invoice], asked.Kinds.Distinct());
    }

    /// <summary>
    /// Предикат «это таблица?» ворот НЕ спрашивает: по нему решается, осиротел ли источник таблицы, и
    /// там по ответу удаляются данные. Ответь выключенный модуль «не таблица» — и его выключение
    /// снесло бы источники. А взять профиль по тому же тэгу, чтобы распознавать, нельзя.
    /// </summary>
    [Fact]
    public async Task Table_predicate_survives_the_disabled_module_but_reading_does_not()
    {
        _ = host.CreateClient();
        using var scope = host.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IRecognitionProfileProvider>();
        const string tag = Domain.Schema.FunctionalTag.GostDocSpecification;

        Assert.True(provider.IsTableTag(tag));
        Assert.True(await provider.IsTableGroupAsync(profileId: null, [tag]));

        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => provider.GetForTagAsync(tag));
        Assert.Contains("Исполнительная документация", refusal.Message);
    }

    /// <summary>
    /// Профиль выключенного модуля, привязанный к набору или группе листов раньше: поставщик отвечает
    /// отказом, а не «такого нет». По «нет» потребитель взял бы заводской профиль вида — и прочитал
    /// бы документ не теми параметрами, которые выбрал человек, не сказав об этом никому.
    /// </summary>
    [Fact]
    public async Task Bound_profile_of_the_disabled_module_refuses_instead_of_falling_back()
    {
        _ = host.CreateClient();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stamp = await db.RecognitionProfiles.AsNoTracking()
            .SingleAsync(p => p.Code == IdRecognitionProfiles.TitleBlockCode);
        var provider = scope.ServiceProvider.GetRequiredService<IRecognitionProfileProvider>();

        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => provider.GetByIdAsync(stamp.Id));

        Assert.Contains("Исполнительная документация", refusal.Message);
        Assert.Null(await provider.GetByIdAsync(Guid.NewGuid()));
    }

    private static async Task AssertNamesModuleAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {text}");
        Assert.Contains("Исполнительная документация", text);
        Assert.Contains("выключен", text);
    }

    private async Task<Guid> SeedPdfAsync(string? profile, params string[] markers)
    {
        _ = host.CreateClient();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var pdf = new MemoryStream();
        using (var doc = new SharpPdfDocument())
        {
            doc.AddPage();
            doc.Save(pdf, false);
        }
        pdf.Position = 0;
        var blobPath = await scope.ServiceProvider.GetRequiredService<IBlobStorage>()
            .UploadAsync("test.pdf", pdf, "application/pdf");

        var file = DataSetFile.Create("Набор", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        file.SetPreprocessingProfile(profile);
        db.DataSetFiles.Add(file);
        foreach (var marker in markers) db.DataSetSources.Add(file.AddSource("Источник", marker, "[]", 0));
        await db.SaveChangesAsync();
        return file.Id;
    }

    private sealed class InvoiceRecognizer : IDocumentRecognizer
    {
        public Task<RecognitionResult> RecognizeAsync(
            byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
            Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default) =>
            Task.FromResult(new RecognitionResult(new Dictionary<string, string?>
            {
                ["НомерСчёта"] = "СЧ-17",
                [InvoiceFields.LineItemsPath] = """[{"Наименование":"Кабель","Количество":"3"}]""",
            }, null));
    }

    /// <summary>Поставщик профилей приложения, который записывает, о каких видах его спросили.</summary>
    private sealed class AskedProvider(IRecognitionProfileProvider inner) : IRecognitionProfileProvider
    {
        public List<RecognitionProfileKind> Kinds { get; } = [];

        public Task<ResolvedRecognitionProfile> GetDefaultAsync(RecognitionProfileKind kind, CancellationToken ct = default)
        {
            Kinds.Add(kind);
            return inner.GetDefaultAsync(kind, ct);
        }

        public void RequireKind(RecognitionProfileKind kind)
        {
            Kinds.Add(kind);
            inner.RequireKind(kind);
        }

        public Task<ResolvedRecognitionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);
        public Task<ResolvedRecognitionProfile?> GetForTagAsync(string tag, CancellationToken ct = default) => inner.GetForTagAsync(tag, ct);
        public bool IsTableTag(string tag) => inner.IsTableTag(tag);
        public Task<bool> IsTableGroupAsync(Guid? profileId, IReadOnlyList<string>? tags, CancellationToken ct = default) =>
            inner.IsTableGroupAsync(profileId, tags, ct);
        public RecognitionKindInfo DescribeKind(RecognitionProfileKind kind) => inner.DescribeKind(kind);
        public IReadOnlyList<RecognitionKindInfo> ListKinds() => inner.ListKinds();
        public Task ReseedBuiltInAsync(CancellationToken ct = default) => inner.ReseedBuiltInAsync(ct);
    }
}
