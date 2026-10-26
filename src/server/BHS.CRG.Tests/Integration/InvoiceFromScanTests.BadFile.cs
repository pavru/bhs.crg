using BHS.CRG.Domain.Recognition;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Нечитаемый файл — отказ о файле ДО движков и до проверки их готовности (ТЗ COST-8, issue #1344).
///
/// <para><b>Сторож задачи:</b> живой прогон счетов заводит счета из файлов, внутри которых строка текста,
/// и на стенде с настроенным движком каждый уходил к нему. Ломается он возвратом проверки готовности
/// перед проверкой файла: тогда без движка исход — «не настроено», а с движком готовность спрошена (а
/// она бывает пробой зрения — запросом к движку).</para>
/// </summary>
public sealed partial class InvoiceFromScanTests
{
    private const string Unreadable = "PDF не открывается: файл повреждён, защищён паролем или это не PDF.";

    [Fact]
    public async Task Нечитаемый_файл_без_движка_назван_файлом_а_не_ненастроенным_распознаванием()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.Unreadable[scan] = Unreadable;
        host.Recognition.NotReady = new RecognitionRefusedException(
            RecognitionRefusal.NotConfigured, "Распознавание не настроено: движок не выбран.");
        var asked = host.Recognition.ReadyChecks;
        try
        {
            var created = await FromScanAsync(client, scan, "Битый.pdf");

            var recognition = created.GetProperty("recognition");
            Assert.Equal("failed", recognition.GetProperty("state").GetString());
            Assert.Equal("BadFile", recognition.GetProperty("reason").GetString());
            Assert.Equal(Unreadable, recognition.GetProperty("error").GetString());
            Assert.Equal(asked, host.Recognition.ReadyChecks);
        }
        finally
        {
            host.Recognition.NotReady = null;
        }
    }

    /// <summary>
    /// С настроенным движком — тот же исход, и ни готовность, ни движок не спрошены: ни при заведении из
    /// файла, ни кнопкой «Распознать файл».
    /// </summary>
    [Fact]
    public async Task Нечитаемый_файл_с_движком_к_движку_не_уходит_ни_при_заведении_ни_кнопкой()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.Unreadable[scan] = Unreadable;
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: "СЧ-1344"))));
        var asked = host.Recognition.ReadyChecks;

        var created = await FromScanAsync(client, scan, "Битый.pdf");
        var id = created.GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("BadFile", created.GetProperty("recognition").GetProperty("reason").GetString());

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{id}/recognition", null));
        var again = await OutcomeAsync(client, id);
        Assert.Equal("failed", again.GetProperty("state").GetString());
        Assert.Equal("BadFile", again.GetProperty("reason").GetString());

        Assert.Equal(asked, host.Recognition.ReadyChecks);
        Assert.False(host.Recognition.Kinds.ContainsKey(scan), "Нечитаемый файл ушёл движку.");
    }
}
