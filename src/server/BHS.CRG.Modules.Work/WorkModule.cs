using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Crew;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Devices;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Exchange;
using BHS.CRG.Modules.Work.Photos;
using BHS.CRG.Modules.Work.Review;
using BHS.CRG.Modules.Work.Closing;
using BHS.CRG.Modules.Work.Submission;
using BHS.CRG.Modules.Work.Tables;
using BHS.CRG.Modules.Tables;
using Microsoft.AspNetCore.Authentication;
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
public sealed class WorkModule : IAppModule, IFieldReportExchange
{
    public const string ModuleCode = "work";

    /// <summary>Свои отчёты: ввод и подача (ТЗ WORK-45) — одним местом, его назовут адреса и составы ролей.</summary>
    public const string ReportOwn = "work.report.own";

    /// <summary>Приёмка и возврат отчётов своих строек, ввод за сотрудника (ТЗ WORK-45).</summary>
    public const string ReportReview = "work.report.review";

    public const string CrewApprove = "work.crew.approve";

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

    public const string FactsRead = "work.facts.read";
    public const string DevicesManage = "work.devices.manage";

    private const string PhotoGeolocationNote =
        "Если в исходном снимке есть читаемая геометка, координаты места съёмки сохраняются отдельно от файла как данные фото.";

    public string Code => ModuleCode;

    public string Title => "Учёт работ";

    /// <summary>
    /// Права модуля (ТЗ WORK-45, WORK-46) — каждое с объяснением: галку в редакторе ролей ставит человек.
    ///
    /// <para>Право объявляется вместе с первым адресом или контрактом, который оно открывает
    /// (D2 зонтика <c>work-00-epic</c>).</para>
    ///
    /// <para>Двери есть у четырёх: назначения (<c>work.assignment.edit</c>, <c>work-11</c>), свои отчёты
    /// (<c>work.report.own</c>) и ввод за сотрудника (<c>work.report.review</c>, <c>work-13</c>), чтение всех отчётов
    /// (<c>work.report.read</c>) — журнал отчёта (<c>work-14</c>); остальное чтение придёт с <c>work-16</c>. Объявлены права в каркасе нарочно, как у <c>plan</c>: составы ролей проверяются на живых
    /// правах сразу. Права без двери названы поимённо в <c>WorkOnlyHostTests</c>, и запись уходит оттуда, как
    /// только дверь появится.</para>
    ///
    /// <para>«Свои стройки» — стройки с действующим назначением «менеджер» (WORK-46): поля «менеджер» у
    /// стройки нет, в ядро привязка данных к пользователю не вводится (инвариант #675). Граница
    /// проходит параметром доступа (D4 зонтика), а не глобальным фильтром.</para>
    /// </summary>
    public IReadOnlyList<AppPermission> Permissions =>
    [
        new(DevicesManage, "выдавать приглашения и отзывать полевые устройства",
            "управляет устройствами сотрудников своих строек. Право «на любых стройках» расширяет охват на весь экземпляр.",
            ["core.employees.read"], ReadAllMark.Out("выдаёт приглашения и отзывает устройства")),

        new(ReportOwn,
            "вводить и подавать свои отчёты о работах: строки, часы, фото",
            "только свои отчёты — того сотрудника, с которым связана учётная запись (WORK-47), и только по " +
            "стройкам, на которые он назначен (WORK-21). Фото таких отчётов можно просматривать. " +
            "Новые фото можно добавлять при редактировании отчёта в открытом периоде. " +
            PhotoGeolocationNote,
            ["core.files.use"],
            ReadAllMark.Out("вводит и подаёт отчёты")),

        new(ReportReview,
            "принимать и возвращать отчёты своих строек, вводить отчёт за сотрудника",
            "отчёты строек, на которые пользователь назначен менеджером (WORK-46), со строками, часами и " +
            "сотрудниками. На любых стройках — вместе с правом «на любых стройках». " +
            "Фото видны при доступе к содержимому отчёта. Новые фото можно добавлять при редактировании " +
            "отчёта в открытом периоде. " + PhotoGeolocationNote,
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

        new(CrewApprove,
            "утверждать итоговый объём бригадной работы своих строек",
            "сохраняет единый итог сверки бригадных строк поданных отчётов и записывает решение в журналы отчётов",
            [],
            ReadAllMark.Out("утверждает итоговый объём бригадной работы")),

        new(ReportRead,
            "читать отчёты всех строек: строки, часы, фото и журнал — без приёмки и правки",
            "все отчёты экземпляра на чтение, с часами и сотрудниками (WORK-39). Фото видны при доступе " +
            "к содержимому отчёта. Действий приёмки не открывает. " +
            "Принятые объёмы без часов и людей другим модулям выдаёт своё право контракта. " +
            PhotoGeolocationNote,
            ["core.constructions.read", "core.employees.read"],
            ReadAllMark.In),

        new(FactsRead,
            "читать принятые объёмы и выработку по всем стройкам",
            "даёт другим модулям текущий факт по позициям перечня, числовую диагностику и выработку по виду работ; " +
            "часы и сотрудники остаются в учёте работ",
            [],
            ReadAllMark.Out("модуль «Учёт работ» не открывает"), Contract: true),
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
    /// <para>Раздел «Ход работ» объявляется вместе с экраном в <c>plan-25</c>.</para>
    /// </summary>
    public IReadOnlyList<string> UsesSections =>
        [CoreSections.Constructions, CoreSections.Catalog, CoreSections.Employees, CoreSections.Progress];

    public IReadOnlyList<ModuleRoleNeed> Needs =>
    [
        new(FunctionalTag.RoleNomenclature, "Выбор материалов в отчёте о работах",
            "Номенклатуру в строке отчёта не выбрать, пока справочник не настроен."),
    ];

    /// <summary>
    /// Колонки, держащие записи ядра (ТЗ CORE-34.1): назначение держит сотрудника и стройку, отчёт и его строки —
    /// сотрудника, стройку, раздел, вид работы с вариантом, единицу, позицию перечня и номенклатуру.
    /// </summary>
    public IReadOnlyList<ModuleReference> References => WorkReferences.All;

    /// <summary>
    /// Типы модуля (ТЗ CORE-19.2, CORE-20.2; <c>work-13</c>): закрытая шапка отчёта монтажника и расширяемые
    /// сведения строки работы — колонки показаны в них системными полями.
    /// </summary>
    public IReadOnlyList<ModuleRecordType> RecordTypes => WorkRecordTypes.All;

    public IReadOnlyList<ModuleTable> Tables => [WorkTables.Reports, WorkTables.Lines];

    /// <summary>
    /// Файлы модуля (ТЗ WORK-35, CORE-26; <c>work-17</c>): снимок и превью фото строки — оба едут в копию. Превью
    /// перестраивать после восстановления пришлось бы отдельным путём, которого нет, а весит оно мало.
    /// </summary>
    public IReadOnlyList<ModuleFile> Files =>
    [
        ModuleFile.Holding("line_photos", "blob_path", "фото строк отчётов"),
        ModuleFile.Holding("line_photos", "preview_blob_path", "превью фото строк отчётов"),
        ModuleFile.Holding("report_lines", "details", "вложения в сведениях строк отчётов"),
        ModuleFile.Holding("report_versions", "content", "файлы прежних версий отчётов"),
        ModuleFile.Remembering("report_journal", "changes", "файлы держат строки и версии, журнал только помнит их пути"),
    ];

    /// <summary>Лимиты фото строки и пороги выборки выработки.</summary>
    public IReadOnlyList<BHS.CRG.Modules.Settings.ModuleSetting> Settings => WorkSettings.All;

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
        services.AddSingleton<IModuleActivityActions, ReportActions>();
        services.AddSingleton<IModuleActivityActions, DeviceActions>();
        services.AddScoped<ManagerSites>();
        services.AddScoped<DeviceSessions>();
        services.AddScoped<FieldReferences>();
        services.AddScoped<FieldMaterialHints>();
        services.AddSingleton<FieldReferencePages>();
        services.AddScoped<DeviceManagementScope>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, FieldDeviceAuthentication>(
            FieldDeviceAuthentication.SchemeName, _ => { });
        services.AddAuthorizationBuilder().AddPolicy(FieldReportEndpoints.DevicePolicy, policy =>
            policy.AddAuthenticationSchemes(FieldDeviceAuthentication.SchemeName).RequireAuthenticatedUser());
        services.AddScoped<AssignmentGate>();
        services.AddScoped<WorkPlanItemLink>();
        services.AddScoped<ReportReferenceCheck>();
        services.AddScoped<ReportReads>();
        services.AddScoped<IOwnReportReader, OwnReportReader>();
        services.AddModuleContract<IActualWorks, ActualWorksProvider>(ModuleCode);
        services.AddModuleContract<IWorkProductivity, WorkProductivityProvider>(ModuleCode);
        services.AddModuleContract<IProgressContribution, WorkProgressContribution>(ModuleCode);
        services.AddScoped<Labor.LaborSnapshots>();
        services.AddScoped<Labor.Productivity>();
        services.AddScoped<Labor.LaborAnalytics>();
        services.AddScoped<WorkTableReaders>();
        services.AddScoped<Summary.SummaryQuery>();
        services.AddScoped<WorkTableNames>();
        services.AddScoped<ReportTableRows>();
        services.AddScoped<LineTableRows>();
        services.AddScoped<ReportPeriods>();
        services.AddScoped<IModuleClosingReport, WorkClosingReport>();
        services.AddScoped<ReportWriter>();
        services.AddScoped<CrewCandidates>();
        services.AddScoped<CrewInputs>();
        services.AddScoped<CrewGroups>();
        services.AddScoped<ReportViews>();
        services.AddScoped<ReportEntrySites>();
        services.AddScoped<MaterialHintAccess>();
        services.AddScoped<MaterialHintEstimate>();
        services.AddScoped<MaterialHintChoices>();
        services.AddScoped<ReportHistory>();
        services.AddScoped<ReportCorrection>();
        services.AddScoped<ReportJournal>();
        services.AddScoped<ReportPeople>();
        services.AddScoped<ReportActivity>();
        services.AddScoped<ReportDesk>();
        services.AddScoped<ReturnNotice>();
        services.AddScoped<ReportReview>();
        services.AddScoped<SubmittedDigest>();
        services.AddScoped<RequiredFields>();
        services.AddScoped<ReportSubmission>();
        services.AddScoped<LinePhotos>();
        services.AddScoped<IModuleJobHandler, AutoSubmitJob>();
    }

    /// <summary>
    /// Такт автоподачи (ТЗ WORK-10, WORK-41; <c>work-15</c>): ядро ставит задачу <see cref="AutoSubmitJob" /> раз в пять
    /// минут с постоянной целью, а задача выбирает все черновики с истёкшим окном — догон после простоя.
    /// </summary>
    public IReadOnlyList<ModuleTick> Ticks => [AutoSubmitJob.Tick];

    /// <summary>
    /// Путь модуля. Объявлен ДО первого адреса: по нему ядро отвечает отказом «модуль не подключён» на
    /// выключенном экземпляре, — без объявления <c>/api/work/...</c> давал бы пустой 404, неотличимый
    /// от опечатки (ТЗ OVW-10, AUTH-15, AUTH-19).
    /// </summary>
    public IReadOnlyList<string> RoutePrefixes => ["/api/work", "/api/field"];

    internal const int MinimumFieldSenderVersionCode = 1;

    int IFieldReportExchange.MinimumSenderVersionCode => MinimumFieldSenderVersionCode;

    void IFieldReportExchange.MapReports(IEndpointRouteBuilder fieldV1)
    {
        FieldReportEndpoints.Map(fieldV1);
        FieldObservationEndpoints.Map(fieldV1);
        FieldReferences.Map(fieldV1);
    }

    /// <summary>
    /// Адреса модуля. Ворота (<c>module:work</c>) стоят на группе целиком, поэтому каждый адрес здесь закрыт
    /// самим фактом регистрации — права же на нём названы поимённо: доступ к модулю и право на назначения —
    /// разные вещи. Назначения и «мои стройки» (<c>work-11</c>), свои отчёты и ввод за сотрудника (<c>work-13</c>),
    /// журнал отчёта (<c>work-14</c>), приёмка и возврат (<c>work-16</c>), фото строки (<c>work-17</c>).
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        AssignmentEndpoints.Map(endpoints);
        MySitesEndpoint.Map(endpoints);
        ReportEndpoints.Map(endpoints);
        ReportDetailsSchema.Map(endpoints);
        EntryReportEndpoints.Map(endpoints);
        ReportEntryCatalog.Map(endpoints);
        MaterialsHint.Map(endpoints);
        ReportJournalEndpoint.Map(endpoints);
        LaborEndpoints.Map(endpoints);
        ReportReviewEndpoints.Map(endpoints);
        ReportCorrection.Map(endpoints);
        ReviewQueue.Map(endpoints);
        ReviewCard.Map(endpoints);
        Summary.SummaryEndpoints.Map(endpoints);
        CrewCandidates.Map(endpoints);
        CrewGroups.Map(endpoints);
        PhotoEndpoints.Map(endpoints);
        DeviceManagementEndpoints.Map(endpoints);
        FieldSessionEndpoints.Map(endpoints);
    }

    /// <summary>
    /// Инициализировать нечего: справочников у каркаса нет, а системные роли («Монтажник», «Менеджер
    /// проекта») заводит ядро (<c>SystemRoles</c>) — их составы названы там целиком, включая права этого
    /// модуля, и наполняются ими, как только права объявлены здесь.
    /// </summary>
    public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}
