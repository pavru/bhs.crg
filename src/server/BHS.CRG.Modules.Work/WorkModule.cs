using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Modules.Work;

/// <summary>
/// Модуль «Учёт работ» (этап 4, ТЗ WORK-1): что сделано на стройке за день и сколько времени это заняло.
/// Назначения, отчёты монтажников, их приёмка, сверка бригад и закрытие периода; принятые объёмы — другим
/// модулям, без часов и людей.
///
/// <para>Первая дверь — назначения (change <c>work-11-assignments</c>): кто на какой стройке монтажник и
/// менеджер, «мои стройки» и охват менеджера. До неё был КАРКАС (<c>work-10-skeleton</c>): объявление, права и
/// своя схема без таблиц. Каркас первым проверил то, что у четвёртого
/// модуля с данными ломается молча: приложение поднимается только с <c>work</c>, без <c>id</c>,
/// <c>costs</c> и <c>plan</c> (<c>WorkOnlyHostTests</c>), а объявленные права наполняют составы системных
/// ролей — и тем самым меняют их навигацию.</para>
///
/// <para>Устроен как <c>costs</c> и <c>plan</c>: отдельный проект, которому доступны только контракты ядра
/// и домен. Всё, что модулю нужно от ядра или от другого модуля, приходит портами и контрактами ядра
/// (<c>CORE-34</c>), а не ссылкой — стережёт <c>ModuleBoundaryTests</c>.</para>
/// </summary>
public sealed class WorkModule : IAppModule
{
    public const string ModuleCode = "work";

    /// <summary>Свои отчёты: ввод и подача (ТЗ WORK-45) — одним местом, его назовут адреса и составы ролей.</summary>
    public const string ReportOwn = "work.report.own";

    /// <summary>Приёмка и возврат отчётов своих строек, ввод за сотрудника (ТЗ WORK-45).</summary>
    public const string ReportReview = "work.report.review";

    /// <summary>
    /// Назначения на свои стройки (ТЗ WORK-21, WORK-45).
    ///
    /// <para>⚠️ Код назван НЕ так, как в таблице ТЗ: там <c>work.assign</c> — из двух частей, а `AUTH-1`
    /// требует <c>модуль.объект.действие</c> (см. <see cref="ReportRead" />). Тот же случай, что
    /// <c>costs.allocate</c> → <c>costs.allocation.edit</c> у счетов.</para>
    /// </summary>
    public const string Assign = "work.assignment.edit";

    /// <summary>Назначать и принимать на любых стройках — «или администратор» из WORK-21 (ТЗ WORK-46).</summary>
    public const string SitesAll = "work.sites.all";

    /// <summary>
    /// Чтение всех отчётов (ТЗ WORK-39, WORK-45: «через <c>*.read.all</c>»).
    ///
    /// <para>⚠️ Код назван НЕ так, как стояло в таблице ТЗ до каркаса: там был <c>work.report.read.all</c> —
    /// из четырёх частей, а `AUTH-1` требует <c>модуль.объект.действие</c>, и <c>AppPermission.Validate</c>
    /// останавливает старт на любом другом виде. Тот же случай, что <c>costs.report</c> у счетов
    /// (<c>CostsModule</c>): взята форма модели (решение владельца 13.11.2026), таблица ТЗ поправлена тем же
    /// PR. Суффикс <c>.all</c> не нужен и по смыслу — в «читать всё» право входит пометкой
    /// <see cref="ReadAllMark.In" />, а не именем.</para>
    /// </summary>
    public const string ReportRead = "work.report.read";

    public string Code => ModuleCode;

    public string Title => "Учёт работ";

    /// <summary>
    /// Права модуля (ТЗ WORK-45, WORK-46) — каждое с объяснением: галку в редакторе ролей ставит человек.
    ///
    /// <para>⚠️ Здесь пять прав из восьми, и это правило этапа, а не недоделка (D2 зонтика
    /// <c>work-00-epic</c>): право объявляется вместе с первым адресом, который оно открывает.
    /// <c>work.crew.approve</c> придёт с бригадой (<c>work-18</c>), <c>work.facts.read</c> — право
    /// контракта — с «Фактическими работами» (<c>work-27</c>), <c>work.devices.manage</c> — с этапом 5.
    /// Объявленное раньше, право было бы галкой, которая не делает ничего, — а «Инженер ИД» с одним
    /// <c>work.facts.read</c> до признака права контракта у него получил бы пустой модуль.</para>
    ///
    /// <para>Дверь есть пока у одного: назначения (<c>work.assignment.edit</c>, <c>work-11</c>); отчёты придут с
    /// <c>work-13</c>. Объявлены права в каркасе нарочно, как у <c>plan</c>: составы ролей проверяются на живых
    /// правах сразу. Права без двери названы поимённо в <c>WorkOnlyHostTests</c>, и запись уходит оттуда, как
    /// только дверь появится.</para>
    ///
    /// <para>«Свои стройки» — стройки с действующим назначением «менеджер» (WORK-46): поля «менеджер» у
    /// стройки нет, в ядро привязка данных к пользователю не вводится (инвариант #675). Граница
    /// проходит параметром доступа (D4 зонтика), а не глобальным фильтром.</para>
    /// </summary>
    public IReadOnlyList<AppPermission> Permissions =>
    [
        new(ReportOwn,
            "вводить и подавать свои отчёты о работах: строки, часы, фото",
            "только свои отчёты — того сотрудника, с которым связана учётная запись (WORK-47), и только по " +
            "стройкам, на которые он назначен (WORK-21)",
            ["core.files.use"],
            ReadAllMark.Out("вводит и подаёт отчёты")),

        new(ReportReview,
            "принимать и возвращать отчёты своих строек, вводить отчёт за сотрудника",
            "отчёты строек, на которые пользователь назначен менеджером (WORK-46), со строками, часами и " +
            "сотрудниками; на любых стройках — вместе с правом «на любых стройках»",
            [Assign, "core.employees.read"],
            ReadAllMark.Out("принимает, возвращает и вводит отчёты")),

        new(Assign,
            "назначать сотрудников на свои стройки на период — монтажником или менеджером",
            "назначения строек, на которые пользователь назначен менеджером (WORK-21, WORK-46): от них " +
            "зависит, по каким стройкам сотрудник подаёт отчёты и кто их принимает",
            [ReportReview, "core.employees.read"],
            ReadAllMark.Out("назначает и снимает сотрудников")),

        new(SitesAll,
            "назначать и принимать отчёты на любых стройках, не будучи назначенным на них менеджером",
            "снимает границу «свои стройки» у назначений и приёмки — назначения и отчёты всех строек " +
            "экземпляра; последнего менеджера стройки снимает только оно (WORK-21). Без этих прав не даёт " +
            "ничего",
            [Assign, ReportReview],
            ReadAllMark.Out("назначает и принимает на любых стройках")),

        new(ReportRead,
            "читать отчёты всех строек: строки, часы, фото и журнал — без приёмки и правки",
            "все отчёты экземпляра на чтение, с часами и сотрудниками (WORK-39); действий приёмки не " +
            "открывает. Принятые объёмы без часов и людей другим модулям выдаёт своё право контракта",
            ["core.constructions.read", "core.employees.read"],
            ReadAllMark.In),
    ];

    /// <summary>
    /// Своя схема базы (ТЗ CORE-4, D1 зонтика). Ядро мигрирует её при старте — сразу после своей и только
    /// если модуль включён. Имя названо и здесь, и в контексте: <c>dotnet ef</c> создаёт контекст вне
    /// приложения, где реестра модулей нет, — ядро сверяет два объявления.
    /// </summary>
    public ModuleSchema? Schema => new(WorkDbContext.SchemaName, typeof(WorkDbContext));

    /// <summary>
    /// Разделы ядра, которыми пользуется учёт работ (ТЗ AUTH-16.1): стройки — назначения ведутся по ним
    /// (WORK-21); справочники — классификатор видов работ, из которого строка отчёта выбирает работу
    /// (CORE-8); сотрудники — монтажник есть сотрудник учётной записи (WORK-47), и дверь к их реквизитам
    /// — только их экран (C15, <c>work-01</c>).
    ///
    /// <para>Учётный период (WORK-15) дописывает <c>work-19</c>, «Ход работ» — <c>work-27</c>, каждый своей
    /// строкой и вместе с тем, что модуль там показывает.</para>
    /// </summary>
    public IReadOnlyList<string> UsesSections => [CoreSections.Constructions, CoreSections.Catalog, CoreSections.Employees];

    /// <summary>
    /// Колонки, держащие записи ядра (ТЗ CORE-34.1): назначение держит сотрудника и стройку.
    /// </summary>
    public IReadOnlyList<ModuleReference> References => WorkReferences.All;

    /// <summary>
    /// Свой контекст, действия журнала и ворота назначений. Строку подключения модуль берёт из настроек
    /// приложения; что она задана, проверяет ядро при старте, раньше модулей. Регистрирует контекст
    /// модуль, а мигрирует ядро: выключенный модуль не регистрирует ничего (AUTH-19), и его схема
    /// остаётся такой, какой была.
    ///
    /// <para>Действия журнала — одиночкой, как у <c>costs</c> и <c>plan</c>: каталог действий ядра собирает
    /// объявления один раз при старте. Ворота и охват — на запрос: «свои» стройки обязаны читаться заново
    /// после каждого снятия (design <c>work-11</c>).</para>
    /// </summary>
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<WorkDbContext>(options =>
            WorkDbContext.Configure(options, configuration.GetConnectionString("Postgres")));
        services.AddSingleton<IModuleActivityActions, AssignmentActions>();
        services.AddScoped<ManagerSites>();
        services.AddScoped<AssignmentGate>();
    }

    /// <summary>
    /// Путь модуля. Объявлен ДО первого адреса: по нему ядро отвечает отказом «модуль не подключён» на
    /// выключенном экземпляре, — без объявления <c>/api/work/...</c> давал бы пустой 404, неотличимый
    /// от опечатки (ТЗ OVW-10, AUTH-15, AUTH-19).
    /// </summary>
    public IReadOnlyList<string> RoutePrefixes => ["/api/work"];

    /// <summary>
    /// Адреса модуля. Ворота (<c>module:work</c>) стоят на группе целиком, поэтому каждый адрес здесь закрыт
    /// самим фактом регистрации — права же на нём названы поимённо: доступ к модулю и право на назначения —
    /// разные вещи. Первые адреса — назначения и «мои стройки» (<c>work-11</c>).
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        AssignmentEndpoints.Map(endpoints);
        MySitesEndpoint.Map(endpoints);
    }

    /// <summary>
    /// Инициализировать нечего: справочников у каркаса нет, а системные роли («Монтажник», «Менеджер
    /// проекта») заводит ядро (<c>SystemRoles</c>) — их составы названы там целиком, включая права этого
    /// модуля, и наполняются ими, как только права объявлены здесь.
    /// </summary>
    public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}
