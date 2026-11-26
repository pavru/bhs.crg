using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Plan.Calendars;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Distribution;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Plan.Exchange;
using BHS.CRG.Modules.Plan.Import;
using BHS.CRG.Modules.Plan.Matching;
using BHS.CRG.Modules.Plan.Printing;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Printing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Modules.Plan;

/// <summary>
/// Модуль «Планирование» (этап 3, ТЗ PLAN-1): сметы стройки и их редакции, импорт из ГРАНД-Сметы,
/// распределение по разделам, коммерческие предложения, прайс-лист, графики с календарями и плановые
/// данные для других модулей.
///
/// <para>Каркас (change <c>plan-01-skeleton</c>) первым проверил то, что у третьего модуля с данными
/// ломается молча: приложение поднимается только с <c>plan</c>, без <c>id</c> и <c>costs</c>
/// (<c>PlanOnlyHostTests</c>), а объявленные права наполняют составы системных ролей — и тем самым меняют
/// их навигацию. Первые таблицы и адреса — модель сметы (change <c>plan-06-estimate-model</c>).</para>
///
/// <para>Устроен как <c>costs</c>: отдельный проект, которому доступны только контракты ядра и домен.
/// Всё, что модулю нужно от ядра или от другого модуля, приходит портами и контрактами ядра
/// (<c>CORE-34</c>), а не ссылкой — стережёт <c>ModuleBoundaryTests</c>.</para>
/// </summary>
public sealed class PlanModule : IAppModule
{
    public const string ModuleCode = "plan";

    /// <summary>Чтение смет — одним местом: его назовут адреса и составы ролей.</summary>
    public const string EstimateRead = PlannedHint.ReadPermission;

    /// <summary>Правка смет — одним местом, как <see cref="EstimateRead" />.</summary>
    public const string EstimateEdit = "plan.estimate.edit";

    /// <summary>Себестоимость сметы — одним местом: её спрашивают ответы смет и строк (ТЗ PLAN-11).</summary>
    public const string EstimateCost = "plan.estimate.cost";

    /// <summary>
    /// Что даёт <see cref="EstimateCost" /> — словами каталога прав. Одним местом и с объявлением права, и с
    /// носителем «скрыто правами»: колонка «нет права» называет право тем же именем, что редактор ролей, а
    /// сам каталог читающему смету закрыт (<c>/api/roles/permissions</c> — право на пользователей).
    /// </summary>
    public const string EstimateCostGives = "видеть себестоимость сметы: стоимость строк, итоги, расценки";

    /// <summary>
    /// Право контракта (ТЗ PLAN-23, решение В-2): материалы позиций сметы с количеством, без цен, — заявкам на
    /// закупку <c>costs</c>. Модуль «Планирование» не открывает (AUTH-8.1).
    /// </summary>
    public const string EstimateMaterials = "plan.estimate.materials";

    /// <summary>Право контракта (ТЗ PLAN-23, WORK-57): нормы чел.-ч позиций сметы без денег — «по смете — фактически».</summary>
    public const string LaborRead = "plan.labor.read";

    /// <summary>
    /// Право контракта (ТЗ PLAN-23, WORK-59): позиции номенклатуры материалов сметы стройки без количеств и
    /// цен — ярус «смета» подсказки монтажнику.
    /// </summary>
    public const string EstimateHint = PlannedHint.HintPermission;

    /// <summary>Чтение КП — цены продажи, наценки и итоги (ТЗ PLAN-11, PLAN-23); объявлено с адресами КП (<c>plan-13</c>).</summary>
    public const string OfferRead = "plan.offer.read";

    /// <summary>Правка КП: сделать из редакции, шапка, наценки, удаление (<c>plan-13</c>).</summary>
    public const string OfferEdit = "plan.offer.edit";

    public const string OfferSend = "plan.offer.send";

    public const string PriceListEdit = "plan.pricelist.edit";

    public const string CalendarEdit = "plan.calendar.edit";
    public const string ScheduleRead = "plan.schedule.read";
    public const string ScheduleEdit = "plan.schedule.edit";

    /// <summary>Почему права контракта не входят в «читать всё»: это выдача другим модулям (D12 зонтика).</summary>
    private static readonly ReadAllMark ContractOut = ReadAllMark.Out("выдача другим модулям: модуль «Планирование» не открывает");

    public string Code => ModuleCode;

    public string Title => "Планирование";

    /// <summary>
    /// Права модуля (ТЗ PLAN-23) — каждое с объяснением: галку в редакторе ролей ставит человек.
    ///
    /// <para>Право объявляется вместе с первым адресом или контрактом, который оно открывает
    /// (D12 зонтика <c>plan-00-epic</c>).</para>
    ///
    /// <para><b>Права контракта</b> (<see cref="EstimateMaterials" />, <see cref="LaborRead" />,
    /// <see cref="EstimateHint" />, <c>plan-23</c>) открывают по одной части «Плановых работ» тем, кому
    /// сметы не нужны, — «Снабженцу», «Менеджеру проекта», «Монтажнику». Модуль они не открывают
    /// (AUTH-8.1, <c>plan-21</c>): иначе у монтажника появилось бы пустое «Планирование».</para>
    ///
    /// <para>Чтение и правка объявлены ещё в каркасе, раньше двери, — чтобы составы ролей по
    /// <c>PLAN-23</c> проверялись на живых правах уже тогда; двери им дала модель сметы (<c>plan-06</c>).</para>
    ///
    /// <para>Чтение смет — без денег: себестоимость открывает <c>plan.estimate.cost</c> (<c>PLAN-2</c>,
    /// <c>PLAN-11</c>), иначе «Менеджер проекта», которому чтение выдано ради КП и графиков (решение
    /// В-1, <c>PLAN-Q9</c>), видел бы и её. Объявлено оно с моделью сметы — первым местом, где оно
    /// что-то закрывает (D12 зонтика).</para>
    /// </summary>
    public IReadOnlyList<AppPermission> Permissions =>
    [
        new(EstimateRead,
            "видеть сметы строек: строки, объёмы, распределение по разделам и редакции",
            "сметы всех строек экземпляра — состав работ и материалов и их объёмы, БЕЗ денег: себестоимость " +
            "открывает своё право; построчной изоляции по стройкам в первой версии нет",
            ["core.constructions.read", "core.catalog.read"],
            ReadAllMark.In),

        new(EstimateEdit,
            "импортировать сметы из ГРАНД-Сметы, сопоставлять позиции, распределять по разделам и выбирать " +
            "действующую редакцию",
            "то же, что чтение, плюс изменение: действующая редакция — та, из которой строятся КП и графики, " +
            "и от которой другие модули берут плановые объёмы",
            [EstimateRead, "core.worktypes.edit", "core.nomenclature.edit"],
            ReadAllMark.Out("импортирует и правит сметы, выбирает действующую редакцию")),

        new(EstimateCost,
            EstimateCostGives,
            "стоимость единицы и итог каждой строки, итоги редакций, цены и стоимость материалов и всё " +
            "сметное наполнение строк (расценка, коэффициенты, поля заказчика); без права сметы читаются " +
            "без денег",
            [EstimateRead],
            ReadAllMark.In),

        new(EstimateMaterials,
            "получать материалы позиций сметы стройки с количествами, без цен, — для заявок на закупку",
            "материалы действующих редакций смет стройки и их количество на объём позиции — контрактом " +
            "«Плановые работы» в модуле, который их спрашивает; модуль «Планирование» и сами сметы не открывает",
            // Обычно — с правкой заявок costs (costs.request.edit); подсказку впишет plan-26, объявив то право:
            // ссылка на необъявленный код показала бы в редакторе ролей галку, которой нет.
            [],
            ContractOut, Contract: true),

        new(LaborRead,
            "получать нормы чел.-ч позиций сметы стройки, без денег, — для сравнения «по смете — фактически»",
            "нормы трудозатрат действующих редакций смет стройки на единицу позиции перечня — контрактом " +
            "«Плановые работы» в модуле, который их спрашивает; модуль «Планирование» и сами сметы не открывает",
            ["work.report.review"],
            ContractOut, Contract: true),

        new(EstimateHint,
            "получать наименования материалов позиций сметы стройки, без цен и количеств, — подсказка монтажнику",
            "позиции номенклатуры материалов действующих редакций смет стройки — контрактом «Плановые работы» " +
            "в модуле, который их спрашивает; модуль «Планирование», сметы, количества и цены не открывает",
            ["work.report.own"],
            ContractOut, Contract: true),

        // КП (plan-13): цены продажи — своим правом, а не правом смет: «Сметчик» видит себестоимость и не видит
        // цен продажи (PLAN-2), «Менеджер проекта» — наоборот (PLAN-11). Себестоимость в ответе КП — по-прежнему
        // под plan.estimate.cost.
        new(OfferRead,
            "видеть коммерческие предложения и прайс-лист: цены продажи, наценки и итоги",
            "Прайс-лист и КП всех строек экземпляра — строки, цены продажи, наценки, НДС и итоги; себестоимость и маржу строк " +
            "открывает право на себестоимость сметы",
            [EstimateRead],
            ReadAllMark.In),

        new(OfferEdit,
            "делать КП из редакции сметы и по прайс-листу, править черновики и удалять их",
            "то же, что чтение КП, плюс изменение; КП, сделанное из редакции, фиксирует её — редакцию больше не " +
            "правят и не удаляют",
            [OfferRead, EstimateRead],
            ReadAllMark.Out("делает и правит коммерческие предложения, фиксирует редакции смет")),
        new(OfferSend,
            "отмечать отправку и итог коммерческого предложения",
            "ручная отметка отправки КП с датой, затем результата: принято, отклонено или без ответа; " +
            "отправленная версия не правится",
            [OfferRead],
            ReadAllMark.Out("отмечает отправку и итог коммерческого предложения")),
        new(PriceListEdit,
            "править прайс-лист",
            "заводить, изменять и снимать цены продажи за единицу вида работы с датой начала действия",
            [OfferRead],
            ReadAllMark.Out("правит цены продажи")),
        new(CalendarEdit,
            "править производственный календарь компании",
            "загружать и править рабочую неделю, часы в смене, праздники и переносы на год",
            [],
            ReadAllMark.Out("правит календарь компании")),
        new(ScheduleRead,
            "видеть графики и календари планирования",
            "читать производственный календарь компании, календари строек и графики работ",
            ["core.constructions.read"],
            ReadAllMark.In),
        new(ScheduleEdit,
            "править графики и календари строек",
            "задавать рабочую неделю, сменность и исключения календаря стройки и править её графики",
            [ScheduleRead],
            ReadAllMark.Out("правит графики и календари строек")),
    ];

    /// <summary>
    /// Своя схема базы (ТЗ CORE-4, D1 зонтика). Ядро мигрирует её при старте — сразу после своей и только
    /// если модуль включён. Имя названо и здесь, и в контексте: <c>dotnet ef</c> создаёт контекст вне
    /// приложения, где реестра модулей нет, — ядро сверяет два объявления.
    /// </summary>
    public ModuleSchema? Schema => new(PlanDbContext.SchemaName, typeof(PlanDbContext));

    /// <summary>Тип сметного наполнения строки — <see cref="PlanRecordTypes" /> (ТЗ TYPE-16).</summary>
    public IReadOnlyList<ModuleRecordType> RecordTypes => PlanRecordTypes.All;

    public IReadOnlyList<ModulePrintForm> PrintForms =>
    [
        new(PlanRecordTypes.OfferCode, PlanRecordTypes.Offer.Name, OfferRead, OfferPrintTemplate.Default, OfferPrintContext.Requisites),
        new(PlanRecordTypes.ScheduleCode, PlanRecordTypes.Schedule.Name, ScheduleRead,
            ScheduleWorksPrintTemplate.Default, ScheduleWorksPrintContext.Requisites),
    ];

    /// <summary>Тэг наценки КП (ТЗ TYPE-17): объявлен модулем, на экземпляре без планирования его нет.</summary>
    public IReadOnlyList<ModuleTag> Tags => PlanRecordTypes.Tags;

    /// <summary>
    /// КП — документ с суммами в валюте учёта (ТЗ CORE-20.1, D19 зонтика): пока есть хоть одно, валюту учёта не
    /// сменить — суммы не пересчитываются. Строки и наценки без КП не живут, поэтому названа одна таблица.
    /// </summary>
    public IReadOnlyList<ModuleAccountingDocument> AccountingDocuments => [new("offers", "коммерческих предложений")];

    /// <summary>
    /// Справочник номенклатуры — по роли, как у счетов (ТЗ TYPE-23, PLAN-9.1): на позицию ссылается
    /// материал строки сметы. Порт справочников отвечает только на объявленные роли.
    /// </summary>
    public IReadOnlyList<ModuleRoleNeed> Needs =>
    [
        new(BHS.CRG.Domain.Schema.FunctionalTag.RoleNomenclature,
            "Позиция материала в строке сметы",
            "Материалы строк сметы не сослаться на номенклатуру: редакция, где у материала выбрана позиция, " +
            "не записывается; материалы без позиции записываются и ждут сопоставления."),
        new(BHS.CRG.Domain.Schema.FunctionalTag.RoleOrganization,
            "Заказчик коммерческого предложения",
            "Заказчика КП не выбрать: КП делается и правится без заказчика."),
    ];

    public IReadOnlyList<ModuleReference> References => PlanReferences.All;

    public IReadOnlyList<ModuleFile> Files => PlanFiles.All;

    /// <summary>
    /// Разделы ядра, которыми пользуется планирование (ТЗ AUTH-16.1, выбор П0): стройки — у карточки
    /// стройки блок «Работы» («Сметы», «График»), и без них в пространстве «Планирование» нет «Строек»;
    /// справочники — классификатор видов работ и номенклатура, с которыми сопоставляют позиции сметы.
    ///
    /// <para>Учётного периода, наборов данных и сверки модулю не нужно. «Ход работ» дописывает
    /// <c>plan-25</c>, когда заведёт свою константу раздела.</para>
    ///
    /// <para>Сотрудники — там же, где справочники: реквизиты сотрудника правит только их экран (C15, work-01),
    /// и пространство со справочниками без него осталось бы без двери к сотрудникам.</para>
    /// </summary>
    public IReadOnlyList<string> UsesSections =>
        [CoreSections.Constructions, CoreSections.Catalog, CoreSections.Employees, CoreSections.Progress];

    /// <summary>
    /// Свой контекст, действия журнала, запись редакции и разбор файла импорта (фоновой операцией, <c>plan-07</c>). Строку подключения модуль берёт из настроек
    /// приложения; что она задана, проверяет ядро при старте, раньше модулей. Регистрирует контекст
    /// модуль, а мигрирует ядро: выключенный модуль не регистрирует ничего (AUTH-19), и его схема
    /// остаётся такой, какой была.
    ///
    /// <para>Действия журнала — одиночкой, как у <c>costs</c>: каталог действий ядра собирает объявления
    /// один раз при старте.</para>
    /// </summary>
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PlanDbContext>(options =>
            PlanDbContext.Configure(options, configuration.GetConnectionString("Postgres")));
        services.AddSingleton<IModuleActivityActions, EstimateActions>();
        services.AddSingleton<IModuleActivityActions, OfferActions>();
        services.AddScoped<Offers.OfferDesk>();
        services.AddKeyedScoped<IModulePrintContext, OfferPrintContext>(PlanRecordTypes.OfferCode);
        services.AddKeyedScoped<IModulePrintContext, ScheduleWorksPrintContext>(PlanRecordTypes.ScheduleCode);
        services.AddScoped<Offers.OfferLifecycle>();
        services.AddScoped<EstimateRevisionWriter>();
        services.AddScoped<IModuleJobHandler, EstimateImportJob>();
        // Подстановка запомненного при импорте (plan-08) — в точку расширения, оставленную plan-07.
        services.TryAddScoped<IEstimateRowMatcher, RememberedRowMatcher>();
        services.AddScoped<RowMatching>();
        services.AddScoped<MaterialMatching>();
        services.AddScoped<EstimateImportCommit>();
        services.AddScoped<EstimateAttach>();
        services.AddScoped<DistributionCarryOver>();
        services.AddScoped<DistributionReader>();
        services.AddScoped<DistributionWriter>();
        services.AddScoped<CalendarStore>();
        services.AddScoped<Schedules.ScheduleBuilder>();
        services.AddScoped<Schedules.ScheduleFacts>();
        services.AddScoped<Schedules.ScheduleViews>();
        services.AddScoped<Schedules.ScheduleWorkEditor>();
        services.AddScoped<Schedules.ScheduleLinkStore>();
        // Контракт «Плановые работы» (plan-23): потребители берут его портом IModuleContracts.
        services.AddModuleContract<IPlannedWorks, PlannedWorksProvider>(ModuleCode);
        services.AddModuleContract<IProgressContribution, PlanProgressContribution>(ModuleCode);
    }

    /// <summary>
    /// Путь модуля. Объявлен ДО первого адреса: по нему ядро отвечает отказом «модуль не подключён» на
    /// выключенном экземпляре, — без объявления <c>/api/plan/...</c> давал бы пустой 404, неотличимый
    /// от опечатки (ТЗ OVW-10, AUTH-15, AUTH-19).
    /// </summary>
    public IReadOnlyList<string> RoutePrefixes => ["/api/plan"];

    /// <summary>
    /// Адреса модуля. Ворота (<c>module:plan</c>) стоят на группе целиком, поэтому каждый адрес здесь
    /// закрыт самим фактом регистрации — права же на нём названы поимённо: доступ к модулю и право на
    /// сметы — разные вещи. Первые адреса — сметы (<c>plan-06</c>).
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        EstimateEndpoints.Map(endpoints);
        EstimateRowsEndpoints.Map(endpoints);
        EstimateImportEndpoints.Map(endpoints);
        MatchEndpoints.Map(endpoints);
        RateMatchListEndpoints.Map(endpoints);
        ResourceMatchEndpoints.Map(endpoints);
        ResourceMatchListEndpoints.Map(endpoints);
        RevisionSectionsEndpoints.Map(endpoints);
        RevisionMaterialsEndpoints.Map(endpoints);
        NomenclatureSearchEndpoints.Map(endpoints);
        DistributionEndpoints.Map(endpoints);
        ConjunctureEndpoints.Map(endpoints);
        OfferEndpoints.Map(endpoints);
        PriceListEndpoints.Map(endpoints);
        CalendarEndpoints.Map(endpoints);
        ScheduleEndpoints.Map(endpoints);
    }

    /// <summary>
    /// Инициализировать нечего: справочников у каркаса нет, а системные роли («Сметчик», «Менеджер
    /// проекта») заводит ядро (<c>SystemRoles</c>) — их составы названы там целиком, включая права этого
    /// модуля, и наполняются ими, как только права объявлены здесь.
    /// </summary>
    public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}
