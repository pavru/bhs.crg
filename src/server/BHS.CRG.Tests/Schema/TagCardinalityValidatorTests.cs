using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Кратность тэга внутри типа (ТЗ TYPE-21, столбец «Сколько»; issue #959).
///
/// Ограничение было объявлено с самого появления реестра (<c>Multiple: false</c>) и не проверялось
/// нигде. Цена тишины видна на любом читателе: код ищет поле по тэгу и берёт первое попавшееся —
/// из двух помеченных полей молча выбирает одно, а какое, зависит от порядка в схеме.
/// </summary>
public class TagCardinalityValidatorTests
{
    /// <summary>Одиночный тэг поля (Multiple: false) и множественный — для пары «нельзя / можно».</summary>
    private const string Single = FunctionalTag.DocNumber;
    private const string Many = FunctionalTag.Identity;

    private static readonly TagCatalog Catalog = TagCatalog.Build(TagRegistry.Core, []);

    private static DocumentType Type(string name, string schema, Guid? parentId = null) =>
        DocumentType.Create(name, name, DocumentTypeKind.Document, parentId,
            JsonDocument.Parse(schema.Replace('\'', '"')), TypeOwner.Core, TypeVisibility.Shared);

    private static string Field(string key, string tag) =>
        $"{{'key':'{key}','type':'string','tags':['{tag}']}}";

    /// <summary>Схема из записи с одинарными кавычками — как у <see cref="Type" />.</summary>
    private static JsonDocument Schema(string json) => JsonDocument.Parse(json.Replace('\'', '"'));

    [Fact]
    public void Второе_поле_с_одиночным_тэгом_отказывает()
    {
        var t = Type("Акт", $"{{'fields':[{Field("Номер", Single)},{Field("НомерКопии", Single)}]}}");

        var v = TagCardinalityValidator.Validate(Catalog, t, [t]);

        Assert.Single(v);
        Assert.Contains("Номер", v[0].Describe());
        Assert.Contains("НомерКопии", v[0].Describe());
    }

    [Fact]
    public void Одно_поле_с_одиночным_тэгом_проходит()
    {
        var t = Type("Акт", $"{{'fields':[{Field("Номер", Single)}]}}");
        Assert.Empty(TagCardinalityValidator.Validate(Catalog, t, [t]));
    }

    [Fact]
    public void Множественному_тэгу_несколько_полей_разрешено()
    {
        // `identity` объявлен Multiple: true намеренно — составной ключ склеивается из ВСЕХ
        // помеченных полей. Запрети мы ему второе поле, сломался бы резолв «строка→объект».
        var t = Type("Материал", $"{{'fields':[{Field("Артикул", Many)},{Field("Наименование", Many)}]}}");
        Assert.Empty(TagCardinalityValidator.Validate(Catalog, t, [t]));
    }

    [Fact]
    public void Унаследованное_поле_считается_вместе_с_собственным()
    {
        // Читатель видит поля вместе с унаследованными, и тэг, добавленный потомком рядом с
        // унаследованным, даёт ровно ту же двусмысленность, что два собственных.
        var parent = Type("Документ", $"{{'fields':[{Field("Номер", Single)}]}}");
        var child = Type("Акт", $"{{'fields':[{Field("НомерАкта", Single)}]}}", parent.Id);

        var v = TagCardinalityValidator.Validate(Catalog, child, [parent, child]);

        Assert.Single(v);
        Assert.Contains("НомерАкта", v[0].Describe());
    }

    [Fact]
    public void Потомок_вправе_исключить_поле_предка_и_пометить_своё()
    {
        // Законное действие, а не обход: потомок ОТКАЗЫВАЕТСЯ от помеченного поля предка. Считай
        // мы по объединению схем, запрет пришёл бы на то, что пользователь вправе сделать, — и
        // единственным выходом осталось бы «снимите тэг у предка», то есть сломайте соседей.
        var parent = Type("Документ", $"{{'fields':[{Field("Номер", Single)}]}}");
        var child = Type("Акт",
            $"{{'excludedFields':['Номер'],'fields':[{Field("НомерАкта", Single)}]}}", parent.Id);

        Assert.Empty(TagCardinalityValidator.Validate(Catalog, child, [parent, child]));
    }

    [Fact]
    public void Потомок_вправе_переопределить_помеченное_поле_предка()
    {
        // Поле с тем же ключом перекрывает унаследованное — носитель остаётся один.
        var parent = Type("Документ", $"{{'fields':[{Field("Номер", Single)}]}}");
        var child = Type("Акт", $"{{'fields':[{Field("Номер", Single)}]}}", parent.Id);

        Assert.Empty(TagCardinalityValidator.Validate(Catalog, child, [parent, child]));
    }

    [Fact]
    public void Правка_предка_ловится_по_потомку()
    {
        // Схема ПОТОМКА не менялась и сама по себе безупречна — второго носителя создаёт правка
        // ПРЕДКА. Смотри проверка только вверх от сохраняемого типа, эта правка прошла бы молча, а
        // потомок после неё не сохранялся бы уже никогда: любая правка его схемы упиралась бы в
        // отказ про поля, которых в ней нет (поймано ревью PR #1012).
        var parent = Type("Документ", "{'fields':[]}");
        var child = Type("Акт", $"{{'fields':[{Field("НомерАкта", Single)}]}}", parent.Id);
        var parentWithTag = parent.WithSchema(Schema($"{{'fields':[{Field("Номер", Single)}]}}"));

        var v = TagCardinalityValidator.Validate(Catalog, parentWithTag, [parent, child]);

        var one = Assert.Single(v);
        // Имя типа обязательно: поля «Номер» и «НомерАкта» лежат в РАЗНЫХ схемах, и без него
        // сообщение называло бы поле, которого в открытой форме нет вовсе.
        Assert.Contains("Акт", one.Describe());
        Assert.Contains("НомерАкта", one.Describe());
    }

    [Fact]
    public void Правка_предка_без_нарушения_у_потомка_проходит()
    {
        // Контроль к предыдущему: обход потомков не должен запрещать безобидную правку предка.
        var parent = Type("Документ", "{'fields':[]}");
        var child = Type("Акт", $"{{'fields':[{Field("НомерАкта", Single)}]}}", parent.Id);
        var parentWithOther = parent.WithSchema(Schema("{'fields':[{'key':'Тема','type':'string'}]}"));

        Assert.Empty(TagCardinalityValidator.Validate(Catalog, parentWithOther, [parent, child]));
    }

    [Fact]
    public void Тэг_вне_реестра_не_проверяется()
    {
        // Тэг выключенного модуля или вовсе чужой: его кратность объявлена в коде, которого на
        // этом экземпляре нет. Отказать по нему значило бы отказать в сохранении схемы из-за
        // правила, которого мы не знаем.
        var t = Type("Акт", "{'fields':[" +
            "{'key':'А','type':'string','tags':['work.shift']}," +
            "{'key':'Б','type':'string','tags':['work.shift']}]}");

        Assert.Empty(TagCardinalityValidator.Validate(Catalog, t, [t]));
    }
}
