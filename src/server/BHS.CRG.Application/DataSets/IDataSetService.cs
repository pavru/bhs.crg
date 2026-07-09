namespace BHS.CRG.Application.DataSets;

/// <summary>
/// Application-level operations for data sets, bindings and binding templates.
/// HTTP endpoints stay thin and delegate here; all parsing/mapping/preview logic lives in the impl.
/// Throws <see cref="KeyNotFoundException"/> for missing entities and
/// <see cref="ArgumentException"/> for invalid input (mapped to 404 / 400 by the global handler).
/// </summary>
public interface IDataSetService
{
    // ── Files ───────────────────────────────────────────────────────────────────
    Task<IReadOnlyList<DataSetFileDto>> ListFilesAsync(string? scope, Guid? scopeId, CancellationToken ct);
    Task<IReadOnlyList<DataSetFileDto>> ListAvailableFilesAsync(Guid setId, CancellationToken ct);
    Task<DataSetFileDto> UploadFileAsync(UploadFileInput input, CancellationToken ct);
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
    /// <summary>Настроить/снять материализацию источника в тип (issue #19). typeId=null снимает.</summary>
    Task<DataSetSourceDto?> SetMaterializationAsync(Guid sourceId, Guid? typeId, Dictionary<string, string>? mapping, CancellationToken ct);
    /// <summary>Предпросмотр материализации источника (строки → объекты формы типа).</summary>
    Task<MaterializePreviewDto?> MaterializePreviewAsync(Guid sourceId, int maxRows, CancellationToken ct);
    Task<DataSetSourceDto?> UpdateSourceAsync(Guid sourceId, UpdateSourceInput input, CancellationToken ct);
    Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken ct);

    /// <summary>Копия источника (тот же locator/колонки/Filter/Transformation/Sort) на том же файле — доступно для любого формата.</summary>
    Task<DataSetSourceDto?> DuplicateSourceAsync(Guid sourceId, CancellationToken ct);

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
    /// не передан — бросает <see cref="InvalidOperationException"/> (эндпоинт мапит в 409), чтобы
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

    // ── Bindings (владелец — ровно одно из instanceId/commonDataEntryId) ─────────────
    Task<IReadOnlyList<DataSetBindingDto>> ListBindingsAsync(Guid? instanceId, Guid? commonDataEntryId, CancellationToken ct);
    Task<DataSetBindingDto?> CreateBindingAsync(CreateBindingInput input, CancellationToken ct);
    Task<DataSetBindingDto?> UpdateBindingAsync(Guid id, UpdateBindingInput input, CancellationToken ct);
    Task<bool> DeleteBindingAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<BindingPreviewDto>> PreviewBindingsAsync(Guid? instanceId, Guid? commonDataEntryId, CancellationToken ct);

    // ── Binding templates ─────────────────────────────────────────────────────────
    Task<IReadOnlyList<DataSetBindingTemplateDto>> ListTemplatesAsync(Guid docTypeId, CancellationToken ct);
    Task<DataSetBindingTemplateDto> CreateTemplateAsync(Guid docTypeId, CreateTemplateInput input, CancellationToken ct);
    Task<DataSetBindingTemplateDto?> UpdateTemplateAsync(Guid docTypeId, Guid id, UpdateTemplateInput input, CancellationToken ct);
    Task<bool> DeleteTemplateAsync(Guid docTypeId, Guid id, CancellationToken ct);
}
