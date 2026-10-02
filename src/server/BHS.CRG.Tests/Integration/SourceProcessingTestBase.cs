using System.Text;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Общее у тестов правки обработки источника (issue #1137, #1139): один способ послать правку, один —
/// посмотреть, что легло в базу, и один — положить в базу мимо службы. Раньше у каждого класса были
/// свои, под одними именами и с разными сигнатурами.
/// </summary>
public abstract class SourceProcessingTestBase(InvoiceLineHost host) : ModuleTableSeededTests(host)
{
    private readonly InvoiceLineHost host = host;

    /// <summary>Тело — дословно, без единой добавки: так проверяется и запрос, не назвавший версию.</summary>
    protected static Task<HttpResponseMessage> PutRawAsync(HttpClient client, Guid id, string body) =>
        client.PutAsync($"/api/datasets/sources/{id}/processing", new StringContent(body, Encoding.UTF8, "application/json"));

    /// <summary>
    /// Правка из диалога, открытого ТОЛЬКО ЧТО: тело — как написано, плюс версия обработки, какой она
    /// лежит в базе сейчас (issue #1141). Тело, которое не объект, уходит как есть — версию в него
    /// вписать некуда.
    /// </summary>
    protected async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, string body) =>
        await PutRawAsync(client, id, WithVersion(body, await VersionAsync(id)));

    /// <summary>Правка одного отбора — как её шлёт диалог отбора.</summary>
    protected Task<HttpResponseMessage> PutFilterAsync(HttpClient client, Guid id, string rowFilter) =>
        PutAsync(client, id, $$"""{"rowFilter":{{rowFilter}}}""");

    /// <summary>Версия обработки источника, каким он лежит в базе.</summary>
    protected async Task<string> VersionAsync(Guid id) => SourceProcessingVersion.Of(await StoredAsync(id));

    /// <summary>Версия — первым полем тела; остальное в нём остаётся дословным.</summary>
    protected static string WithVersion(string body, string version)
    {
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('{')) return body;
        var rest = trimmed[1..].TrimStart();
        return $$"""{"ifMatch":"{{version}}"{{(rest.StartsWith('}') ? "" : ",")}}{{rest}}""";
    }

    /// <summary>Источник так, как он лежит в базе.</summary>
    protected async Task<DataSetSource> StoredAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DataSetSources.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    /// <summary>Отбор — в базу мимо службы: так лежит сохранённое до #1137 и восстановленное из копии.</summary>
    protected Task StoreAsync(Guid id, string rowFilter) => ChangeAsync(id, s => s.SetProcessing(rowFilter, null, null));

    /// <summary>Правка источника в базе мимо службы — так его меняют распознавание, кэш и прочие соседи.</summary>
    protected async Task ChangeAsync(Guid id, Action<DataSetSource> change)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        change(await db.DataSetSources.FirstAsync(s => s.Id == id));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Вызов службы от имени пользователя — каждый в СВОЕЙ области: в общей контекст базы отдал бы
    /// источник из памяти, каким тот был до правки мимо службы, и проверка шла бы не по тому отбору.
    /// </summary>
    protected async Task<T> AsAsync<T>(Guid user, Func<IDataSetService, DataAccess, Task<T>> call)
    {
        using var scope = host.Services.CreateScope();
        var access = await scope.ServiceProvider.GetRequiredService<DataAccessResolver>().ForUserAsync(user, default);
        return await call(scope.ServiceProvider.GetRequiredService<IDataSetService>(), access);
    }
}
