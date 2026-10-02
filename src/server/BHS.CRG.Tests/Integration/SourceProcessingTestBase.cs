using System.Text;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
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

    /// <summary>Тело — дословно: какие поля в нём есть, а каких нет, тестами и проверяется.</summary>
    protected static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, string body) =>
        client.PutAsync($"/api/datasets/sources/{id}/processing", new StringContent(body, Encoding.UTF8, "application/json"));

    /// <summary>Правка одного отбора — как её шлёт диалог отбора.</summary>
    protected static Task<HttpResponseMessage> PutFilterAsync(HttpClient client, Guid id, string rowFilter) =>
        PutAsync(client, id, $$"""{"rowFilter":{{rowFilter}}}""");

    /// <summary>Источник так, как он лежит в базе.</summary>
    protected async Task<DataSetSource> StoredAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DataSetSources.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    /// <summary>Отбор — в базу мимо службы: так лежит сохранённое до #1137 и восстановленное из копии.</summary>
    protected async Task StoreAsync(Guid id, string rowFilter)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.DataSetSources.FirstAsync(s => s.Id == id)).SetProcessing(rowFilter, null, null);
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
