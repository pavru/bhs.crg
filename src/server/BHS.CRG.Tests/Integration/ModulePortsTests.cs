using System.Text;
using System.Text.Json;
using BHS.CRG.Api.Activity;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Catalog;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порты ядра для модулей работают на живом приложении (задача M2 этапа 2, issue #1069, ТЗ CORE-2,
/// CORE-3, CORE-34).
///
/// <para>Почему настоящий хост, а не поддельный контейнер служб. Порт — это обещание «модуль получит
/// это из приложения», и ломается оно не в коде переходника, а в СБОРКЕ: не зарегистрировали, область
/// не та, зависимость появилась позже. Такое видно только там, где контейнер собран целиком, — и
/// первым на это наступил бы модуль в бою.</para>
///
/// <para>⚠️ Хост живёт на своей базе и включает ОБА модуля (<c>id,costs</c>): так проверяется и
/// журнал с префиксом модуля, и состав поставки из двух кодов. Общая тестовая база не годится по той
/// же причине, что у <see cref="CostsOnlyHost" />: состав системных ролей приводится при старте к
/// объявленному, и хост с другим набором модулей менял бы права ролям у соседних классов.</para>
/// </summary>
public class ModulePortsTests(ModulePortsHost host) : IClassFixture<ModulePortsHost>, IAsyncLifetime
{
    /// <summary>
    /// База чистится перед каждым тестом: журнал и справочник здесь проверяются ЕДИНСТВЕННОЙ записью,
    /// а второй прогон на той же машине шёл бы по накопленным строкам — и «Assert.Single» упал бы не
    /// от дефекта.
    /// </summary>
    public async Task InitializeAsync() => await host.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Сторож задачи ─────────────────────────────────────────────────────────

    /// <summary>
    /// Каждый порт, объявленный в контрактах, приложение выдаёт.
    ///
    /// Это главный сторож задачи: он перечисляет порты ОТРАЖЕНИЕМ, а не списком, поэтому новый порт
    /// без переходника роняет тест со своим именем — и роняет на прогоне решения, а не первым
    /// обращением модуля в бою. Обратное тоже важно: порт, у которого переходник потеряли при правке
    /// регистрации, здесь виден сразу.
    /// </summary>
    [Fact]
    public void Каждый_порт_получается_из_приложения()
    {
        // Реализует МОДУЛЬ, а не ядро: приложению его регистрировать нечем — обработчик появляется
        // вместе с операцией, которую модуль объявил в своём RegisterServices.
        Dictionary<string, string> providedByModules = new()
        {
            [nameof(IModuleJobHandler)] = "обработчик фоновой операции регистрирует сам модуль",
        };

        using var scope = host.Services.CreateScope();

        var missing = typeof(IEnabledModules).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Namespace == typeof(IEnabledModules).Namespace)
            .Where(t => !providedByModules.ContainsKey(t.Name))
            .Where(t => scope.ServiceProvider.GetService(t) is null)
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Порт объявлен в контрактах модулей, но приложение его не выдаёт:\n  " +
            string.Join("\n  ", missing) + "\n\n" +
            "Модуль попросит службу, которой в контейнере нет, и отказ придёт первым обращением в " +
            "бою. Добавьте переходник в ModulePorts.AddModulePorts — либо, если порт реализует сам " +
            "модуль, впишите его в providedByModules с причиной.");
    }

    // ── Журнал действий ───────────────────────────────────────────────────────

    /// <summary>Запись модуля доезжает до журнала ядра — того же, что показывает действия ядра.</summary>
    [Fact]
    public async Task Запись_модуля_доезжает_до_журнала_ядра()
    {
        var action = ProbeModuleActivity.InvoicePaid;

        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IModuleActivityLog>()
            .RecordAsync(action, targetId: "42", targetLabel: "Счёт № 42", before: "не оплачен", after: "оплачен");

        var written = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 50, action.Code);

        var record = Assert.Single(written);
        Assert.Equal("Счёт № 42", record.TargetLabel);
        Assert.Equal("оплачен", record.After);
        // Вне запроса автор — сам экземпляр, а не пустота: имя в журнале обязано читаться словом.
        Assert.Equal(ActivityActor.System.Name, record.ActorName);
        Assert.Null(record.ActorId);
    }

    /// <summary>
    /// НАЗВАНИЕ действия модуля доезжает до экрана журнала и до отбора по действию.
    ///
    /// ⚠️ Сторож находки ревью PR #1106. В записи журнала лежит только код, а название берётся из
    /// каталога при чтении — и каталог ядра для модульного кода возвращал сам код: строка читалась
    /// «costs.invoice.paid», в отборе модульных действий не было вовсе, то есть название, переданное
    /// при записи, не доезжало до человека НИКАК. Проверяется тот самый каталог, которым отвечают
    /// адреса журнала.
    /// </summary>
    [Fact]
    public void Название_действия_модуля_доезжает_до_экрана_журнала()
    {
        var catalog = host.Services.GetRequiredService<ActivityActionCatalog>();

        Assert.Equal(ProbeModuleActivity.InvoicePaid.Title, catalog.Title(ProbeModuleActivity.InvoicePaid.Code));
        Assert.Contains(catalog.All, a => a.Code == ProbeModuleActivity.InvoicePaid.Code);
        // Действия ядра из каталога не пропали: он общий, а не «вместо».
        Assert.Contains(catalog.All, a => a.Code == ActivityActions.ModulesChanged.Code);
    }

    /// <summary>
    /// Незнакомое действие писать нельзя — в том числе с чужим префиксом.
    ///
    /// Первая половина проверки объявления (три части, непустое название) живёт в
    /// <see cref="Configuration.ModulePortMirrorTests.Действие_журнала_проверяет_свою_форму" />; здесь
    /// вторая: действие, которого модуль не объявлял, в каталог не попало, и записать его значит
    /// оставить в журнале строку без названия и без строки в отборе. Действие ядра модулю недоступно
    /// тем же отказом — иначе на экране журнала его нельзя было бы отличить от настоящего.
    /// </summary>
    [Theory]
    [InlineData("core.user.deleted")]
    [InlineData("work.invoice.paid")]
    [InlineData("costs.invoice.cancelled")]
    public async Task Журнал_отказывает_необъявленному_действию(string code)
    {
        using var scope = host.Services.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<IModuleActivityLog>();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => log.RecordAsync(new ModuleActivityAction(code, "Что-то произошло")));

        Assert.Contains(code, refusal.Message);
    }

    /// <summary>
    /// Название, разошедшееся с объявленным, — отказ.
    ///
    /// Молча оно означало бы, что на экране стоит одно название, а автор вызова уверен в другом: две
    /// копии одного действия разъехались бы, и заметить это было бы нечем.
    /// </summary>
    [Fact]
    public async Task Журнал_отказывает_названию_мимо_объявления()
    {
        using var scope = host.Services.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<IModuleActivityLog>();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => log.RecordAsync(
            new ModuleActivityAction(ProbeModuleActivity.InvoicePaid.Code, "Счёт оплатили")));

        Assert.Contains(ProbeModuleActivity.InvoicePaid.Title, refusal.Message);
    }

    /// <summary>
    /// Негодное объявление роняет СБОРКУ каталога, а каталог разрешается при старте — то есть отказ
    /// приходит тому, кто собирал поставку, а не читателю журнала.
    ///
    /// Проверяются три способа испортить объявление: чужой префикс (модуль пишет от имени ядра),
    /// негодная форма кода и повтор кода — последний решал бы молча, как читается уже записанное.
    /// </summary>
    [Theory]
    [InlineData("core.user.deleted", "Удалён пользователь", "core")]
    [InlineData("costs.paid", "Оплачен", "трёх частей")]
    [InlineData("costs.invoice.paid", "Другое название", "дважды")]
    public void Каталог_действий_отказывает_негодному_объявлению(string code, string title, string expected)
    {
        var modules = host.Services.GetRequiredService<ModuleRegistry>();
        IModuleActivityActions[] declarations = [new ProbeModuleActivity(), new BrokenActions(code, title)];

        var refusal = Assert.Throws<InvalidOperationException>(
            () => new ActivityActionCatalog(declarations, modules));

        Assert.Contains(expected, refusal.Message);
    }

    private sealed class BrokenActions(string code, string title) : IModuleActivityActions
    {
        public IReadOnlyList<ModuleActivityAction> Actions => [new(code, title)];
    }

    // ── Файлы ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Файл модуля кладётся, читается, удаляется — и попадает в реестр блобов.
    ///
    /// Реестр здесь и есть причина, по которой порт существует: файл, сложенный мимо обёртки ядра,
    /// не попал бы ни в оценку веса резервной копии, ни в проверку «этот путь вообще наш», и
    /// обнаружилось бы это как «копия меньше, чем данных».
    /// </summary>
    [Fact]
    public async Task Файл_модуля_кладётся_читается_и_виден_реестру()
    {
        using var scope = host.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IModuleBlobs>();
        var content = Encoding.UTF8.GetBytes("скан");

        var path = await blobs.PutAsync("Счёт.pdf", new MemoryStream(content), "application/pdf");

        Assert.Equal(content.Length, await blobs.SizeAsync(path));
        using (var read = new StreamReader(await blobs.OpenAsync(path)))
            Assert.Equal("скан", await read.ReadToEndAsync());

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.BlobRegistry.AnyAsync(b => b.Path == path),
            "Файл модуля лёг мимо реестра блобов: резервная копия его не заметит, а выдача не " +
            "признает своим.");

        await blobs.DeleteAsync(path);
        Assert.Null(await blobs.SizeAsync(path));
    }

    // ── Уведомления ───────────────────────────────────────────────────────────

    /// <summary>
    /// Уведомление модуля уходит с объявленной аудиторией и отказывает с необъявленной.
    ///
    /// Отказ здесь важнее успеха: опечатка в коде права адресует уведомление НИКОМУ, и выглядит это
    /// как удавшаяся отправка — запись создана, в списке её ни у кого нет.
    /// </summary>
    [Fact]
    public async Task Уведомление_модуля_требует_объявленного_адресата()
    {
        using var scope = host.Services.CreateScope();
        var notifications = scope.ServiceProvider.GetRequiredService<IModuleNotifications>();

        var id = await notifications.PublishAsync(ModuleNotificationLevel.Info,
            "Счёт разобран", "Счёт № 42 разобран", audience: "costs.invoice.read");
        Assert.NotEqual(Guid.Empty, id);

        await Assert.ThrowsAsync<ArgumentException>(() => notifications.PublishAsync(
            ModuleNotificationLevel.Warning, "Никому", "Ничего", audience: "costs.nosuch.perm"));

        // Адресат, названный дважды, — тоже отказ: «право» и «пользователь» суть разные списки
        // получателей, и молчаливый выбор одного из них выглядел бы отправкой тем, кого не называли.
        await Assert.ThrowsAsync<InvalidOperationException>(() => notifications.PublishAsync(
            ModuleNotificationLevel.Info, "Двоим", "Ничего",
            audience: "costs.invoice.read", userId: Guid.NewGuid()));
    }

    // ── Пользователь и права ──────────────────────────────────────────────────

    /// <summary>
    /// Вне запроса у модуля нет ни пользователя, ни прав — «прав нет», а не «можно всё».
    ///
    /// Проверяется именно этот случай, потому что в нём легче всего ошибиться: у фоновой операции и
    /// у старта приложения принципала нет, и порт, отвечающий «прав нет» отказом, уронил бы их, а
    /// отвечающий «можно всё» открыл бы их данные без спроса.
    /// </summary>
    [Fact]
    public async Task Вне_запроса_у_модуля_нет_пользователя_и_прав()
    {
        using var scope = host.Services.CreateScope();
        var user = scope.ServiceProvider.GetRequiredService<IModuleUser>();

        Assert.Null(user.Id);
        Assert.Equal(ActivityActor.System.Name, user.Name);
        Assert.Empty(await user.PermissionsAsync());
        Assert.False(await user.HasAsync("costs.invoice.read"));
    }

    // ── Справочники ───────────────────────────────────────────────────────────

    /// <summary>
    /// Справочник читается через порт — и записи берутся ОТТУДА, КУДА ПИШЕТ ЭКРАН.
    ///
    /// <para>⚠️ Прежний тест заводил запись прежней модели (<c>CreateCatalogEntityCommand</c>) и по ней
    /// же спрашивал порт: писал и читал ОДНУ таблицу, а экран пишет в другую. Поэтому он проходил и с
    /// видом «Organization» — вида с таким кодом в системе нет вовсе. На живых данных порт отвечал бы
    /// пустотой на каждый настоящий поставщик, и в реестре счетов это выглядело бы как «ссылка есть,
    /// названия нет», то есть как потеря. Здесь запись заводится КОМАНДОЙ ОБЩИХ ДАННЫХ — той самой,
    /// которой пользуется раздел «Общие данные», — и вид называется кодом типа.</para>
    /// </summary>
    [Fact]
    public async Task Справочник_ядра_читается_через_порт()
    {
        var created = await OrganizationAsync("ООО «Поставщик»", """{"ИНН":"7701234567"}""");

        using var scope = host.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IModuleCatalog>();

        var found = Assert.Single(await catalog.ListAsync("Организация"), e => e.Id == created);
        Assert.Equal("ООО «Поставщик»", found.DisplayName);
        Assert.Contains("7701234567", found.DataJson);
        Assert.Equal("Организация", found.EntityType);

        Assert.NotNull(await catalog.GetAsync(created));
        Assert.Null(await catalog.GetAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// Запись ПОДТИПА приходит в списке вида: подтип организации — организация.
    ///
    /// <para>У заказчика так и есть: живые организации лежат подтипом «ОрганизацияСРО», и поставщик
    /// может быть любым из подтипов. Отбирай порт строго по одному типу, список поставщиков был бы
    /// пустым при полном справочнике — ровно тот отказ, который не выглядит отказом. Так же считает и
    /// ядро, когда решает, годится ли запись полю.</para>
    /// </summary>
    [Fact]
    public async Task Подтип_вида_приходит_в_списке_вида()
    {
        var parent = await TypeIdAsync("Организация");
        var subtype = await SendAsync(new CreateDocumentTypeCommand(
            $"Подрядчик {Guid.NewGuid():N}", $"Подрядчик{Guid.NewGuid():N}", DocumentTypeKind.Composite,
            parent, JsonDocument.Parse("""{"fields":[]}""")));

        var entry = await SendAsync(new CreateCommonDataEntryCommand(
            "ООО «Подрядчик»", subtype.Id, JsonDocument.Parse("{}"), CatalogScope.System, null, null));

        using var scope = host.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IModuleCatalog>();

        var found = Assert.Single(await catalog.ListAsync("Организация"), e => e.Id == entry.Id);

        // Вид — свойство ЗАПИСИ, а не запроса: спросили «Организация», а запись своего подтипа.
        Assert.Equal(subtype.Code, found.EntityType);
    }

    /// <summary>
    /// «Вида нет» отличимо от «записей нет»: ноль против пустого списка.
    ///
    /// <para>Сведи мы эти два ответа, опечатка в коде вида и «организаций ещё не завели» выглядели бы
    /// одинаково — пустым выпадающим списком, и выбирать между объяснениями пришлось бы человеку.
    /// Именно так и жил этот порт: в контракте стояли английские имена видов, по которым список
    /// приходил пустым, и ни одного отказа при этом не случалось.</para>
    ///
    /// <para>Отказом порт не отвечает нарочно: тип-справочник заводит человек, и его отсутствие —
    /// состояние установки, а не промах модуля. Что показать, решает модуль.</para>
    /// </summary>
    [Fact]
    public async Task Нет_вида_и_нет_записей_это_разные_ответы()
    {
        var empty = await TypeIdAsync($"ВидБезЗаписей{Guid.NewGuid():N}");

        using var scope = host.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IModuleCatalog>();

        Assert.Null(await catalog.ListAsync("Органиазция"));
        Assert.Empty((await catalog.ListAsync(await CodeOfAsync(empty)))!);
    }

    /// <summary>Код типа по идентификатору — чтобы спросить порт тем же словом, каким тип заведён.</summary>
    private async Task<string> CodeOfAsync(Guid typeId)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        return (await repo.GetByIdAsync(typeId))!.Code;
    }

    /// <summary>
    /// Записи приходят ПО НАЗВАНИЮ — как обещает контракт.
    ///
    /// ⚠️ Сторож находки ревью PR #1106: запрос ядра не сортирует вовсе, порядок приходит от базы и
    /// меняется после правок и уборки. Обещание «по названию» сбывалось бы, пока записи не правили, —
    /// то есть до первой же правки у заказчика, где список читает человек.
    /// </summary>
    [Fact]
    public async Task Справочник_отдаётся_по_названию()
    {
        foreach (var name in (string[])["Яшма", "Берёза", "Ёлка", "Дуб"])
            await OrganizationAsync(name, "{}");

        using var scope = host.Services.CreateScope();
        var entries = await scope.ServiceProvider.GetRequiredService<IModuleCatalog>().ListAsync("Организация");

        Assert.Equal(["Берёза", "Дуб", "Ёлка", "Яшма"],
            entries.Where(e => e.DisplayName is "Яшма" or "Берёза" or "Ёлка" or "Дуб").Select(e => e.DisplayName));
    }

    // ── Охрана записи ─────────────────────────────────────────────────────────

    /// <summary>
    /// Охрана записи через порт видит запертое поле модуля — и молчит там, где трогать нечего.
    ///
    /// Вторая половина обязательна: проверка «отказ пришёл» зелена и у охраны, которая отказывает
    /// всегда, — а такая охрана делает записи нередактируемыми.
    /// </summary>
    [Fact]
    public async Task Охрана_записи_отвечает_модулю_находками()
    {
        var type = await SendAsync(new ProjectModuleTypeCommand(new ModuleTypeSpec(
            "costs", "COSTS_PORT_GUARD", "Счёт (сторож портов)", SchemaEditLevel.Closed,
            [new ModuleFieldSpec("Сумма", "Сумма счёта", "number")])));

        using var scope = host.Services.CreateScope();
        var guard = scope.ServiceProvider.GetRequiredService<IModuleWriteGuard>();

        var refusals = await guard.RefusalsAsync(type!.Id, "{}", """{"Сумма":1000}""");

        var refusal = Assert.Single(refusals);
        Assert.Equal(RecordWriteGuard.LockedField, refusal.Code);
        Assert.Equal("Сумма", refusal.Path);

        Assert.Empty(await guard.RefusalsAsync(type.Id, """{"Сумма":1000}""", """{"Сумма":1000}"""));
    }

    /// <summary>
    /// Неизвестный тип — отказ, а не пустой список находок.
    ///
    /// ⚠️ Сторож находки ревью PR #1106: охрана ядра на отсутствующем типе молча выходит (у её путей
    /// тип проверен раньше), и через порт это читалось бы как «запись разрешена» — модуль сохранил бы
    /// данные, тронув запертые поля, без какой-либо охраны. Отказ обязан отличаться от разрешения.
    /// </summary>
    [Fact]
    public async Task Охрана_записи_отказывает_на_неизвестном_типе()
    {
        var unknown = Guid.NewGuid();

        using var scope = host.Services.CreateScope();
        var guard = scope.ServiceProvider.GetRequiredService<IModuleWriteGuard>();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => guard.RefusalsAsync(unknown, "{}", """{"Сумма":1000}"""));

        Assert.Contains(unknown.ToString(), refusal.Message);
    }

    // ── Учётный период ────────────────────────────────────────────────────────

    /// <summary>
    /// Закрытых периодов пока не бывает, и это правда об экземпляре, а не заглушка: закрывать период
    /// нечем — служба закрытия приезжает задачей E1a этапа 2 (issue #1081).
    ///
    /// Тест стоит здесь, чтобы ответ «не закрыто ничего» не остался незамеченным решением: когда
    /// служба появится, он упадёт и потребует переписать себя под неё.
    /// </summary>
    [Fact]
    public async Task Периоды_пока_не_закрываются()
    {
        using var scope = host.Services.CreateScope();
        var periods = scope.ServiceProvider.GetRequiredService<IModulePeriods>();

        Assert.Null(await periods.ClosedThroughAsync());
        Assert.False(await periods.IsClosedAsync(DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    // ── Состав поставки ───────────────────────────────────────────────────────

    /// <summary>
    /// Состав поставки виден модулю КОДАМИ, а не объектами модулей.
    ///
    /// Вторая половина — отражением: реестр модулей лежит в том же контейнере, и «узкий порт» легко
    /// становится широким одной строкой. Получив объект чужого модуля, модуль может позвать его
    /// <c>MapEndpoints</c> или прочитать его права — то есть сделать то, что запрещено на уровне
    /// ссылок, не добавив ни одной ссылки.
    /// </summary>
    [Fact]
    public void Состав_поставки_виден_кодами_а_не_объектами()
    {
        var modules = host.Services.GetRequiredService<IEnabledModules>();

        Assert.Equal(["id", "costs"], modules.Codes);
        Assert.True(modules.IsEnabled("COSTS"), "Код модуля сверяется без учёта регистра.");
        Assert.False(modules.IsEnabled("work"));

        var leaks = typeof(IEnabledModules).GetMembers()
            .SelectMany(Types)
            .Where(t => typeof(IAppModule).IsAssignableFrom(t)
                || (t.IsGenericType && t.GetGenericArguments().Any(a => typeof(IAppModule).IsAssignableFrom(a))))
            .Select(t => t.Name)
            .Distinct()
            .ToList();

        Assert.True(leaks.Count == 0,
            "Через IEnabledModules наружу выдаётся объект модуля: " + string.Join(", ", leaks) + ".\n" +
            "Порт отвечает на вопрос «включён ли», и ответом обязаны быть коды: в объекте модуля " +
            "лежат его права и регистрация адресов — то есть способ одному модулю влезть в другой.");
    }

    private static IEnumerable<Type> Types(System.Reflection.MemberInfo member) => member switch
    {
        System.Reflection.MethodInfo method =>
            [method.ReturnType, .. method.GetParameters().Select(p => p.ParameterType)],
        System.Reflection.PropertyInfo property => [property.PropertyType],
        _ => [],
    };

    // ── Фоновые задачи ────────────────────────────────────────────────────────

    /// <summary>
    /// Фоновая операция модуля доезжает до его обработчика и докладывает ход.
    ///
    /// Сквозной прогон, а не проверка постановки: между «задача в очереди» и «работа выполнена» лежит
    /// то, чего в контрактах не видно — вид задачи, аргументы с кодом операции, поиск исполнителя в
    /// корне композиции и фоновый цикл в инфраструктуре. Задача, не дошедшая до обработчика, осталась
    /// бы в очереди навсегда, а на экране выглядела бы идущей.
    /// </summary>
    [Fact]
    public async Task Фоновая_операция_модуля_выполняется_и_докладывает_ход()
    {
        var probe = host.Services.GetRequiredService<ProbeModuleWork>();
        var target = Guid.NewGuid();

        using var scope = host.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IModuleJobs>();

        var jobId = await jobs.EnqueueAsync(
            ProbeModuleWork.Code, target, "Проба работы модуля", """{"строк":3}""");

        var run = await probe.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(jobId, run.JobId);
        Assert.Equal(target, run.TargetId);
        Assert.Equal("""{"строк":3}""", run.Payload);
        // Владельца у задачи нет: её поставили вне запроса — так же живёт плановая резервная копия.
        Assert.Equal(Guid.Empty, run.UserId);

        var state = await WaitForAsync(jobs, jobId, ModuleJobStatus.Succeeded);
        Assert.Null(state.Error);
        Assert.Equal("3 из 3 строк", state.Progress);
        Assert.Equal("Проба работы модуля", state.Title);
    }

    /// <summary>
    /// Операция, которую никто не умеет выполнять, отказывает СРАЗУ — и задачи не остаётся.
    ///
    /// Иначе она встала бы в очередь и осталась там: в базе «ожидает», на экране «идёт», ни отказа,
    /// ни результата. Проверяется и то, что строки в таблице задач не появилось: отказ после записи
    /// оставил бы висящую задачу, а тест был бы зелёным.
    /// </summary>
    [Fact]
    public async Task Операция_без_обработчика_отказывает_до_постановки()
    {
        var target = Guid.NewGuid();

        using var scope = host.Services.CreateScope();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
            .GetRequiredService<IModuleJobs>()
            .EnqueueAsync("costs.никто-не-умеет", target, "Проба", null));

        Assert.Contains("costs.никто-не-умеет", refusal.Message);
        Assert.Contains(ProbeModuleWork.Code, refusal.Message);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Jobs.AnyAsync(j => j.TargetId == target),
            "Отказ пришёл, а задача в очереди осталась — она не выполнится никогда.");
    }

    /// <summary>
    /// Задача модуля читается по идентификатору независимо от того, кто её поставил — и «ничейная»
    /// тоже.
    ///
    /// ⚠️ Сторож находки ревью PR #1106: первая редакция спрашивала задачу правами текущего
    /// пользователя, то есть модуль, показывающий ход по сохранённому идентификатору, получал «нет
    /// такой задачи» и для задачи по расписанию (владельца нет вовсе), и для задачи, поставленной
    /// другим человеком (счёт открыли вдвоём). Задача модуля принадлежит ЗАПИСИ, а не человеку:
    /// привязки данных к пользователю в системе нет вовсе (issue #675).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Задача_модуля_читается_независимо_от_владельца(bool ownerless)
    {
        var owner = ownerless ? Guid.Empty : Guid.NewGuid();
        var jobId = await SeedJobAsync(JobKind.ModuleWork, owner, "Разбор выгрузки");

        using var scope = host.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<IModuleJobs>().GetAsync(jobId);

        Assert.NotNull(state);
        Assert.Equal("Разбор выгрузки", state!.Title);
        Assert.Equal(ModuleJobStatus.Queued, state.Status);
    }

    /// <summary>
    /// Ход операций ЯДРА модулю не виден: по чужому идентификатору порт отвечает «нет такой задачи».
    ///
    /// Половина, без которой предыдущая проверка опасна: сняв проверку владельца, легко снять и
    /// проверку вида — и тогда модуль, подставив идентификатор, узнавал бы ход резервного копирования
    /// или распознавания. Такого окна у него нет ни одним другим способом.
    /// </summary>
    [Fact]
    public async Task Операции_ядра_модулю_не_видны()
    {
        var jobId = await SeedJobAsync(JobKind.CreateBackup, Guid.NewGuid(), "Резервная копия");

        using var scope = host.Services.CreateScope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<IModuleJobs>().GetAsync(jobId));
    }

    /// <summary>
    /// Код операции обязан начинаться с кода включённого модуля.
    ///
    /// Без префикса два модуля, назвавшие операцию «import», столкнулись бы — и отказ при постановке
    /// пришёл бы обоим, не называя, чья операция лишняя. Проверка не доказывает, что операцию ставит
    /// именно тот модуль (кто вызвал порт, контейнеру неизвестно), и не притворяется этим: она убирает
    /// столкновение имён.
    /// </summary>
    [Theory]
    [InlineData("проба")]
    [InlineData("work.проба")]
    public async Task Операция_обязана_быть_названа_кодом_модуля(string operation)
    {
        using var scope = host.Services.CreateScope();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
            .GetRequiredService<IModuleJobs>()
            .EnqueueAsync(operation, Guid.NewGuid(), "Проба", null));

        Assert.Contains(operation, refusal.Message);
        // Именно про префикс, а не «исполнителя нет»: без этой строки тест был бы зелёным и со снятой
        // проверкой — операцию без обработчика отвергает следующая проверка, другими словами.
        Assert.Contains("кода модуля", refusal.Message);
    }

    private async Task<Guid> SeedJobAsync(JobKind kind, Guid owner, string title)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Прямо в таблицу, мимо очереди: фоновый цикл иначе подобрал бы задачу и уронил её на
        // аргументах — а проверяется здесь чтение, а не выполнение.
        var job = Job.Create(kind, owner, Guid.NewGuid(), title);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private static async Task<ModuleJobState> WaitForAsync(
        IModuleJobs jobs, Guid jobId, ModuleJobStatus expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var state = await jobs.GetAsync(jobId);
            Assert.NotNull(state);
            if (state!.Status == expected) return state;

            Assert.True(DateTime.UtcNow < deadline,
                $"Задача {jobId} так и не пришла в состояние {expected}: сейчас {state.Status}, {state.Error}.");
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Организация — ТАК, КАК ЕЁ ЗАВОДИТ ЭКРАН: запись общих данных типа «Организация». Не прежней
    /// моделью: в неё не пишет ни один экран, и тест на ней сходился бы сам с собой.
    /// </summary>
    private async Task<Guid> OrganizationAsync(string name, string data) =>
        (await SendAsync(new CreateCommonDataEntryCommand(
            name, await TypeIdAsync("Организация"), JsonDocument.Parse(data),
            CatalogScope.System, null, null))).Id;

    /// <summary>
    /// Идентификатор типа по коду; тип-справочник заводится, если его нет.
    ///
    /// <para>Заводить приходится потому, что «Организация» — тип, который у заказчика создал ЧЕЛОВЕК:
    /// ни ядро, ни миграция его не создают (миграция лишь передаёт ему владельца). На чистой базе его
    /// нет — и именно поэтому тип счёта объявлен с послаблением.</para>
    /// </summary>
    private async Task<Guid> TypeIdAsync(string code)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();

        if ((await repo.FindAsync(t => t.Code == code)).FirstOrDefault() is { } existing) return existing.Id;

        var created = DocumentType.Create(code, code, DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
        await repo.AddAsync(created);
        await repo.SaveChangesAsync();
        return created.Id;
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
