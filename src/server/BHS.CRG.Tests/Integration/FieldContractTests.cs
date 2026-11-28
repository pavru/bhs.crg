using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Branding;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Work.Devices;
using BHS.CRG.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

internal static class FirstFieldClient
{
    private static readonly JsonElement Snapshot = JsonSerializer.Deserialize<JsonElement>(
        File.ReadAllText(RepoPaths.File("docs", "field", "client-v1.json")));

    internal static JsonElement Operation(string name) => Snapshot.GetProperty("operations").GetProperty(name);
    internal static JsonElement WorkDisabled => Snapshot.GetProperty("workDisabled");
    internal static JsonElement InfoRateLimited => Snapshot.GetProperty("infoRateLimited");
    internal static JsonElement DefaultInfo => Snapshot.GetProperty("defaultInfo");

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, string name)
    {
        var saved = Operation(name).GetProperty("request");
        using var request = new HttpRequestMessage(new HttpMethod(saved.GetProperty("method").GetString()!),
            saved.GetProperty("path").GetString());
        if (saved.TryGetProperty("body", out var body))
            request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    // Клиент читает обязательные поля. Сервер может добавить другие поля в ответ.
    internal static async Task<JsonElement> ReadAsync(HttpResponseMessage response, JsonElement example)
    {
        Assert.Equal(example.GetProperty("status").GetInt32(), (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var actual = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var property in example.GetProperty("response").EnumerateObject())
        {
            var value = actual.GetProperty(property.Name);
            if (property.Name == "protocolVersions")
            {
                Assert.Contains(1, value.EnumerateArray().Select(version => version.GetInt32()));
                continue;
            }
            if (property.Name == "minimumSenderVersionCode")
            {
                Assert.InRange(value.GetInt32(), 1, int.MaxValue);
                continue;
            }
            Assert.True(JsonElement.DeepEquals(property.Value, value),
                $"Изменилось обязательное поле {property.Name}: {actual}");
        }
        return actual;
    }

    internal static RouteEndpoint[] Routes(IServiceProvider services) =>
        EndpointInventory.Routes(services, includeRefusals: true)
            .Where(e => EndpointInventory.Matches(EndpointInventory.Route(e), "/api/field")).ToArray();
}

[Collection(WorkOnlyCollection.Name)]
public class FieldContractTests(WorkOnlyHost host)
{
    [Fact]
    public void Common_gate_inventory_accounts_for_public_field_routes() =>
        EndpointGateInventoryTests.AssertInventory(host);

    [Fact]
    public void Only_named_public_operations_and_the_logo_are_mapped_in_the_field_group()
    {
        using var client = host.CreateClient();
        var routes = FirstFieldClient.Routes(host.Services)
            .Where(endpoint => !EndpointInventory.Matches(EndpointInventory.Route(endpoint), "/api/field/v1")).ToArray();
        Assert.Equal(["GET /api/field/info", "GET /api/field/info/logo", "GET /api/field/ping",
                "POST /api/field/join", "POST /api/field/refresh"],
            routes.Select(e => $"{EndpointInventory.Verbs(e)} {EndpointInventory.Route(e)}").Order());
        Assert.All(routes, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>()));
    }

    [Fact]
    public async Task Report_exchange_routes_require_the_field_device_scheme()
    {
        using var client = host.CreateClient();
        var routes = FirstFieldClient.Routes(host.Services).Where(endpoint =>
            EndpointInventory.Matches(EndpointInventory.Route(endpoint), FieldReportEndpoints.Path)).ToArray();
        Assert.Equal(5, routes.Length);
        Assert.All(routes, endpoint =>
        {
            Assert.True(EndpointInventory.IsGated(endpoint));
            Assert.Equal(FieldReportEndpoints.DevicePolicy,
                Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        });
        var provider = host.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await provider.GetPolicyAsync(FieldReportEndpoints.DevicePolicy);
        Assert.Equal(FieldDeviceAuthentication.SchemeName, Assert.Single(policy!.AuthenticationSchemes));
        using var refused = await client.GetAsync(FieldReportEndpoints.Path);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Saved_info_reads_default_and_configured_instance_names()
    {
        using var client = host.CreateClient();
        using var scope = host.Services.CreateScope();
        var branding = scope.ServiceProvider.GetRequiredService<IBrandingService>();
        using var initial = await FirstFieldClient.SendAsync(client, "info");
        await FirstFieldClient.ReadAsync(initial, FirstFieldClient.DefaultInfo);
        try
        {
            await branding.SetProductNameAsync("Пример-Строй");
            using var configured = await FirstFieldClient.SendAsync(client, "info");
            await FirstFieldClient.ReadAsync(configured, FirstFieldClient.Operation("info"));
            Assert.True(configured.Headers.CacheControl?.NoStore);
        }
        finally
        {
            await branding.SetProductNameAsync(null);
        }
    }

    [Fact]
    public async Task Logo_resource_serves_the_configured_file_under_the_field_prefix()
    {
        using var client = host.CreateClient();
        using var scope = host.Services.CreateScope();
        var branding = scope.ServiceProvider.GetRequiredService<IBrandingService>();
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        try
        {
            await branding.SetLogoAsync(png, "logo.png", "image/png");
            using var response = await FirstFieldClient.SendAsync(client, "info");
            response.EnsureSuccessStatusCode();
            var info = await response.Content.ReadFromJsonAsync<JsonElement>();
            var logo = info.GetProperty("logoUrl").GetString();
            Assert.Equal("/api/field/info/logo", logo);
            using var image = await client.GetAsync(logo);
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
            Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
            Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await branding.RemoveLogoAsync();
        }
        using var missing = await client.GetAsync("/api/field/info/logo");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var failure = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("logo-unavailable", failure.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Saved_ping_reaches_Work_without_an_office_session()
    {
        using var client = host.CreateClient();
        using var response = await FirstFieldClient.SendAsync(client, "ping");
        await FirstFieldClient.ReadAsync(response, FirstFieldClient.Operation("ping"));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("join")]
    [InlineData("refresh")]
    public async Task Saved_session_refusals_keep_their_wire_shape(string name)
    {
        using var client = host.CreateClient();
        var refusals = FirstFieldClient.Operation(name).GetProperty("refusals");
        using var unknown = await FirstFieldClient.SendAsync(client, name);
        await FirstFieldClient.ReadAsync(unknown, refusals[1]);
        using var malformed = await client.PostAsync($"/api/field/{name}",
            new StringContent("{", Encoding.UTF8, "application/json"));
        await FirstFieldClient.ReadAsync(malformed, refusals[0]);
    }
}

[Collection(PlanOnlyCollection.Name)]
public class FieldContractWorkOffTests(PlanOnlyHost host)
{
    [Fact]
    public void Common_gate_inventory_accounts_for_disabled_field_routes() =>
        EndpointGateInventoryTests.AssertInventory(host);

    [Fact]
    public async Task Both_prefixes_keep_the_module_refusal_and_all_saved_requests_can_read_its_reason()
    {
        using var client = host.CreateClient();
        var routes = FirstFieldClient.Routes(host.Services);
        Assert.Equal(["/api/field", "/api/field/{**rest}"], routes.Select(EndpointInventory.Route).Order());
        Assert.All(routes, endpoint =>
        {
            Assert.Equal("work", endpoint.Metadata.GetMetadata<DisabledModuleEndpoint>()?.Code);
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        });

        foreach (string operation in new[] { "info", "ping", "join", "refresh" })
        {
            using var response = await FirstFieldClient.SendAsync(client, operation);
            var actual = await FirstFieldClient.ReadAsync(response, FirstFieldClient.WorkDisabled);
            Assert.False(string.IsNullOrWhiteSpace(actual.GetProperty("error").GetString()));
        }

        using var office = await client.GetAsync("/api/work/assignments");
        Assert.Equal(HttpStatusCode.NotImplemented, office.StatusCode);
        var refusal = await FirstFieldClient.ReadAsync(office, FirstFieldClient.WorkDisabled);
        Assert.Contains("не подключён", refusal.GetProperty("error").GetString());
    }
}
