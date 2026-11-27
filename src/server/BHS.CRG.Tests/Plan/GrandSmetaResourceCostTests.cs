using System.Text.Json;
using System.Xml.Linq;
using BHS.CRG.Modules.Plan.GrandSmeta;

namespace BHS.CRG.Tests.Plan;

/// <summary>PLAN-32: источник — исходный файл; независимый контроль БИМ — печатная форма,
/// РИМ — сохранённая программой расшифровка и парный файл без итогов.</summary>
public class GrandSmetaResourceCostTests
{
    [Theory]
    [InlineData("lsr-16.2.2-sks")]
    [InlineData("lsr-16.3.0-sks-no-itog")]
    public void Собственный_материал_не_получает_базисный_итог_или_итог_акта(string sample)
    {
        var model = Read(sample);
        var washer = Position(model, "85");
        var concrete = Position(model, "107");
        Assert.Equal(new ResourceCost(5.01m, 388.38m, ResourceTaxBasis.ExcludingVat, ResourceTaxBasis.ExcludingVat), washer.OwnMaterialCost);
        Assert.Equal(76m, washer.Quantity);
        Assert.Equal(1076.11m, concrete.OwnMaterialCost!.Amount);
        Assert.Null(concrete.OwnMaterialCost.UnitPrice); // PriceCurr есть, но активна база × индекс.
        var etalon = GrandSmetaSamples.Etalon("lsr-16.2.2-sks").Arr("positions").ToList();
        Assert.Equal(etalon.Single(p => p.Str("number") == "85").Dec("totalCurrent"), washer.OwnMaterialCost!.Amount);
        Assert.Equal(etalon.Single(p => p.Str("number") == "107").Dec("totalCurrent"), concrete.OwnMaterialCost.Amount);
    }

    [Theory]
    [InlineData("lsr-16.2.3-rim1")]
    [InlineData("lsr-16.3.0-rim1-no-itog")]
    public void Два_материала_работы_РИМ_получают_свои_суммы_а_не_общую(string sample)
    {
        var work = Position(Read(sample), "1");
        Assert.Equal(10422.73m, work.Total);
        Assert.Null(work.OwnMaterialCost);
        Assert.Collection(work.Materials,
            water => Assert.Equal(new ResourceCost(38.42m, 12.64m, ResourceTaxBasis.ExcludingVat, ResourceTaxBasis.ExcludingVat), water.Cost),
            drill => Assert.Equal(new ResourceCost(1514.46m, 214.14m, ResourceTaxBasis.ExcludingVat, ResourceTaxBasis.ExcludingVat), drill.Cost));
    }

    [Theory]
    [InlineData("lsr-16.2.3-rim1", "lsr-16.3.0-rim1-no-itog")]
    [InlineData("lsr-16.2.3-rim2", "lsr-16.3.0-rim2-no-itog")]
    public void Материальные_слагаемые_всех_работ_пар_РИМ_равны_расшифровке_программы(string recorded, string noTotals)
    {
        var program = Read(recorded);
        var computed = Read(noTotals).Positions.ToDictionary(p => p.Number);
        var checkedAmounts = 0;
        foreach (var p in program.Positions.Where(p => !p.IsInactive))
        {
            var actual = computed[p.Number];
            Assert.Equal(p.Materials.Count, actual.Materials.Count);
            for (var i = 0; i < p.Materials.Count; i++)
            {
                if (p.Materials[i].Cost?.Amount is not { } amount) continue;
                Assert.Equal((p.Number, i, (decimal?)amount), (p.Number, i, actual.Materials[i].Cost?.Amount));
                checkedAmounts++;
            }
        }
        Assert.True(checkedAmounts > 2, "проверка должна включать материальные слагаемые нескольких работ");
    }

    [Theory]
    [InlineData("lsr-16.2.2-sks")]
    [InlineData("lsr-16.3.0-sks-no-itog")]
    public void Общая_статья_БИМ_не_разносится_по_ресурсам(string sample)
    {
        var work = Position(Read(sample), "1");
        Assert.True(work.Materials.Count > 1);
        Assert.All(work.Materials, m => Assert.Null(m.Cost!.Amount));
        Assert.All(work.Materials, m => Assert.Null(m.Cost!.UnitPrice));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Записанное_нулевое_значение_отличается_от_отсутствующего(bool explicitZero)
    {
        var work = EditRim(p => MaterialLines(p)[0].SetAttributeValue("TotalCurr", explicitZero ? "0" : null));
        Assert.Equal(explicitZero ? 0m : (decimal?)null, work.Materials[0].Cost!.Amount);
        Assert.Equal(38.42m, work.Materials[0].Cost!.UnitPrice);
        Assert.Equal(214.14m, work.Materials[1].Cost!.Amount);
    }

    [Fact]
    public void Цена_и_сумма_не_подтверждают_друг_друга()
    {
        var work = EditRim(p =>
        {
            MaterialLines(p)[0].SetAttributeValue("ForOneCurr", null);
            Mats(p)[0].Element("PriceCurr")!.Remove();
        });
        Assert.Null(work.Materials[0].Cost!.UnitPrice);
        Assert.Equal(12.64m, work.Materials[0].Cost!.Amount);
        Assert.Equal(ResourceTaxBasis.Unknown, work.Materials[0].Cost!.UnitPriceBasis);
        Assert.Equal(ResourceTaxBasis.ExcludingVat, work.Materials[0].Cost!.AmountBasis);
    }

    [Fact]
    public void Нечитаемая_цена_не_удаляет_принятую_позицию_и_записанную_сумму()
    {
        var work = EditRim(p =>
        {
            MaterialLines(p)[0].SetAttributeValue("ForOneCurr", "Ф1*2");
            Mats(p)[0].Element("PriceCurr")!.SetAttributeValue("Value", "Ф1*2");
        });
        Assert.Null(work.Materials[0].Cost!.UnitPrice);
        Assert.Equal(12.64m, work.Materials[0].Cost!.Amount);
    }

    [Fact]
    public void Одноимённые_материалы_с_разными_нормами_сопоставляются_однозначно()
    {
        var work = EditRim(p =>
        {
            Mats(p)[1].SetAttributeValue("Caption", "Вода");
            MaterialLines(p)[1].SetAttributeValue("Caption", "Вода");
        });
        Assert.Equal(12.64m, work.Materials[0].Cost!.Amount);
        Assert.Equal(214.14m, work.Materials[1].Cost!.Amount);
    }

    [Fact]
    public void Неразличимые_дубли_не_получают_первый_подходящий_итог()
    {
        var work = EditRim(p =>
        {
            var mats = Mats(p);
            mats[1].SetAttributeValue("Caption", "Вода");
            mats[1].SetAttributeValue("Quantity", mats[0].Attribute("Quantity")!.Value);
            var lines = MaterialLines(p);
            lines[1].SetAttributeValue("Caption", "Вода");
            foreach (var line in lines)
            {
                line.SetAttributeValue("QuantityForOne", null);
                line.SetAttributeValue("QuantityTotal", null);
            }
        });
        Assert.All(work.Materials, m => Assert.Null(m.Cost!.Amount));
        Assert.Equal(38.42m, work.Materials[0].Cost!.UnitPrice);
    }

    [Fact]
    public void Материальный_итог_не_путается_с_одноимённым_трудом_и_машинами()
    {
        var work = EditRim(p =>
        {
            var name = Mats(p)[0].Attribute("Caption")!.Value;
            foreach (var res in p.Element("Itog")!.Descendants("Itog").Where(i => (string?)i.Attribute("DataType") == "Res").Take(4))
                res.SetAttributeValue("Caption", name);
        });
        Assert.Equal(12.64m, work.Materials[0].Cost!.Amount);
    }

    [Theory]
    [InlineData("Deleted", null)]
    [InlineData(null, "NotCount")]
    public void Удалённый_ресурс_пропущен_а_исключённый_из_итога_имеет_явный_ноль(string? attribs, string? options)
    {
        var work = EditRim(p =>
        {
            Mats(p)[0].SetAttributeValue("Attribs", attribs);
            Mats(p)[0].SetAttributeValue("Options", options);
        });
        if (attribs is not null)
        {
            var drill = Assert.Single(work.Materials);
            Assert.Equal(214.14m, drill.Cost!.Amount);
        }
        else
        {
            Assert.Equal(0m, work.Materials[0].Cost!.Amount);
            Assert.Equal(0.329m, work.Materials[0].Quantity);
        }
    }

    [Theory]
    [InlineData("lsr-16.2.3-rim1")]
    [InlineData("lsr-16.3.0-rim1-no-itog")]
    public void Неизвестная_налоговая_основа_не_стирает_доступные_числа(string sample)
    {
        var model = Read(sample, x => x.Root!.Element("AddZatrats")!.Remove());
        var water = Position(model, "1").Materials[0].Cost!;
        Assert.Equal(new ResourceCost(38.42m, 12.64m), water);
    }

    [Fact]
    public void Цена_КА_не_подменяется_отпускной_с_НДС()
    {
        var model = Read("lsr-16.2.2-sks", x =>
        {
            var p = XmlPosition(x, "85");
            p.Element("MarketAnalysisDocLink")!.SetAttributeValue("OptPrice", "900");
            p.Element("MarketAnalysisDocLink")!.SetAttributeValue("OptPriceWithVAT", "999");
            x.Root!.Element("AddZatrats")!.Remove();
        });
        var cost = Position(model, "85").OwnMaterialCost!;
        Assert.Equal(5.01m, cost.UnitPrice);
        Assert.Equal(ResourceTaxBasis.ExcludingVat, cost.UnitPriceBasis);
        Assert.Equal(388.38m, cost.Amount);
        Assert.Equal(ResourceTaxBasis.Unknown, cost.AmountBasis);
    }

    [Theory]
    [InlineData("OZ", "100")]
    [InlineData("EM", "100")]
    [InlineData("EM", "Ф1*2")]
    public void Собственный_материал_с_конкурирующей_статьёй_не_получает_итог_всей_позиции(string article, string value)
    {
        var model = Read("lsr-16.2.2-sks", x => XmlPosition(x, "85").Element("PriceCurr")!.SetAttributeValue(article, value));
        var cost = Position(model, "85").OwnMaterialCost!;
        Assert.Null(cost.Amount);
        Assert.Equal(5.01m, cost.UnitPrice);
    }

    [Fact]
    public void Старый_JSON_не_приобретает_стоимость_и_налоговую_основу()
    {
        var material = JsonSerializer.Deserialize<PositionMaterial>("""{"Code":"1","Caption":"Вода","Units":"м3","Quantity":2}""")!;
        Assert.Null(material.Cost);
        var price = JsonSerializer.Deserialize<ResourceCost>("""{"UnitPrice":0,"Amount":100}""")!;
        Assert.Equal(new ResourceCost(0m, 100m), price);
    }

    [Fact]
    public void Неизвестная_настройка_не_портит_сохранённую_расшифровку()
    {
        var model = Read("lsr-16.2.3-rim1", x =>
        {
            var parameters = x.Root!.Element("Parameters")!;
            parameters.SetAttributeValue("Options", parameters.Attribute("Options")!.Value + " RoundPos");
        });
        Assert.Equal(new ResourceCost(38.42m, 12.64m), Position(model, "1").Materials[0].Cost);
    }

    [Fact]
    public void Неизвестный_индекс_не_подставляет_справочную_цену_в_сумму_БИМ()
    {
        var model = LocalEstimateReader.Read(GrandSmetaSamples.OpenEditedXml("lsr-16.3.0-sks-no-itog",
            x => XmlPosition(x, "107").SetAttributeValue("IndexCode", "not-an-index")));
        Assert.Contains(model.Unparsed, u => u.Place.Contains("позиция 107 ") && u.Reason.Contains("индекс"));
        Assert.DoesNotContain(model.Positions, p => p.Number == "107");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void РИМ_без_атрибута_цены_не_создаёт_нулевую_стоимость_ресурса(bool explicitZero)
    {
        var model = Read("lsr-16.3.0-rim1-no-itog", x =>
            Mats(XmlPosition(x, "1"))[0].Element("PriceCurr")!.SetAttributeValue("Value", explicitZero ? "0" : null));
        var water = Position(model, "1").Materials[0].Cost!;
        Assert.Equal(explicitZero ? 0m : (decimal?)null, water.UnitPrice);
        Assert.Equal(explicitZero ? 0m : (decimal?)null, water.Amount);
    }

    [Fact]
    public void Умолчание_нуля_общего_досчёта_не_становится_суммой_собственного_материала()
    {
        var model = Read("lsr-16.3.0-sks-no-itog", x =>
        {
            var p = XmlPosition(x, "107");
            p.SetAttributeValue("PriceLevel", "Curr");
            p.Element("PriceCurr")!.SetAttributeValue("MT", null);
        });
        var material = Position(model, "107");
        Assert.Equal(0m, material.Total); // прежний договор общего досчёта сохраняется
        Assert.Null(material.OwnMaterialCost!.Amount);
        Assert.Null(material.OwnMaterialCost.UnitPrice);
    }

    [Fact]
    public void Неактивная_позиция_сохраняет_пометку_не_приписывает_общий_итог_материалу()
    {
        var work = EditRim(p => p.SetAttributeValue("Options", "Inactive"));
        Assert.True(work.IsInactive);
        Assert.Null(work.OwnMaterialCost);
        // Исключение из импорта проверяется EstimateCarriersImportTests; физическая модель не меняется.
        Assert.Equal(12.64m, work.Materials[0].Cost!.Amount);
    }

    [Fact]
    public void ВОР_не_несёт_денежной_модели_ресурса()
    {
        var model = VolumeStatementReader.Read(GrandSmetaSamples.Open("vor-16.2.3-variables-hyperlinks"));
        var json = JsonSerializer.Serialize(model);
        Assert.DoesNotContain("UnitPrice", json);
        Assert.DoesNotContain("AmountBasis", json);
    }

    private static ParsedLocalEstimate Read(string sample, Action<XDocument>? edit = null)
    {
        var model = LocalEstimateReader.Read(edit is null ? GrandSmetaSamples.Open(sample) : GrandSmetaSamples.OpenEditedXml(sample, edit));
        Assert.Empty(model.Unparsed);
        return model;
    }

    private static LocalEstimatePosition Position(ParsedLocalEstimate model, string number) => model.Positions.Single(p => p.Number == number);
    private static XElement XmlPosition(XDocument x, string number) => x.Root!.Elements("Chapters").Elements("Chapter").Elements("Position")
        .Single(p => (string?)p.Attribute("Number") == number);
    private static List<XElement> Mats(XElement p) => p.Elements("Resources").Elements("Mat").ToList();
    private static List<XElement> MaterialLines(XElement p) => p.Element("Itog")!.Descendants("Itog")
        .Single(i => (string?)i.Attribute("DataType") == "MAT").ElementsAfterSelf("Itog")
        .TakeWhile(i => (string?)i.Attribute("DataType") == "Res").ToList();
    private static LocalEstimatePosition EditRim(Action<XElement> edit) => Position(Read("lsr-16.2.3-rim1", x => edit(XmlPosition(x, "1"))), "1");
}
