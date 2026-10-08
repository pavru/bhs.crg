using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Сопоставление сторон распознанного счёта с организациями справочника по ИНН (issue #1077).
///
/// <para><b>Сторож:</b> ответ «в справочнике такой организации нет» даётся, только когда ИНН прочитан
/// верно и прочитаны все записи. По нему человек заведёт организацию, и каждый случай, где утверждать
/// «нет» нельзя, обязан иметь своё состояние — иначе в справочнике появится вторая такая же.</para>
/// </summary>
public class InvoicePartiesTests
{
    // ИНН с верной контрольной суммой: первый — организации, второй начинается с нуля, третий —
    // предпринимателя (12 цифр).
    private const string Valid = "7701234560";
    private const string Other = "7802345676";
    private const string LeadingZero = "0105001234";
    private const string Person = "770123456703";

    [Theory]
    [InlineData("7701234560", "7701234560")]
    [InlineData(" 7701 234 560 ", "7701234560")]
    [InlineData("7701234560/770101001", "7701234560")]
    [InlineData("ИНН 770123456703", "770123456703")]
    [InlineData("0105001234", "0105001234")]
    public void ИНН_из_скана_читается_цифрами(string text, string expected)
    {
        Assert.Equal(expected, TaxId.FromScan(text, out var problem));
        Assert.Null(problem);
    }

    /// <summary>
    /// Сторож: неверно прочитанный ИНН — причина, а не значение. Первый пример — одна перепутанная
    /// цифра: без контрольной суммы поиск по нему честно ответил бы «такой организации нет».
    /// </summary>
    [Theory]
    [InlineData("7701234561", "контрольная сумма")]
    [InlineData("770123456", "10 цифр")]
    [InlineData("77012345601", "10 цифр")]
    [InlineData("нет данных", "10 цифр")]
    public void ИНН_прочитанный_с_ошибкой_даёт_причину(string text, string why)
    {
        Assert.Null(TaxId.FromScan(text, out var problem));
        Assert.Contains(why, problem);
    }

    [Fact]
    public void Пустой_ИНН_в_скане_не_ошибка_чтения()
    {
        Assert.Null(TaxId.FromScan("  ", out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("7701234560", "7701234560")]
    [InlineData("105001234", "0105001234")]       // число в JSON потеряло ведущий ноль
    [InlineData("77012345670", "077012345670")]
    [InlineData("77 01 234560", "7701234560")]
    [InlineData("", null)]
    [InlineData("б/н", null)]
    public void ИНН_записи_справочника_приводится_к_цифрам(string value, string? expected) =>
        Assert.Equal(expected, TaxId.FromRecord(value));

    [Fact]
    public async Task Единственная_действующая_организация_совпадает()
    {
        var org = Record("ООО «Кабель-Торг»", Valid);
        var view = await SupplierAsync(Catalog(org, Record("Другая", Other)), "Кабель-Торг ООО", Valid);

        Assert.Equal("matched", view.State);
        Assert.Null(view.Why);
        Assert.Equal(org.Record.Id, Assert.Single(view.Candidates).Id);
        Assert.Equal(org.Record.Id, view.Match);
        Assert.Equal("Кабель-Торг ООО", view.Name);
        Assert.Equal(Valid, view.TaxId);
    }

    /// <summary>Архивный двойник единственности не мешает, но в ответе назван.</summary>
    [Fact]
    public async Task Действующая_и_архивная_с_одним_ИНН_это_одно_совпадение()
    {
        var view = await SupplierAsync(
            Catalog(Record("Действующая", Valid), Record("Прежняя", Valid, archived: true)), "Поставщик", Valid);

        Assert.Equal("matched", view.State);
        Assert.Equal(2, view.Candidates.Count);
        Assert.Single(view.Candidates, c => c.Archived);
    }

    /// <summary>
    /// Организация и её роли — одна организация: найдена она сама, а не роль (решение владельца
    /// продукта от 08.10.2026). Роли остаются в ответе, с основой у каждой.
    /// </summary>
    [Fact]
    public async Task Организация_среди_своих_ролей_найдена_и_это_она_а_не_роль()
    {
        var org = Record("ООО «Кабель-Торг»", Valid);
        var role = Record("Подрядчик", Valid, inheritedFrom: org.Record.Id);
        var another = Record("Субподрядчик", Valid, inheritedFrom: org.Record.Id);

        var view = await SupplierAsync(Catalog(role, org, another), "Кабель-Торг", Valid);

        Assert.Equal("matched", view.State);
        Assert.Equal(org.Record.Id, view.Match);
        Assert.Equal(3, view.Candidates.Count);
        Assert.Equal(org.Record.Id, view.Candidates.Single(c => c.Id == role.Record.Id).InheritedFrom);
    }

    /// <summary>
    /// Сторож: правило «организация и её роли» не должно глотать настоящие дубли. Две записи со своим
    /// ИНН каждая — разные записи, даже если у одной из них есть роль.
    /// </summary>
    [Fact]
    public async Task Две_записи_со_своим_ИНН_остаются_выбором_человека()
    {
        var org = Record("ООО «Кабель-Торг»", Valid);
        var twin = Record("Кабель-Торг (дубль)", Valid);
        var role = Record("Подрядчик", Valid, inheritedFrom: org.Record.Id);

        var view = await SupplierAsync(Catalog(org, twin, role), "Кабель-Торг", Valid);

        Assert.Equal("several", view.State);
        Assert.Null(view.Match);
        Assert.Contains("несколько", view.Why);
    }

    /// <summary>Основа в архиве, роли живы: архивную не ставим, а между ролями выбирает человек.</summary>
    [Fact]
    public async Task Роли_архивной_организации_остаются_выбором_человека()
    {
        var org = Record("ООО «Кабель-Торг»", Valid, archived: true);
        var view = await SupplierAsync(
            Catalog(org, Record("Подрядчик", Valid, inheritedFrom: org.Record.Id),
                Record("Субподрядчик", Valid, inheritedFrom: org.Record.Id)),
            "Кабель-Торг", Valid);

        Assert.Equal("several", view.State);
        Assert.Null(view.Match);
    }

    [Fact]
    public async Task Только_архивное_совпадение_названо_архивным_а_не_отсутствием()
    {
        var view = await SupplierAsync(Catalog(Record("Прежняя", Valid, archived: true)), "Поставщик", Valid);

        Assert.Equal("archived", view.State);
        Assert.Single(view.Candidates);
        Assert.Contains("в архиве", view.Why);
    }

    [Fact]
    public async Task Нет_совпадений_и_все_записи_прочитаны_значит_организации_нет()
    {
        var view = await SupplierAsync(Catalog(Record("Другая", Other), Record("Без ИНН", null)), "Новый поставщик", Valid);

        Assert.Equal("absent", view.State);
        Assert.Empty(view.Candidates);
        Assert.Equal(0, view.Unreadable);
    }

    /// <summary>
    /// Сторож: запись, чей ИНН узнать не удалось, могла быть той самой организацией. «Нет» при ней —
    /// ложь, по которой заведут дубль.
    /// </summary>
    [Fact]
    public async Task Нечитаемая_запись_не_даёт_сказать_что_организации_нет()
    {
        var view = await SupplierAsync(
            Catalog(Record("Другая", Other), Record("Роль без основы", null, unreadable: true)), "Поставщик", Valid);

        Assert.Equal("unknown", view.State);
        Assert.Equal(1, view.Unreadable);
        Assert.Contains("прочитать не удалось", view.Why);
    }

    /// <summary>Нечитаемые записи найденному совпадению не мешают.</summary>
    [Fact]
    public async Task Нечитаемая_запись_не_отменяет_найденного_совпадения()
    {
        var view = await SupplierAsync(
            Catalog(Record("Наша", Valid), Record("Роль без основы", null, unreadable: true)), "Поставщик", Valid);

        Assert.Equal("matched", view.State);
        Assert.Equal(1, view.Unreadable);
    }

    [Fact]
    public async Task ИНН_с_ошибкой_в_справочник_не_идёт()
    {
        var catalog = Catalog(Record("Наша", Valid));
        var view = await SupplierAsync(catalog, "Поставщик", "7701234561");

        Assert.Equal("badTaxId", view.State);
        Assert.Equal("7701234561", view.TaxId);
        Assert.Contains("контрольная сумма", view.Why);
        Assert.Equal(0, catalog.Asked);
    }

    [Fact]
    public async Task Название_без_ИНН_не_сопоставляется()
    {
        var catalog = Catalog(Record("ООО «Кабель-Торг»", Valid));
        var view = await SupplierAsync(catalog, "ООО «Кабель-Торг»", null);

        Assert.Equal("noTaxId", view.State);
        Assert.Empty(view.Candidates);
        Assert.Equal(0, catalog.Asked);
    }

    /// <summary>
    /// Сторож: поле ИНН ведёт человек. Переименовали — отказ с названием поля, а не «организаций нет».
    /// </summary>
    [Fact]
    public async Task Нет_поля_ИНН_в_схеме_или_самого_типа_это_не_отсутствие_организации()
    {
        var renamed = await SupplierAsync(new FakeCatalog(new ModuleCatalogFieldValues(false, [])), "Поставщик", Valid);
        Assert.Equal("unavailable", renamed.State);
        Assert.Contains("«ИНН»", renamed.Why);

        var noType = await SupplierAsync(new FakeCatalog(null), "Поставщик", Valid);
        Assert.Equal("unavailable", noType.State);
        Assert.Contains("не заведён", noType.Why);
    }

    [Fact]
    public async Task ИНН_записи_числом_без_ведущего_нуля_совпадает()
    {
        var view = await SupplierAsync(Catalog(Record("Майкоп", "105001234")), "Поставщик", LeadingZero);
        Assert.Equal("matched", view.State);
    }

    /// <summary>Стороны независимы; о стороне, про которую в скане нет ничего, ответа нет вовсе.</summary>
    [Fact]
    public async Task Плательщик_сопоставляется_сам_по_себе_а_непрочитанной_стороны_в_ответе_нет()
    {
        var us = Record("ИП Иванов", Person);
        var parties = await new InvoiceParties(Catalog(us)).MatchAsync(new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.Supplier] = null,
            [CostsRecognitionProfiles.SupplierTaxId] = " ",
            [CostsRecognitionProfiles.Payer] = "Иванов И. И.",
            [CostsRecognitionProfiles.PayerTaxId] = Person,
        }, CancellationToken.None);

        Assert.Null(parties.Supplier);
        Assert.Equal("matched", parties.Payer!.State);
        Assert.Equal(us.Record.Id, Assert.Single(parties.Payer.Candidates).Id);
    }

    private static async Task<InvoicePartyView> SupplierAsync(FakeCatalog catalog, string? name, string? taxId) =>
        (await new InvoiceParties(catalog).MatchAsync(new Dictionary<string, string?>
        {
            [CostsRecognitionProfiles.Supplier] = name,
            [CostsRecognitionProfiles.SupplierTaxId] = taxId,
        }, CancellationToken.None)).Supplier!;

    private static ModuleCatalogFieldValue Record(
        string name, string? taxId, bool archived = false, Guid? inheritedFrom = null, bool unreadable = false) =>
        new(new ModuleCatalogRef(Guid.NewGuid(), CostsRecordTypes.OrganizationCode, name, archived),
            taxId, inheritedFrom, unreadable);

    private static FakeCatalog Catalog(params ModuleCatalogFieldValue[] records) =>
        new(new ModuleCatalogFieldValues(true, records));

    private sealed class FakeCatalog(ModuleCatalogFieldValues? answer) : IModuleCatalog
    {
        public int Asked { get; private set; }

        public Task<ModuleCatalogFieldValues?> FieldValuesAsync(
            string entityType, string fieldKey, RecordsFor purpose, CancellationToken ct = default)
        {
            Assert.Equal(CostsRecordTypes.OrganizationCode, entityType);
            Assert.Equal("ИНН", fieldKey);
            // Сопоставление обязано видеть архив: иначе «в архиве» выглядело бы как «нет».
            Assert.Equal(RecordsFor.Display, purpose);
            Asked++;
            return Task.FromResult(answer);
        }

        public Task<IReadOnlyList<ModuleCatalogEntry>?> ListAsync(string entityType, RecordsFor purpose, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ModuleCatalogEntry?> GetAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ModuleCatalogChoice?> SearchAsync(string entityType, string? query, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ModuleCatalogRef>?> RefsAsync(
            string entityType, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
