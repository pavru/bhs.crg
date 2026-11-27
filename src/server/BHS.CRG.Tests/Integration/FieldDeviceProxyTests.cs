using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Integration;

public sealed class FieldDeviceProxyHost : AssignmentHost
{
    protected override string DatabaseSuffix => "field_proxy";
    protected override bool UseProductionRateLimits => true;

    public FieldDeviceProxyHost() => UseKestrel(options => options.ListenAnyIP(0));

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.BaseAddress = new UriBuilder(client.BaseAddress!) { Host = "127.0.0.1" }.Uri;
    }
}

public sealed class FieldDeviceProxyFactAttribute : FactAttribute
{
    public FieldDeviceProxyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BHS_TEST_FIELD_PROXY") != "1")
            Skip = "Запускается с BHS_TEST_FIELD_PROXY=1, Docker и Python.";
    }
}

public sealed class FieldDeviceProxyTests(FieldDeviceProxyHost host) : IClassFixture<FieldDeviceProxyHost>
{
    [FieldDeviceProxyFact]
    public async Task Session_can_join_and_refresh_through_the_external_proxy()
    {
        var world = new AssignmentWorld(host);
        await world.ResetAsync();
        var employee = await world.EmployeeAsync(await world.DirectoryAsync(), "Монтажник без офисной записи");
        var (_, manager) = await world.UserAsync("Администратор устройств", WorkModule.DevicesManage, WorkModule.SitesAll);
        using var office = manager;
        using var invite = await office.PostAsJsonAsync($"/api/work/devices/employees/{employee}/invitations",
            new { serverUrl = "https://field.example.test", transport = "https-system" });
        invite.EnsureSuccessStatusCode();
        var invitationCode = (await AssignmentWorld.JsonAsync(invite)).GetProperty("invitationCode").GetString();

        var start = new ProcessStartInfo("python")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(RepoPaths.File("deploy", "field-proxy.tests.py"));
        start.ArgumentList.Add(office.BaseAddress!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--invitation-stdin");
        using var probe = Process.Start(start)!;
        var stdout = probe.StandardOutput.ReadToEndAsync();
        var stderr = probe.StandardError.ReadToEndAsync();
        await probe.StandardInput.WriteAsync(JsonSerializer.Serialize(new { invitationCode, employeeId = employee }));
        probe.StandardInput.Close();
        await probe.WaitForExitAsync();
        Assert.True(probe.ExitCode == 0, (await stdout) + (await stderr));
    }
}
