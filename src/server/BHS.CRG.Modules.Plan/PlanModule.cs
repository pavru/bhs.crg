using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Plan.Import;
using BHS.CRG.Modules.Plan.Matching;
using BHS.CRG.Modules.Ports;
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
    public const string EstimateRead = "plan.estimate.read";

    /// <summary>Правка смет — одним местом, как <see cref="EstimateRead" />.</summary>
    public const string EstimateEdit = "plan.estimate.edit";

    /// <summary>Себестоимость сметы — одним местом: её спрашивают ответы смет и строк (ТЗ PLAN-11).</summary>
    public const string EstimateCost = "plan.estimate.cost";

    public string Code => ModuleCode;

    public string Title => "Планирование";

    /// <summary>
    /// Права модуля (ТЗ PLAN-23) — каждое с объяснением: галку в редакторе ролей ставит человек.
    ///
    /// <para>⚠️ Здесь три права из двенадцати по перечню ТЗ, и это правило этапа, а не недоделка
    /// (D12 зонтика <c>plan-00-epic</c>): право объявляется вместе с первым адресом, который оно
    /// открывает. Объявленное раньше, оно было бы галкой, которая не делает ничего, — а составы
    /// системных ролей наполнились бы им сразу. Остальные приходят своими change: <c>plan.offer.*</c> —
    /// 13, <c>plan.pricelist.edit</c> — 14, календарь и графики — 18, права контракта
    /// (<c>plan.estimate.materials</c>, <c>plan.labor.read</c>, <c>plan.estimate.hint</c>) — 23.</para>
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
            "видеть себестоимость сметы: стоимость строк, итоги, расценки",
            "стоимость единицы и итог каждой строки, итоги редакций, цены и стоимость материалов и всё " +
            "сметное наполнение строк (расценка, коэффициенты, поля заказчика); без права сметы читаются " +
            "без денег",
            [EstimateRead],
            ReadAllMark.In),
    ];

    /// <summary>
    /// Своя схема базы (ТЗ CORE-4, D1 зонтика). Ядро мигрирует её при старте — сразу после своей и только
    /// если модуль включён. Имя названо и здесь, и в контексте: <c>dotnet ef</c> создаёт контекст вне
    /// приложения, где реестра модулей нет, — ядро сверяет два объявления.
    /// </summary>
    public ModuleSchema? Schema => new(PlanDbContext.SchemaName, typeof(PlanDbContext));

    /// <summary>Тип сметного наполнения строки — <see cref="PlanRecordTypes" /> (ТЗ TYPE-16).</summary>
    public IReadOnlyList<ModuleRecordType> RecordTypes => PlanRecordTypes.All;

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
    /// </summary>
    public IReadOnlyList<string> UsesSections => [CoreSections.Constructions, CoreSections.Catalog];

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
        services.AddScoped<EstimateRevisionWriter>();
        services.AddScoped<IModuleJobHandler, EstimateImportJob>();
        // Подстановка запомненного при импорте (plan-08) — в точку расширения, оставленную plan-07.
        services.TryAddScoped<IEstimateRowMatcher, RateRowMatcher>();
        services.AddScoped<RowMatching>();
        services.AddScoped<EstimateImportCommit>();
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
    }

    /// <summary>
    /// Инициализировать нечего: справочников у каркаса нет, а системные роли («Сметчик», «Менеджер
    /// проекта») заводит ядро (<c>SystemRoles</c>) — их составы названы там целиком, включая права этого
    /// модуля, и наполняются ими, как только права объявлены здесь.
    /// </summary>
    public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}
