namespace BHS.CRG.Application.DataSets;

/// <summary>
/// Application-level operations for data sets, bindings and binding templates.
/// HTTP endpoints stay thin and delegate here; all parsing/mapping/preview logic lives in the impl.
/// Throws <see cref="NotFoundException"/> for missing entities and
/// <see cref="InvalidRequestException"/> for invalid input (mapped to 404 / 400 by the global handler).
/// </summary>
public interface IDataSetService
{
    // ── Files ───────────────────────────────────────────────────────────────────
    /// <summary>Наборы уровня. includeInherited — вместе с наборами родительских уровней
    /// (issue #721): комплект пользуется наборами своего раздела, стройки и системы.</summary>
    Task<IReadOnlyList<DataSetFileDto>> ListFilesAsync(
        string? scope, Guid? scopeId, bool includeInherited, CancellationToken ct);
    Task<IReadOnlyList<DataSetFileDto>> ListAvailableFilesAsync(Guid setId, CancellationToken ct);
    Task<DataSetFileDto> UploadFileAsync(UploadFileInput input, CancellationToken ct);
    /// <summary>Создать набор без файла — сырьём служат данные системы (issue #580). Идемпотентно на уровень.</summary>
    Task<DataSetFileDto> CreateSystemFileAsync(CreateSystemFileInput input, CancellationToken ct);
    /// <summary>Какие консолидации данных системы возможны на уровне — до создания набора (issue #606).</summary>
    Task<IReadOnlyList<DataSetSourceInfo>> ListSystemCandidatesAsync(string scope, Guid? scopeId, CancellationToken ct);
    Task<DataSetFileDto?> ReplaceFileAsync(Guid id, ReplaceFileInput input, CancellationToken ct);
    Task<FileDownloadDto?> DownloadFileAsync(Guid id, CancellationToken ct);
    Task<bool> DeleteFileAsync(Guid id, CancellationToken ct);

    // ── Sources ─────────────────────────────────────────────────────────────────
    Task<IReadOnlyList<DataSetSourceDto>> ListSourcesAsync(Guid fileId, CancellationToken ct);
    /// <summary>Детект кандидатов на источник в сыром файле (без персиста) — подсказки для диалога создания.</summary>
    Task<IReadOnlyList<DataSetSourceInfo>> DetectSourceCandidatesAsync(Guid fileId, CancellationToken ct);
    Task<SourcePreviewDto?> PreviewSourceAsync(Guid sourceId, int maxRows, CancellationToken ct);
    /// <summary>Выгрузка ВСЕХ строк источника (после обработки) в CSV/XLS/XLSX. format: "csv"/"xls"/"xlsx" (по умолчанию xlsx).</summary>
    Task<SourceExportDto?> ExportSourceAsync(Guid sourceId, string? format, CancellationToken ct);
    Task<Dictionary<string, string>?> AutoMapAsync(Guid sourceId, IReadOnlyList<FieldInfo> fields, CancellationToken ct);

    /// <summary>Ручное создание источника (для XML — единственный способ, авто-детект не используется).</summary>
    Task<DataSetSourceDto> CreateSourceAsync(Guid fileId, CreateSourceInput input, CancellationToken ct);
    /// <summary>Настроить/снять материализацию источника в тип (issue #19). typeId=null снимает;
    /// маппинг, правило выбора варианта (issue #716) и колонка режима «по Ид» (issue #725) задаются
    /// целиком, замещением.</summary>
    Task<DataSetSourceDto?> SetMaterializationAsync(Guid sourceId, Guid? typeId,
        Dictionary<string, string>? mapping, MaterializeDiscriminatorConfig? discriminator,
        string? byIdColumn, CancellationToken ct);
    /// <summary>Предпросмотр материализации источника (строки → объекты формы типа).
    /// <paramref name="mapping"/> задан — настройку ведёт диалог, и <paramref name="discriminator"/>
    /// с <paramref name="byIdColumn"/> авторитетны (null значит «нет», issue #294/#716/#725);
    /// mapping=null → сохранённые на источнике.</summary>
    Task<MaterializePreviewDto?> MaterializePreviewAsync(Guid sourceId, int maxRows, Guid? typeId,
        Dictionary<string, string>? mapping, MaterializeDiscriminatorConfig? discriminator,
        string? byIdColumn, CancellationToken ct);
    Task<DataSetSourceDto?> UpdateSourceAsync(Guid sourceId, UpdateSourceInput input, CancellationToken ct);
    /// <summary>Лёгкое переименование источника (issue #43) — только имя, без extraction/кэша; для любого
    /// источника, включая PDF-проекции.</summary>
    Task<DataSetSourceDto?> RenameSourceAsync(Guid sourceId, string name, CancellationToken ct);
    Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken ct);

    /// <summary>Есть ли источник, материализуемый в documentTypeId (issue #57 — проверка перед удалением типа документа).</summary>
    Task<bool> AnySourceMaterializedAsTypeAsync(Guid documentTypeId, CancellationToken ct);

    /// <summary>Копия источника (тот же locator/колонки/Filter/Transformation/Sort и материализация)
    /// на том же файле — доступно для любого формата. name пуст → ближайшее свободное имя.</summary>
    Task<DataSetSourceDto?> DuplicateSourceAsync(Guid sourceId, string? name, CancellationToken ct);

    /// <summary>
    /// Ручное создание PDF-источника (без Extraction через builder — см. RecognizePdfSourceAsync).
    /// </summary>
    /// <summary>Выбор профиля препроцессинга PDF-набора. ГОСТ (issue #38) ставит профиль на НАБОР и
    /// возвращает null (источников не создаёт — они кандидаты после распознавания); «Счёт» создаёт
    /// пару источников и возвращает шапку.</summary>
    Task<DataSetSourceDto?> CreatePdfSourceAsync(Guid fileId, CreatePdfSourceInput input, CancellationToken ct);

    /// <summary>Планирование распознавания ГОСТ-набора по fileId (409 при неподтверждённой ручной правке).</summary>
    Task<RecognizePlan?> PlanFileRecognitionAsync(Guid fileId, bool confirm, CancellationToken ct);

    /// <summary>Распознавание ГОСТ-комплекта по НАБОРУ (issue #38): пишет Grouping (сырьё), источников
    /// не создаёт. Штатно через фоновую задачу (Job.TargetId=fileId).</summary>
    Task RecognizeFileAsync(Guid fileId, bool confirm, CancellationToken ct);

    /// <summary>
    /// Распознаёт основную надпись каждой страницы PDF (по одной странице за вызов, через
    /// существующий IDocumentRecognizer) и кэширует результат на источнике (DataSetSource.CachedData).
    /// Дорогая/небыстрая операция — запускается явным действием пользователя, не при каждом
    /// preview/generation вызове. Для ГОСТ-профиля "Документы": если на источнике уже есть
    /// ручная правка группировки (GostGrouping.ManuallyEdited=true) и <paramref name="confirm"/>
    /// не передан — бросает <see cref="ConflictException"/> (эндпоинт мапит в 409), чтобы
    /// не затереть ручные правки без явного согласия пользователя.
    /// </summary>
    Task<DataSetSourceDto?> RecognizePdfSourceAsync(Guid sourceId, bool confirm, CancellationToken ct);

    /// <summary>Синхронная пред-валидация распознавания ДО постановки в фон: проверяет формат/наличие
    /// и (для GOST) конфликт ручной правки (409 при ManuallyEdited без confirm). Возвращает план —
    /// долгую ли операцию ставить в фоновую задачу (GOST-набор) или выполнить синхронно (счёт/legacy).
    /// null — источник не найден.</summary>
    Task<RecognizePlan?> PlanRecognitionAsync(Guid sourceId, bool confirm, CancellationToken ct);

    /// <summary>
    /// Текущая группировка страниц источника «Документы» ГОСТ-профиля — для ручного редактора
    /// разбиения (миниатюры + перенос страниц между документами). Null, если источник не найден
    /// или не относится к ГОСТ-профилю "Документы".
    /// </summary>
    Task<GostGroupingDto?> GetPagesAsync(Guid fileId, CancellationToken ct);

    /// <summary>Миниатюра одной страницы исходного PDF (PNG, низкое DPI — только для узнавания
    /// документа глазами, не OCR) — рендер на лету через PdfRasterizer, без LLM.</summary>
    Task<byte[]?> GetPageThumbnailAsync(Guid fileId, int pageIndex, CancellationToken ct, int dpi = 96);

    /// <summary>
    /// Применяет ручную корректировку разбиения — заменяет группировку целиком, физически
    /// разрезает PDF заново по новым группам, обновляет реестр (CachedData) и помечает
    /// GostGrouping.ManuallyEdited=true. Осиротевшие blob'ы прежних под-PDF удаляются best-effort.
    /// </summary>
    Task<GostGroupingDto?> ApplyGroupingAsync(Guid fileId, ApplyGroupingInput input, CancellationToken ct);
    /// <summary>Лёгкая установка тэгов документа (тип таблицы) без пересборки разбиения.</summary>
    Task<GostGroupingDto?> SetDocumentTagsAsync(Guid fileId, int firstPageIndex, IReadOnlyList<string> tags, CancellationToken ct);

    /// <summary>Привязать профиль распознавания к группе листов (issue #410); null — снять привязку.</summary>
    Task<GostGroupingDto?> SetDocumentProfileAsync(Guid fileId, int firstPageIndex, Guid? profileId, CancellationToken ct);

    /// <summary>Привязать профили распознавания к НАБОРУ (issue #412): {вид: id профиля}, null снимает.</summary>
    Task<bool> SetFileRecognitionProfilesAsync(Guid fileId, IReadOnlyDictionary<string, Guid?> map, CancellationToken ct);
    /// <summary>Распознать таблицу помеченного документа ГОСТ-профиля (спецификация/кабельный журнал):
    /// пишет строки как СЫРЬЁ на группу (Grouping) — доступна как кандидат «Таблица …», источник создаёт
    /// пользователь (issue #42). Источника НЕ создаёт. firstPageIndex — любая страница документа.</summary>
    Task<GostGroupingDto?> RecognizeDocumentTableAsync(Guid fileId, int firstPageIndex, CancellationToken ct);

    /// <summary>Пути XML-записей внутри ZIP-файла — для выбора при ручном создании источника.</summary>
    Task<IReadOnlyList<string>> ListZipXmlEntriesAsync(Guid fileId, CancellationToken ct);

    /// <summary>Предпросмотр XPath/JSONPath-выражения в builder'е — без сохранения источника.</summary>
    Task<ExpressionPreviewDto> PreviewExpressionAsync(Guid fileId, string rowSelector, string? expr, CancellationToken ct);

    /// <summary>Обработка (Filter/Transformation/Sort) источника — лёгкая правка, файл не трогает.</summary>
    Task<DataSetSourceDto?> SetSourceProcessingAsync(Guid sourceId, SetSourceProcessingInput input, CancellationToken ct);

    /// <summary>
    /// Применить шаблон (Extraction, если задан в шаблоне, + Filter/Transformation/Sort) к
    /// источнику — copy-on-apply, единожды. Extraction в шаблоне триггерит пере-парсинг файла
    /// (как Update/CreateSourceInput), в отличие от SetSourceProcessingAsync.
    /// </summary>
    Task<DataSetSourceDto?> ApplyProcessingTemplateAsync(Guid sourceId, Guid templateId, CancellationToken ct);

    // ── Processing templates (переиспользуемые рецепты Extraction + Filter/Transformation/Sort) ────
    Task<IReadOnlyList<DataSetProcessingTemplateDto>> ListProcessingTemplatesAsync(CancellationToken ct);
    Task<DataSetProcessingTemplateDto> CreateProcessingTemplateAsync(CreateProcessingTemplateInput input, CancellationToken ct);
    Task<DataSetProcessingTemplateDto?> UpdateProcessingTemplateAsync(Guid id, UpdateProcessingTemplateInput input, CancellationToken ct);
    Task<bool> DeleteProcessingTemplateAsync(Guid id, CancellationToken ct);

    // ── Bindings (владелец — единый DomainObject.OwnerId, issue #84) ─────────────────
    Task<IReadOnlyList<DataSetBindingDto>> ListBindingsAsync(Guid ownerId, CancellationToken ct);

    /// <summary>
    /// Привязки СРАЗУ МНОГИХ владельцев — для аудита типа (issue #737), который обходит все его
    /// инстансы. Поштучный вызов дал бы запрос на объект: у типа с сотней документов это сотня
    /// обращений ради проверки, которую база делает одним IN.
    /// </summary>
    Task<IReadOnlyList<DataSetBindingDto>> ListBindingsForOwnersAsync(
        IReadOnlyCollection<Guid> ownerIds, CancellationToken ct);

    /// <summary>
    /// Переносит ключ поля <paramref name="oldKey"/> → <paramref name="newKey"/> у привязок
    /// перечисленных владельцев и у шаблонов привязок перечисленных типов (issue #737).
    ///
    /// <para>Спутник миграции данных при переименовании поля (issue #357): та переносит значения в
    /// реквизитах, а ключ живёт и здесь. Не перенеси — привязка осиротеет и перестанет заполнять
    /// поле, причём молча: пользователь переименовал поле и увидел пустоту там, где были данные.</para>
    ///
    /// <para>Затрагивает и <c>TargetFieldKey</c>, и ключи маппинга (у скалярной привязки целевые
    /// поля перечислены только в нём).</para>
    /// </summary>
    Task<BindingKeyMigrationResult> MigrateFieldKeyAsync(
        IReadOnlyCollection<Guid> ownerIds, IReadOnlyCollection<Guid> documentTypeIds,
        string oldKey, string newKey, CancellationToken ct);
    Task<DataSetBindingDto?> CreateBindingAsync(CreateBindingInput input, CancellationToken ct);
    Task<DataSetBindingDto?> UpdateBindingAsync(Guid id, UpdateBindingInput input, CancellationToken ct);
    Task<bool> DeleteBindingAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<BindingPreviewDto>> PreviewBindingsAsync(Guid ownerId, CancellationToken ct);

    // ── Binding templates ─────────────────────────────────────────────────────────
    Task<IReadOnlyList<DataSetBindingTemplateDto>> ListTemplatesAsync(Guid docTypeId, CancellationToken ct);
    Task<DataSetBindingTemplateDto> CreateTemplateAsync(Guid docTypeId, CreateTemplateInput input, CancellationToken ct);
    Task<DataSetBindingTemplateDto?> UpdateTemplateAsync(Guid docTypeId, Guid id, UpdateTemplateInput input, CancellationToken ct);
    Task<bool> DeleteTemplateAsync(Guid docTypeId, Guid id, CancellationToken ct);
}
