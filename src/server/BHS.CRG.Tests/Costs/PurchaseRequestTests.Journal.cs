using System.Net;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Costs;

public sealed partial class PurchaseRequestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Такт_доставляет_причину_возврата_после_сбоя_даже_если_черновик_удалён(bool afterAppend)
    {
        var (site, material, item, _) = await SeedAsync(100);
        using var author = await SupplierAsync();
        var draft = await CreateAsync(author, site.Construction, item, material);
        var path = Address(draft);
        var submitted = await WriteAsync(author, HttpMethod.Post, path + "/submit", Version(draft), null, HttpStatusCode.OK);

        using var failing = HostWithFailingDelivery(afterAppend);
        using var client = failing.CreateClient();
        client.DefaultRequestHeaders.Authorization = author.DefaultRequestHeaders.Authorization;
        const string reason = "Уточнить количество кабеля";
        var returned = await WriteAsync(client, HttpMethod.Post, path + "/return",
            Version(submitted), new { reason }, HttpStatusCode.OK);
        Assert.Equal("draft", returned.GetProperty("status").GetString());
        await WriteAsync(author, HttpMethod.Delete, path, Version(returned), null, HttpStatusCode.NoContent);

        using var scope = host.Services.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        var target = draft.GetProperty("id").GetString();
        var before = (await log.ExportAsync()).Where(row => row.TargetId == target && row.Action == "costs.request.returned");
        Assert.Equal(afterAppend ? 1 : 0, before.Count());

        var tick = Assert.Single(new CostsModule().Ticks);
        var handler = Assert.Single(scope.ServiceProvider.GetServices<IModuleJobHandler>(), job => job.Operation == tick.Operation);
        var run = new ModuleJobRun(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, null, (_, _, _) => Task.CompletedTask);
        await handler.RunAsync(run, CancellationToken.None);
        await handler.RunAsync(run, CancellationToken.None);

        var entry = Assert.Single(await log.ExportAsync(), row => row.TargetId == target && row.Action == "costs.request.returned");
        Assert.Equal(reason, entry.After);
        Assert.Equal(draft.GetProperty("createdBy").GetGuid(), entry.ActorId);
        var returnedAt = returned.GetProperty("updatedAt").GetDateTimeOffset();
        Assert.InRange(entry.OccurredAt, returnedAt.AddMicroseconds(-1), returnedAt.AddMicroseconds(1));
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> HostWithFailingDelivery(bool afterAppend) =>
        host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IModuleDeferredActivityLog>();
            services.AddScoped<IModuleDeferredActivityLog>(provider =>
                new FailingReturnDelivery(ActivatorUtilities.CreateInstance<ModuleActivityLogPort>(provider), afterAppend));
        }));

    private sealed class FailingReturnDelivery(IModuleDeferredActivityLog inner, bool afterAppend) : IModuleDeferredActivityLog
    {
        public async Task RecordDeferredAsync(ModuleActivityAction action, ModuleDeferredActivity saved, CancellationToken ct)
        {
            if (afterAppend) await inner.RecordDeferredAsync(action, saved, ct);
            throw new InvalidOperationException("Проверка повторной доставки причины возврата");
        }
    }
}
