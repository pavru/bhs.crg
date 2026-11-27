using System.Net;
using BHS.CRG.Api.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BHS.CRG.Tests.Integration;

public class FieldContractRateLimitTests(FieldRateLimitHost host) : IClassFixture<FieldRateLimitHost>
{
    [Fact]
    public async Task Field_info_refusal_has_a_reason_and_leaves_office_branding_available()
    {
        using var client = host.CreateClient();
        for (int i = 0; i < BrandingRateLimit.Default; i++)
        {
            using var response = await FirstFieldClient.SendAsync(client, "info");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var limitedInfo = await FirstFieldClient.SendAsync(client, "info");
        await FirstFieldClient.ReadAsync(limitedInfo, FirstFieldClient.InfoRateLimited);
        Assert.True(limitedInfo.Headers.CacheControl?.NoStore);

        using var limitedLogo = await client.GetAsync("/api/field/info/logo");
        await FirstFieldClient.ReadAsync(limitedLogo, FirstFieldClient.InfoRateLimited);

        using var office = await client.GetAsync("/api/branding");
        Assert.Equal(HttpStatusCode.OK, office.StatusCode);

        using var ping = await FirstFieldClient.SendAsync(client, "ping");
        await FirstFieldClient.ReadAsync(ping, FirstFieldClient.Operation("ping"));
    }
}

public sealed class FieldRateLimitHost : ModuleOnlyHost
{
    protected override string ModuleCode => "work";
    protected override string DatabaseSuffix => "field_rate";
    protected override bool UseProductionRateLimits => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var limit = BrandingRateLimit.Default.ToString(System.Globalization.CultureInfo.InvariantCulture);
        builder.UseSetting("RateLimits:Branding", limit);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["RateLimits:Branding"] = limit }));
    }
}
