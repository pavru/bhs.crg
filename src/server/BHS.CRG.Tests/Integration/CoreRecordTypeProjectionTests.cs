using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Скелет типа, объявленный ЯДРОМ, и наследование в проекции (issue #962, ТЗ CORE-7, TYPE-7.1).
///
/// <para>Проекция строилась под модули (issue #958). Справочник сотрудников — первый случай, когда
/// скелет объявляет само ядро, и первый, где тип обязан быть ПРОИЗВОДНЫМ: «Сотрудник» наследует
/// ФИО и должность от «Персоны», иначе у заказчика оказалось бы два справочника людей и два
/// разных «ФИО» (TYPE-7.1 требует этого прямо).</para>
///
/// <para>С issue #963 здесь же проверяются ЦЕЛИ ПОЛЕЙ: классификатор видов работ обязан ссылаться
/// на существующий справочник единиц измерения (ТЗ CORE-8, TYPE-7.1 «использовать как есть»), а
/// найти его в каждой установке можно только по коду — идентификатор везде свой.</para>
///
/// <para>⚠️ Половина тестов здесь — про ОТКАЗЫ, и это не педантизм. Расхождение объявления с базой
/// обязано останавливать старт: наследник без родителя поднялся бы с виду целым, но без половины
/// полей, и заметили бы это не в тесте, а на выгрузке в бухгалтерию.</para>
/// </summary>
[Collection("Integration")]
public class CoreRecordTypeProjectionTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task<DocumentType> MakeTypeAsync(
        string code, string name, string module, DocumentTypeKind kind = DocumentTypeKind.Composite)
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var type = DocumentType.Create(name, code, kind, null,
            JsonDocument.Parse("""{"fields":[]}"""), module, TypeVisibility.Shared);
        await repo.AddAsync(type);
        await repo.SaveChangesAsync();
        return type;
    }

    private static ModuleTypeSpec Derived(string parentCode, string module = TypeOwner.Core) =>
        new(module, "ТестСотрудник", "Тестовый сотрудник", SchemaEditLevel.Extendable,
            [new("ТабельныйНомер", "Табельный номер", "string",
                Tags: [FunctionalTag.EmployeePersonnelNumber], Locked: false)],
            Kind: DocumentTypeKind.Composite, Parent: parentCode);

    // ── Сторож задачи ─────────────────────────────────────────────────────────

    /// <summary>
    /// Родитель находится ПО КОДУ и проставляется. Это и есть то, ради чего проекция училась
    /// наследованию: без родителя «Сотрудник» не знал бы ФИО.
    /// </summary>
    [Fact]
    public async Task Наследник_находит_родителя_по_коду()
    {
        var parent = await MakeTypeAsync("ТестПерсона", "Тестовое лицо", TypeOwner.Core);

        var child = await SendAsync(new ProjectModuleTypeCommand(Derived("ТестПерсона")));

        Assert.Equal(parent.Id, child.ParentId);
    }

    /// <summary>Повторный старт не сбивает родителя — проекция идемпотентна и в этой части.</summary>
    [Fact]
    public async Task Повторная_проекция_оставляет_родителя_на_месте()
    {
        var parent = await MakeTypeAsync("ТестПерсона", "Тестовое лицо", TypeOwner.Core);

        await SendAsync(new ProjectModuleTypeCommand(Derived("ТестПерсона")));
        var again = await SendAsync(new ProjectModuleTypeCommand(Derived("ТестПерсона")));

        Assert.Equal(parent.Id, again.ParentId);
    }

    [Fact]
    public async Task Родителя_с_таким_кодом_нет_старт_останавливается()
    {
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            SendAsync(new ProjectModuleTypeCommand(Derived("КодаНетВовсе"))));

        // Отказ обязан назвать код: иначе искать придётся по всему объявлению.
        Assert.Contains("КодаНетВовсе", ex.Message);
        Assert.Contains("без унаследованных полей", ex.Message);
    }

    /// <summary>
    /// Опора типа ядра обязана принадлежать ядру (ТЗ CORE-30). Ровно это проверяет путь
    /// администратора; проекция не вправе быть дырой мимо него.
    /// </summary>
    [Fact]
    public async Task Ядро_не_наследуется_от_типа_модуля()
    {
        await MakeTypeAsync("ТестЧужой", "Чужой тип", "id");

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            SendAsync(new ProjectModuleTypeCommand(Derived("ТестЧужой"))));

        Assert.Contains("CORE-30", ex.Message);
        Assert.Contains("модулю «id»", ex.Message);
    }

    [Fact]
    public async Task Род_родителя_и_наследника_совпадает()
    {
        await MakeTypeAsync("ТестДокумент", "Тестовый документ", TypeOwner.Core, DocumentTypeKind.Document);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            SendAsync(new ProjectModuleTypeCommand(Derived("ТестДокумент"))));

        Assert.Contains("внутри одного рода", ex.Message);
    }

    /// <summary>
    /// Имя занято — второго такого типа не заводим, и это найдено на ЖИВОЙ базе (issue #962). В
    /// рабочей базе тип с кодом «Персона» носит имя «Сотрудник»; проекция завела бы второй тип с
    /// тем же именем, и после этого из редактора не сохранился бы НИ ОДИН из двух — уникальность
    /// имени запретила бы обоих. Проверка кода тут не помогает: коды как раз разные.
    ///
    /// <para>Объявлению МОДУЛЯ это отказ старта: модуль включает администратор, и выключить его —
    /// действие, которое ему доступно.</para>
    /// </summary>
    [Fact]
    public async Task Имя_занято_другим_типом_второго_такого_не_заводим()
    {
        await MakeTypeAsync("ТестПерсона", "Тестовый сотрудник", TypeOwner.Core);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            SendAsync(new ProjectModuleTypeCommand(
                new("id", "ДругойКод", "Тестовый сотрудник", SchemaEditLevel.Extendable,
                    [], Kind: DocumentTypeKind.Composite))));

        Assert.Contains("ТестПерсона", ex.Message);
        Assert.Contains("не сохранился бы ни один из двух", ex.Message);
    }

    /// <summary>
    /// А объявлению ЯДРА занятое имя — не отказ, а пропуск (ревью PR #1052).
    ///
    /// <para>Разница не в педантизме. Ядро выключить нельзя, поэтому его отказ означает
    /// неподнимаемую установку — а совет из текста отказа, «переименуйте существующий тип»,
    /// требует работающего приложения. Замкнутый круг: обновились и остались без системы.
    /// Пропуск не портит ничего — справочника просто нет, пока имя не освободят.</para>
    /// </summary>
    [Fact]
    public async Task Ядру_занятое_имя_не_отказ_а_пропуск()
    {
        await MakeTypeAsync(CoreRecordTypes.PersonCode, "Лицо", TypeOwner.Core);
        // Посторонний тип, назвавшийся так же, как будущий справочник.
        await MakeTypeAsync("ЧужойКод", "Сотрудник", "id");

        var projected = await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee));

        Assert.Null(projected);

        using var scope = fixture.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>().GetAllAsync();
        Assert.DoesNotContain(all, t => t.Code == CoreRecordTypes.EmployeeCode);
    }

    /// <summary>
    /// Объявление, которое о родителе МОЛЧИТ, родителя и не трогает (ревью PR #1052).
    ///
    /// <para>Найденный дефект: родитель ставился безусловно, а объявление модуля его не несёт —
    /// значит каждый старт обнулял бы родителя у всех типов модулей. Ставит его администратор
    /// (тип модуля вправе опереться на тип ядра, ТЗ CORE-30), и тип молча терял бы унаследованные
    /// поля: форма нарисовалась бы, печать промолчала, а виновником выглядел бы кто угодно, кроме
    /// перезапуска.</para>
    /// </summary>
    [Fact]
    public async Task Объявление_без_родителя_не_обнуляет_родителя()
    {
        var parent = await MakeTypeAsync("ТестОпора", "Тестовая опора", TypeOwner.Core);
        var spec = new ModuleTypeSpec("id", "ТестЗапись", "Тестовая запись", SchemaEditLevel.Extendable,
            [new("Поле", "Поле", "string")], Kind: DocumentTypeKind.Composite);

        var created = await SendAsync(new ProjectModuleTypeCommand(spec));

        // Администратор назначает родителя — штатное действие.
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var stored = await repo.GetByIdAsync(created!.Id);
            stored!.SetParent(parent.Id);
            repo.Update(stored);
            await repo.SaveChangesAsync();
        }

        var again = await SendAsync(new ProjectModuleTypeCommand(spec));

        Assert.Equal(parent.Id, again!.ParentId);
    }

    /// <summary>
    /// Отказ ядра не называет ядро модулем. Мелочь ровно до того момента, когда читатель пойдёт
    /// искать выключатель модуля «core», которого не существует.
    /// </summary>
    [Fact]
    public async Task Отказ_ядра_не_называет_его_модулем()
    {
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            SendAsync(new ProjectModuleTypeCommand(Derived("КодаНетВовсе"))));

        Assert.StartsWith("Ядро", ex.Message);
        Assert.DoesNotContain("модуль «core»", ex.Message);
    }

    /// <summary>
    /// ЧИСТАЯ УСТАНОВКА: справочника лиц нет, и справочник сотрудников не заводится — но старт не
    /// падает.
    ///
    /// <para>⚠️ Этот тест поймал настоящий дефект. Первая редакция отказывала на отсутствующем
    /// родителе всегда, и живая проверка на копии рабочей базы прошла зелёной — там «Персона»
    /// есть. На новой установке её нет ни одной: справочники заводил человек, а не код, — и
    /// приложение не поднялось бы вовсе. Проверка «обновились» показывает только одну из двух
    /// дорог, и вторая длиннее.</para>
    /// </summary>
    [Fact]
    public async Task На_чистой_установке_справочник_не_заводится_и_старт_не_падает()
    {
        var projected = await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee));

        Assert.Null(projected);

        using var scope = fixture.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>().GetAllAsync();
        Assert.DoesNotContain(all, t => t.Code == CoreRecordTypes.EmployeeCode);
    }

    /// <summary>
    /// А вот у ЗАВЕДЁННОГО справочника родитель пропасть не вправе: «Сотрудник» остался бы без
    /// ФИО. Послабление для чистой установки сюда не распространяется — иначе оно молча
    /// прикрывало бы потерю половины сведений у работающего заказчика.
    /// </summary>
    [Fact]
    public async Task У_заведённого_справочника_родитель_пропасть_не_вправе()
    {
        var parent = await MakeTypeAsync(CoreRecordTypes.PersonCode, "Лицо", TypeOwner.Core);
        Assert.NotNull(await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee)));

        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var stored = await repo.GetByIdAsync(parent.Id);
            stored!.Rename("Лицо", "ПерсонаПереименована");
            repo.Update(stored);
            await repo.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee)));

        Assert.Contains(CoreRecordTypes.PersonCode, ex.Message);
    }

    // ── Справочник сотрудников ────────────────────────────────────────────────

    /// <summary>
    /// Объявление ядра проецируется целиком: скелет на месте, поля размечены, замки расставлены.
    ///
    /// <para>Проверяется именно ОБЪЯВЛЕНИЕ из <see cref="CoreRecordTypes" />, а не выдуманная
    /// копия: разойдись они — тест остался бы зелёным, проверяя сам себя.</para>
    /// </summary>
    [Fact]
    public async Task Справочник_сотрудников_проецируется_с_замками_и_тэгами()
    {
        await MakeTypeAsync(CoreRecordTypes.PersonCode, "Лицо", TypeOwner.Core);

        var employee = await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee));

        Assert.Equal(TypeOwner.Core, employee.Module);
        Assert.Equal(SchemaEditLevel.Extendable, employee.EditLevel);
        Assert.NotNull(employee.ParentId);

        var fields = employee.Schema!.RootElement.GetProperty("fields");
        var byKey = fields.EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);

        // Табельный номер заполняет ЧЕЛОВЕК: запертым он не заполнился бы никогда — кода, который
        // его кладёт, нет и не предвидится.
        Assert.False(byKey["ТабельныйНомер"].GetProperty("locked").GetBoolean());
        // А связь с учётной записью кладёт код, и руками её трогать нельзя.
        Assert.True(byKey["УчётнаяЗапись"].GetProperty("locked").GetBoolean());

        foreach (var (key, tag) in new[]
        {
            ("ТабельныйНомер", FunctionalTag.EmployeePersonnelNumber),
            ("ПринятС", FunctionalTag.EmployeeEmployedFrom),
            ("УволенС", FunctionalTag.EmployeeEmployedTo),
            ("УчётнаяЗапись", FunctionalTag.EmployeeAccount),
        })
            Assert.Contains(tag, byKey[key].GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    /// <summary>
    /// Заказчик наращивает схему (ТЗ CORE-7.1), и проекция его работу не трогает: добавленное поле
    /// переживает следующий старт. Без этого «расширяемый» уровень был бы обещанием на словах.
    /// </summary>
    [Fact]
    public async Task Поле_заказчика_переживает_следующий_старт()
    {
        await MakeTypeAsync(CoreRecordTypes.PersonCode, "Лицо", TypeOwner.Core);
        var employee = await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee));

        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var stored = await repo.GetByIdAsync(employee.Id);
            var schema = JsonDocument.Parse("""
                {"fields":[{"key":"Разряд","title":"Разряд","type":"number"}]}
                """);
            // Кладём поле заказчика рядом со скелетом — так же, как это сделает редактор типов.
            var merged = System.Text.Json.Nodes.JsonNode.Parse(stored!.Schema!.RootElement.GetRawText())!;
            ((System.Text.Json.Nodes.JsonArray)merged["fields"]!)
                .Add(System.Text.Json.Nodes.JsonNode.Parse(
                    schema.RootElement.GetProperty("fields")[0].GetRawText()));
            stored.UpdateSchema(JsonDocument.Parse(merged.ToJsonString()));
            repo.Update(stored);
            await repo.SaveChangesAsync();
        }

        var again = await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee));

        var keys = again.Schema!.RootElement.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("key").GetString()).ToList();
        Assert.Contains("Разряд", keys);
        Assert.Contains("ТабельныйНомер", keys);
    }

    // ── Классификатор видов работ: цель поля по коду (issue #963) ─────────────

    /// <summary>Объявление с одним полем-ссылкой — чтобы отказы проверялись на нём, а не на живом.</summary>
    private static ModuleTypeSpec WithUnit(
        string targetCode, string module = TypeOwner.Core, string kind = "complex") =>
        new(module, "ТестВидРаботы", "Тестовый вид работы", SchemaEditLevel.Extendable,
            [new("ЕдиницаИзмерения", "Ед. изм.", kind, Required: true, Locked: false, Target: targetCode)],
            Kind: DocumentTypeKind.Composite);

    /// <summary>
    /// СТОРОЖ ЗАДАЧИ. Классификатор находит справочник единиц ПО КОДУ и ссылается на него
    /// идентификатором той установки, в которой поднялся.
    ///
    /// <para>Проверяется настоящее объявление из <see cref="CoreRecordTypes" />: разойдись оно с
    /// выдуманной копией — тест проверял бы сам себя.</para>
    /// </summary>
    [Fact]
    public async Task Классификатор_ссылается_на_единицы_по_коду()
    {
        var unit = await MakeTypeAsync(CoreRecordTypes.UnitCode, "Единица измерения", TypeOwner.Core);

        var classifier = await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkType));

        Assert.NotNull(classifier);
        Assert.Equal(TypeOwner.Core, classifier.Module);
        Assert.Equal(SchemaEditLevel.Extendable, classifier.EditLevel);

        var byKey = classifier.Schema!.RootElement.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("key").GetString()!);

        Assert.Equal(unit.Id.ToString(), byKey["ЕдиницаИзмерения"].GetProperty("typeId").GetString());
        Assert.Equal("complex", byKey["ЕдиницаИзмерения"].GetProperty("type").GetString());
        Assert.True(byKey["ЕдиницаИзмерения"].GetProperty("required").GetBoolean());
        // Заполняет её человек — запертой она не заполнилась бы никогда.
        Assert.False(byKey["ЕдиницаИзмерения"].GetProperty("locked").GetBoolean());

        foreach (var (key, tag) in new[]
        {
            ("ТребуетМесто", FunctionalTag.WorkRequiresLocation),
            ("ТребуетМатериалы", FunctionalTag.WorkRequiresMaterials),
            ("ВходитВИД", FunctionalTag.WorkProducesId),
        })
            Assert.Contains(tag, byKey[key].GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    /// <summary>
    /// Имя цели администратор переименовывает, и ссылка это обязана переживать: ищем по коду,
    /// который постоянен (ТЗ TYPE-6). Искали бы по имени — переименование единиц молча оставило бы
    /// классификатор без единицы измерения.
    /// </summary>
    [Fact]
    public async Task Переименование_единиц_ссылку_не_рвёт()
    {
        var unit = await MakeTypeAsync(CoreRecordTypes.UnitCode, "Единица измерения", TypeOwner.Core);
        await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkType));

        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var stored = await repo.GetByIdAsync(unit.Id);
            stored!.Rename("Единицы (СИ)", CoreRecordTypes.UnitCode);
            repo.Update(stored);
            await repo.SaveChangesAsync();
        }

        var again = await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkType));

        Assert.Equal(unit.Id.ToString(), again!.Schema!.RootElement.GetProperty("fields")
            .EnumerateArray().First(f => f.GetProperty("key").GetString() == "ЕдиницаИзмерения")
            .GetProperty("typeId").GetString());
    }

    /// <summary>
    /// ЧИСТАЯ УСТАНОВКА: справочника единиц в ней нет — его заводит человек. Классификатор не
    /// заводится, и старт НЕ падает: отказ означал бы, что новая установка не поднимается вовсе.
    /// </summary>
    [Fact]
    public async Task Единиц_нет_классификатор_не_заводится_и_старт_не_падает()
    {
        Assert.Null(await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkType)));

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        Assert.Empty(await repo.FindAsync(t => t.Code == CoreRecordTypes.WorkTypeCode));
    }

    /// <summary>
    /// А у УЖЕ ЗАВЕДЁННОГО классификатора цель пропасть не вправе: поле осталось бы без цели — с
    /// виду целое, но не заполняемое. Это отказ старта, а не пропуск.
    /// </summary>
    [Fact]
    public async Task У_заведённого_классификатора_цель_пропасть_не_вправе()
    {
        var unit = await MakeTypeAsync(CoreRecordTypes.UnitCode, "Единица измерения", TypeOwner.Core);
        await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkType));

        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var stored = await repo.GetByIdAsync(unit.Id);
            repo.Remove(stored!);
            await repo.SaveChangesAsync();
        }

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkType)));
        Assert.Contains(CoreRecordTypes.UnitCode, refusal.Message);
        Assert.Contains("без цели", refusal.Message);
    }

    /// <summary>
    /// Цель из ЧУЖОГО модуля запрещена (ТЗ CORE-30). Для ядра это особенно важно: выключить его
    /// нельзя, а половина его справочника уехала бы вместе с выключенным модулем.
    /// </summary>
    [Fact]
    public async Task Цель_из_чужого_модуля_запрещена()
    {
        await MakeTypeAsync("ЕдиницаМодуля", "Единица модуля", "id");

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new ProjectModuleTypeCommand(WithUnit("ЕдиницаМодуля"))));
        Assert.Contains("CORE-30", refusal.Message);
    }

    /// <summary>
    /// Род цели сверяется: составное поле умеет показывать только составной тип. Документ в нём не
    /// нарисовался бы — и понял бы это администратор по пустому месту в форме.
    /// </summary>
    [Fact]
    public async Task Цель_не_того_рода_отказ()
    {
        await MakeTypeAsync("ЕдиницаДокументом", "Единица документом", TypeOwner.Core,
            DocumentTypeKind.Document);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new ProjectModuleTypeCommand(WithUnit("ЕдиницаДокументом"))));
        Assert.Contains("не составной тип", refusal.Message);
    }

    /// <summary>
    /// Цель у вида поля, который её не читает, — отказ ДО записи: ссылка легла бы в схему рядом с
    /// видом, который на неё не смотрит, и поле нарисовалось бы пустым.
    /// </summary>
    [Fact]
    public async Task Вид_поля_не_принимающий_цель_отказ()
    {
        await MakeTypeAsync(CoreRecordTypes.UnitCode, "Единица измерения", TypeOwner.Core);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new ProjectModuleTypeCommand(WithUnit(CoreRecordTypes.UnitCode, kind: "string"))));
        Assert.Contains("цели не принимает", refusal.Message);
    }

    /// <summary>
    /// И обратное: составное поле БЕЗ цели не объявляется. Иначе в схеме оказалось бы поле, которому
    /// нечего показывать, — самый тихий способ получить пустую форму.
    /// </summary>
    [Fact]
    public async Task Составное_поле_без_цели_отказ()
    {
        var blind = new ModuleTypeSpec(TypeOwner.Core, "ТестБезЦели", "Тест без цели",
            SchemaEditLevel.Extendable,
            [new("ЕдиницаИзмерения", "Ед. изм.", "complex", Required: true, Locked: false)],
            Kind: DocumentTypeKind.Composite);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => SendAsync(new ProjectModuleTypeCommand(blind)));
        Assert.Contains("без цели", refusal.Message);
    }
}
