using BHS.CRG.Api.Modules;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Recognition;

namespace BHS.CRG.Tests.Support;

/// <summary>
/// Каталог профилей распознавания для тестов, которые собирают службы руками, мимо контейнера
/// (issue #1075): сидер и поставщик профилей теперь требуют каталог, а не читают статический список.
///
/// <para>Собирается тем же кодом, что в приложении (<see cref="ModuleRecognitionCollector" />), из
/// настоящего модуля исполнительной документации — подделка каталога проверяла бы подделку.</para>
/// </summary>
internal static class TestRecognition
{
    /// <summary>Модуль ИД включён — состав действующих установок.</summary>
    internal static RecognitionProfileCatalog Catalog { get; } =
        ModuleRecognitionCollector.Build(new ModuleRegistry([new IdModule()], []));

    /// <summary>Модуль ИД в сборке есть, но выключен.</summary>
    internal static RecognitionProfileCatalog WithoutId { get; } =
        ModuleRecognitionCollector.Build(new ModuleRegistry([], [new IdModule()]));

    /// <summary>Владелец вида — чтобы тест, заводящий свой профиль, не вписывал код модуля руками.</summary>
    internal static string OwnerOf(RecognitionProfileKind kind) => Catalog.Require(kind).Owner;

    // Перечни модуля в той форме, которую принимают построители запросов к модели.
    internal static IReadOnlyList<RecognitionField> TitleBlock => Map(IdRecognitionProfiles.TitleBlockFields);
    internal static IReadOnlyList<RecognitionField> CoverTitle => Map(IdRecognitionProfiles.CoverTitleFields);
    internal static IReadOnlyList<RecognitionField> Specification => Map(IdRecognitionProfiles.SpecificationColumns);
    internal static IReadOnlyList<RecognitionField> CableJournal => Map(IdRecognitionProfiles.CableJournalColumns);

    private static IReadOnlyList<RecognitionField> Map(IReadOnlyList<ModuleRecognitionField> fields) =>
        [.. fields.Select(f => new RecognitionField(f.Name, f.Description, f.Type, f.Options))];
}
