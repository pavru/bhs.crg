using System.Text.Json;
using BHS.CRG.Api.Endpoints.DataSets;
using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Разбор тела правки обработки (issue #1139): какая часть прислана, какая прислана пустой и какой в
/// запросе нет вовсе. Различие это видно только по телу — привязка к записи отдала бы <c>null</c> и
/// за отсутствующее поле, и за сброшенное.
/// </summary>
public class SourceProcessingBodyTests
{
    private static SetSourceProcessingInput Parse(string body)
    {
        Assert.True(SourceProcessingBody.TryParse(JsonDocument.Parse(body).RootElement, out var input, out var refusal), refusal);
        return input;
    }

    [Fact]
    public void Прислана_одна_часть_остальные_не_тронуты()
    {
        var input = Parse("""{"ifMatch":"в1","sortSpec":[{"column":"А","direction":"asc"}]}""");

        Assert.True(input.SortSpec.Sent);
        Assert.Equal(JsonValueKind.Array, Assert.IsType<JsonElement>(input.SortSpec.Value).ValueKind);
        Assert.False(input.RowFilter.Sent);
        Assert.False(input.ComputedColumns.Sent);
        Assert.False(input.IsEmpty);
    }

    [Fact]
    public void Часть_присланная_значением_null_сбрасывается_а_не_пропускается()
    {
        var input = Parse("""{"ifMatch":"в1","rowFilter":null}""");

        Assert.Equal(ProcessingPart.Of(null), input.RowFilter);
        Assert.False(input.SortSpec.Sent);
    }

    [Fact]
    public void Имена_частей_читаются_без_оглядки_на_регистр()
    {
        var input = Parse("""{"RowFilter":null,"COMPUTEDCOLUMNS":[],"sortspec":null,"IFMATCH":"в1"}""");

        Assert.True(input.RowFilter.Sent);
        Assert.True(input.ComputedColumns.Sent);
        Assert.True(input.SortSpec.Sent);
    }

    /// <summary>Значение части переживает документ, из которого разобрано: запрос к этому времени закрыт.</summary>
    [Fact]
    public void Значение_части_не_зависит_от_жизни_документа()
    {
        SetSourceProcessingInput input;
        using (var document = JsonDocument.Parse("""{"ifMatch":"в1","rowFilter":{"type":"group","logic":"and","children":[]}}"""))
            Assert.True(SourceProcessingBody.TryParse(document.RootElement, out input, out _));

        Assert.Equal("group", Assert.IsType<JsonElement>(input.RowFilter.Value).GetProperty("type").GetString());
    }

    /// <summary>
    /// Тело без единой части разбирается в пустую правку — отказывает ей служба, одинаково для любого
    /// входа (см. <c>SourceProcessingPartsTests</c>).
    /// </summary>
    [Fact]
    public void Тело_без_частей_даёт_пустую_правку()
    {
        Assert.True(Parse("""{"ifMatch":"в1"}""").IsEmpty);
        Assert.True(new SetSourceProcessingInput().IsEmpty);
    }

    /// <summary>
    /// Тело, которое нельзя понять однозначно, — отказ с причиной. Повтор поля — тоже: «rowFilter» и
    /// «RowFilter» — одна часть, и молча взятое последним <c>null</c> сбросило бы присланный отбор.
    /// </summary>
    [Theory]
    [InlineData("""{"rowFiltr":null}""", "«rowFiltr»")]
    [InlineData("""{"rowFilter":null,"sort":[]}""", "«sort»")]
    [InlineData("""{"rowFilter":{"type":"group","logic":"and","children":[]},"RowFilter":null}""", "«RowFilter» прислано дважды")]
    [InlineData("""{"ifMatch":"в1","sortSpec":null,"IfMatch":"в2"}""", "«IfMatch» прислано дважды")]
    [InlineData("""{"sortSpec":null,"sortSpec":null}""", "«sortSpec» прислано дважды")]
    [InlineData("[]", "не объект")]
    [InlineData("null", "не объект")]
    [InlineData("\"rowFilter\"", "не объект")]
    public void Тело_которое_не_понять_однозначно_отказ_с_причиной(string body, string named)
    {
        Assert.False(SourceProcessingBody.TryParse(JsonDocument.Parse(body).RootElement, out var input, out var refusal));

        Assert.Contains(named, refusal);
        // Отказ — отказ целиком: частей из тела, разобранного наполовину, наружу не выходит.
        Assert.True(input.IsEmpty);
    }

    /// <summary>
    /// Версию обработки тело называет всегда (issue #1141): правку страница собирает по своей копии
    /// источника, и без версии не проверить, не устарела ли копия. Не названа — отказ, а не «сверять
    /// не с чем»: так вкладка с прежней сборкой клиента узнаёт, что её пора обновить.
    /// </summary>
    [Fact]
    public void Версия_обработки_читается_из_тела()
    {
        Assert.Equal("a1b2", Parse("""{"sortSpec":null,"ifMatch":"a1b2"}""").IfMatch);
        Assert.Null(new SetSourceProcessingInput().IfMatch);
    }

    [Theory]
    [InlineData("""{"sortSpec":null}""")]
    [InlineData("""{"sortSpec":null,"ifMatch":null}""")]
    [InlineData("""{"sortSpec":null,"ifMatch":""}""")]
    [InlineData("""{"sortSpec":null,"ifMatch":"  "}""")]
    [InlineData("""{"sortSpec":null,"ifMatch":7}""")]
    [InlineData("""{"rowFilter":null,"computedColumns":null,"sortSpec":null}""")]
    public void Тело_без_версии_обработки_отказ_с_причиной(string body)
    {
        Assert.False(SourceProcessingBody.TryParse(JsonDocument.Parse(body).RootElement, out var input, out var refusal));

        Assert.Contains("«ifMatch»", refusal);
        Assert.Contains("обновите страницу", refusal);
        Assert.True(input.IsEmpty);
        Assert.Null(input.IfMatch);
    }
}
