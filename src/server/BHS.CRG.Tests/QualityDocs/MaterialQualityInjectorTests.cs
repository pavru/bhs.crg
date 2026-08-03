using System.Text.Json;
using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Tests.QualityDocs;

/// <summary>
/// Подмешивание сертификата в строки материалов при генерации (issue #648).
///
/// Разбор живого случая: в АОСР материалы внесены прямо в документ (inline-ветка union «массив ИЛИ
/// ссылка на реестр», #320), то есть лежат в <c>Материалы.Материалы</c> — внутри составной обёртки.
/// Прежний обход брал только ключи верхнего уровня, значение которых само массив, и связка,
/// заведённая на такой материал, в PDF не попадала вовсе.
/// </summary>
public class MaterialQualityInjectorTests
{
    private static readonly string[] IdentityFields = ["Наименование", "Артикул"];
    private const string Target = "ДокументПодтверждающийКачество";
    private static readonly Guid DocId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Dictionary<string, Guid> ByKey = new() { ["проверка | 2342"] = DocId };
    private static readonly Dictionary<Guid, JsonElement> Reqs = new()
    {
        [DocId] = JsonDocument.Parse("""{ "Номер": "ЕАЭС-1" }""").RootElement.Clone(),
    };

    private static bool Inject(string json, out JsonElement result)
        => MaterialQualityInjector.TryInject(
            JsonDocument.Parse(json).RootElement.Clone(), IdentityFields, Target, ByKey, Reqs, out result);

    /// <summary>Ссылка на подмешанный сертификат по пути; null — на этом пути ничего нет.</summary>
    private static string? CertNumberAt(JsonElement root, params string[] path)
    {
        var cur = root;
        foreach (var seg in path)
        {
            if (int.TryParse(seg, out var i))
            {
                if (cur.ValueKind != JsonValueKind.Array || cur.GetArrayLength() <= i) return null;
                cur = cur[i];
            }
            else if (!cur.TryGetProperty(seg, out cur)) return null;
        }
        return cur.TryGetProperty(Target, out var d) && d.TryGetProperty("Номер", out var n) ? n.GetString() : null;
    }

    [Fact]
    public void Injects_IntoTopLevelArray()
    {
        // Штатный сценарий «материалы через реестр» — он работал и раньше; проверяем, что остался.
        Assert.True(Inject("""[ { "Наименование": "проверка", "Артикул": "2342" } ]""", out var r));
        Assert.Equal("ЕАЭС-1", CertNumberAt(r, "0"));
    }

    [Fact]
    public void Injects_ThroughUnionWrapper()
    {
        // Ровно случай #648: массив материалов лежит внутри составной обёртки union.
        Assert.True(Inject("""{ "Материалы": [ { "Наименование": "проверка", "Артикул": "2342" } ] }""", out var r));
        Assert.Equal("ЕАЭС-1", CertNumberAt(r, "Материалы", "0"));
    }

    [Fact]
    public void Injects_IntoSingleCompositeMaterial()
    {
        // Материал в единственном числе — тот же материал, просто не массивом.
        Assert.True(Inject("""{ "Материал": { "Наименование": "проверка", "Артикул": "2342" } }""", out var r));
        Assert.Equal("ЕАЭС-1", CertNumberAt(r, "Материал"));
    }

    [Fact]
    public void Skips_WhenTargetAlreadyFilled()
    {
        // Заданное вручную не перетираем: человек мог выбрать другой сертификат осознанно.
        Assert.False(Inject("""
            [ { "Наименование": "проверка", "Артикул": "2342",
                "ДокументПодтверждающийКачество": { "Номер": "своё" } } ]
            """, out var r));
        Assert.Equal("своё", CertNumberAt(r, "0"));
    }

    [Fact]
    public void Skips_WhenKeyDiffers()
    {
        // Ключ составной и обязан совпасть целиком (#582): один артикул мимо — материал не тот.
        Assert.False(Inject("""[ { "Наименование": "проверка", "Артикул": "9999" } ]""", out var r));
        Assert.Null(CertNumberAt(r, "0"));
    }

    [Fact]
    public void DoesNotDescend_IntoInjectedRequisites()
    {
        // Подмешанные реквизиты сертификата — уже результат: если бы обход шёл по ним, сертификат
        // с теми же полями идентичности получил бы сертификат сам себе.
        Assert.True(Inject("""[ { "Наименование": "проверка", "Артикул": "2342" } ]""", out var r));
        var cert = r[0].GetProperty(Target);
        Assert.False(cert.TryGetProperty(Target, out _));
    }

    [Fact]
    public void Unchanged_WhenNothingMatched()
    {
        // Ничего не совпало — вызывающий не должен переписывать контекст.
        Assert.False(Inject("""{ "Работы": [ { "Наименование": "монтаж" } ] }""", out _));
    }
}
