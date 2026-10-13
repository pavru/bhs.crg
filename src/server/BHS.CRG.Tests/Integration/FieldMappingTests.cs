using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Адрес экрана «Сопоставление полей» и строка-сигнал в настройках модуля (issue #1101) — на живом
/// хосте с включённым модулем счетов.
/// </summary>
[Collection("Integration")]
public class FieldMappingTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>
    /// Экран и строка-сигнал отвечают разными адресами под разными правами — и обязаны говорить одно:
    /// сводка в «Настройках», расходящаяся с экраном, отправляла бы человека чинить то, чего нет.
    /// </summary>
    [Fact]
    public async Task Экран_и_сводка_в_настройках_говорят_одно()
    {
        var (client, _) = await SignInAsync(SystemRoles.Admin);

        var screen = (await client.GetFromJsonAsync<JsonElement>("/api/field-mapping"))
            .GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == "costs");
        var signal = (await client.GetFromJsonAsync<JsonElement>("/api/settings/modules"))
            .GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == "costs")
            .GetProperty("mapping");

        // Два справочника и два обязательных поля организаций — всё, что модуль читает.
        Assert.Equal(4, screen.GetProperty("total").GetInt32());
        Assert.Equal(screen.GetProperty("total").GetInt32(), signal.GetProperty("total").GetInt32());
        Assert.Equal(screen.GetProperty("unmet").GetInt32(), signal.GetProperty("unmet").GetInt32());
        Assert.Equal(
            screen.GetProperty("notWorking").EnumerateArray().Select(x => x.GetString()),
            signal.GetProperty("notWorking").EnumerateArray().Select(x => x.GetString()));

        // Хост заводит справочники с ролями — иначе счета на нём не работали бы; экран обязан это видеть.
        var organizations = screen.GetProperty("groups").EnumerateArray()
            .Single(g => g.GetProperty("role").GetString() == "role.organization");
        Assert.Equal("mapped", organizations.GetProperty("state").GetString());
        Assert.Equal(
            new[] { "org.inn", "org.name" },
            organizations.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("tag").GetString()));
    }

    /// <summary>Экран правит схему типов — и закрыт тем же правом, что запись схемы.</summary>
    [Fact]
    public async Task Без_права_на_типы_экран_закрыт()
    {
        var (user, _) = await SignInAsync(SystemRoles.IdEngineer);

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/field-mapping")).StatusCode);
    }
}
