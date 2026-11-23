using System.Collections.Concurrent;
using System.Net.Http.Json;
using BHS.CRG.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BHS.CRG.Tests.Integration;

/// <summary>Два перекрывающихся HTTP-запроса записи используют разные mediator и DbContext.</summary>
[Collection("Integration")]
public class MediatorLifetimeTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Concurrent_writes_have_independent_scopes()
    {
        using var authenticated = await AccessProbe.SignInAsync(fixture, "Admin", "mediator_scope");
        var probe = new ScopeProbe();
        using var app = fixture.WithWebHostBuilder(builder =>
        {
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(probe));
        });
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = authenticated.DefaultRequestHeaders.Authorization;
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
            client.PostAsJsonAsync("/api/catalog/", new
            {
                entityType = "Organization", displayName = $"Mediator scope {index}", data = "{}",
            })));
        foreach (var response in responses)
        {
            using (response) response.EnsureSuccessStatusCode();
        }
        Assert.Equal(2, probe.Seen.Count);
        var seen = probe.Seen.ToArray();
        Assert.NotSame(seen[0].Mediator, seen[1].Mediator);
        Assert.NotEqual(seen[0].Context, seen[1].Context);
    }

    private sealed class ScopeProbe : IStartupFilter
    {
        internal readonly ConcurrentBag<(IMediator Mediator, Guid Context)> Seen = [];
        private readonly TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int started;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request is { Method: "POST" } && context.Request.Path == "/api/catalog/")
                {
                    var services = context.RequestServices;
                    Seen.Add((services.GetRequiredService<IMediator>(),
                        services.GetRequiredService<AppDbContext>().ContextId.InstanceId));
                    if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
                    await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                await continuation(context);
            });
            next(app);
        };
    }
}
