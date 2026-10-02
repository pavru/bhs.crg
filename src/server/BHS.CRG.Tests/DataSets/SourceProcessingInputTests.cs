using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Разбор тела правки обработки (issue #1139): какая часть прислана, какая прислана пустой и какой в
/// запросе нет вовсе. Различие это видно только по телу — привязка к записи отдала бы <c>null</c> и
/// за отсутствующее поле, и за сброшенное.
/// </summary>
public class SourceProcessingInputTests
{
    private static SetSourceProcessingInput Parse(string body) =>
        SetSourceProcessingInput.FromBody(JsonDocument.Parse(body).RootElement);

    [Fact]
    public void Прислана_одна_часть_остальные_не_тронуты()
    {
        var input = Parse("""{"sortSpec":[{"column":"А","direction":"asc"}]}""");

        Assert.True(input.SortSpec.Sent);
        Assert.Equal(JsonValueKind.Array, Assert.IsType<JsonElement>(input.SortSpec.Value).ValueKind);
        Assert.False(input.RowFilter.Sent);
        Assert.False(input.ComputedColumns.Sent);
    }

    [Fact]
    public void Часть_присланная_значением_null_сбрасывается_а_не_пропускается()
    {
        var input = Parse("""{"rowFilter":null}""");

        Assert.Equal(ProcessingPart.Cleared, input.RowFilter);
        Assert.False(input.SortSpec.Sent);
    }

    [Fact]
    public void Имена_частей_читаются_без_оглядки_на_регистр()
    {
        var input = Parse("""{"RowFilter":null,"COMPUTEDCOLUMNS":[],"sortspec":null}""");

        Assert.True(input.RowFilter.Sent);
        Assert.True(input.ComputedColumns.Sent);
        Assert.True(input.SortSpec.Sent);
    }

    /// <summary>Значение части переживает документ, из которого разобрано: запрос к этому времени закрыт.</summary>
    [Fact]
    public void Значение_части_не_зависит_от_жизни_документа()
    {
        SetSourceProcessingInput input;
        using (var document = JsonDocument.Parse("""{"rowFilter":{"type":"group","logic":"and","children":[]}}"""))
            input = SetSourceProcessingInput.FromBody(document.RootElement);

        Assert.Equal("group", Assert.IsType<JsonElement>(input.RowFilter.Value).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("{}", "менять нечего")]
    [InlineData("""{"rowFiltr":null}""", "«rowFiltr»")]
    [InlineData("""{"rowFilter":null,"sort":[]}""", "«sort»")]
    [InlineData("[]", "не объект")]
    [InlineData("null", "не объект")]
    [InlineData("\"rowFilter\"", "не объект")]
    public void Запрос_из_которого_взять_нечего_отказ_с_причиной(string body, string named)
    {
        var refusal = Assert.Throws<InvalidRequestException>(() => Parse(body));
        Assert.Contains(named, refusal.Message);
    }
}
