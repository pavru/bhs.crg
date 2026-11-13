using BHS.CRG.Application.Common;
using BHS.CRG.Application.Recent;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Templates;

public class TemplateHandlers(
    IRepository<Template> repo,
    IRepository<TemplateAsset> templateAssetRepo,
    IDomainObjectRepository objRepo,
    IBlobStorage blobStorage,
    IDocumentTemplateInvalidator invalidator,
    IRecentRecords recent,
    ITemplateVersionWriter versionWriter,
    ITemplateNames names) :
    IRequestHandler<CreateTemplateCommand, Template>,
    IRequestHandler<UpdateTemplateCommand, TemplateMutationResult>,
    IRequestHandler<SaveTemplateContentCommand, TemplateMutationResult>,
    IRequestHandler<DuplicateTemplateCommand, Template>,
    IRequestHandler<DeleteTemplateCommand>,
    IRequestHandler<GetTemplatesUsageQuery, IReadOnlyDictionary<Guid, TemplateUsage>>,
    IRequestHandler<GetActiveTemplateQuery, Template?>,
    IRequestHandler<ListTemplatesQuery, IReadOnlyList<Template>>,
    IRequestHandler<UpdateTemplateParametersCommand, Template>,
    IRequestHandler<SetTemplateDefaultCommand, TemplateMutationResult>
{
    /// <summary>Отказ гонки за название: тот же по смыслу, что и проверка до записи.</summary>
    public const string NameTakenMessage = "Шаблон с таким названием уже есть.";

    /// <summary>Отказ гонки двух новых версий одного шаблона (#1922).</summary>
    public const string ChangedMeanwhileMessage = "Шаблон тем временем изменили — откройте его заново.";

    public async Task<Template> Handle(CreateTemplateCommand cmd, CancellationToken ct)
    {
        // Стандартные импорты (systemlib + typeblocks) вставляем ТОЛЬКО при создании шаблона (issue #353):
        // дальше шаблон компилируется дословно, поэтому импорты обязаны жить в его содержимом.
        var content = Generation.SystemTypstLib.EnsureImports(cmd.Content);
        var name = ValidName(cmd.Name);
        await EnsureNameFreeAsync(cmd.DocumentTypeId, name, ct);
        var template = Template.Create(cmd.DocumentTypeId, name, content);
        // Гонку двух созданий под одним новым названием ловит индекс (D3) — отказ тот же по смыслу.
        await versionWriter.SaveRetiredThenInsertAsync([template], NameTakenMessage, ct);
        return template;
    }

    public async Task<TemplateMutationResult> Handle(UpdateTemplateCommand cmd, CancellationToken ct)
    {
        var source = (await repo.FindAsync(t => t.Id == cmd.Id, ct)).SingleOrDefault()
            ?? throw new NotFoundException($"Template {cmd.Id} not found");
        // Все версии шаблона, а не одна названная (issue #1920): номер и единственная действующая
        // считаются по ним. Чтение без отслеживания — и исходную берём из того же списка, иначе
        // в контексте оказались бы два экземпляра одной строки.
        var versions = await repo.FindAsync(
            t => t.DocumentTypeId == source.DocumentTypeId && t.Name == source.Name, ct);
        // Версию могли удалить между двумя чтениями — это тоже «не найдена», а не 500.
        var existing = versions.SingleOrDefault(t => t.Id == cmd.Id)
            ?? throw new NotFoundException($"Template {cmd.Id} not found");
        // Гаснут действующие — именно они, а не исходная: от v2 при действующей v4 гаснет v4 (#1920).
        var retiring = versions.Where(v => v.IsActive).Select(v => v.Id).ToList();

        // Закреплённые за гаснущими версиями переходят на новую (вариант А #1923, D6): закрепление
        // значит «этот шаблон». Иначе документ оставался на погашенной, и генерация отказывала.
        var pinned = (await objRepo.GetDocumentsOfTypeAsync(source.DocumentTypeId, ct))
            .Where(o => retiring.Any(o.PinsTemplate)).ToList();
        // Документ в выпуске допишет PDF прежней версии ПОСЛЕ пересадки — и будет числиться выпущенным
        // новой. Ждать его нечем, поэтому отказ: выпуск идёт секунды, повторить можно сразу после.
        var busy = pinned.Count(o => o.Status == Domain.Documents.DocumentStatus.Generating);
        if (busy > 0)
            throw new ConflictException(
                $"За этой версией закреплено {busy} докум., которые сейчас генерируются. " +
                "Сохраните новую версию, когда генерация закончится.");

        // Меняются только версии с признаками — их и пишем: Update у неотслеживаемой строки
        // переписывает её целиком, вместе с Content каждой исторической версии.
        var touched = versions.Where(v => v.IsActive || v.IsDefault).ToList();
        var newVersion = existing.CreateNewVersion(cmd.Content, versions, cmd.Comment);
        foreach (var v in touched) repo.Update(v);

        // Выпущенные возвращаются в черновик — их PDF напечатан прежней версией. Документы прочитаны
        // с отслеживанием: пишутся только изменённые поля, без Update — тот записал бы строку целиком,
        // и реквизиты, сохранённые формой за это время, стёр бы снимок. Блобы — после коммита.
        var replacedBy = retiring.ToDictionary(id => id, _ => newVersion.Id);
        var blobs = new List<string>();
        var repinnedReset = 0;
        foreach (var doc in pinned)
        {
            doc.RepinTemplates(replacedBy);
            if (doc.Status == Domain.Documents.DocumentStatus.Draft) continue;
            blobs.AddRange(doc.ResetToDraft());
            repinnedReset++;
        }
        // Ассеты новой версии — той же транзакцией, что и сама версия: пересаженный документ может уйти
        // в генерацию сразу после коммита и не должен застать версию без своих картинок и шрифтов.
        await AddAssetCopiesAsync(existing.Id, newVersion.Id, ct);

        // Сначала погашение и пересадка, потом вставка — порядок, которого требует индекс (D4).
        // Проигравший гонку получает 409 (D5).
        await versionWriter.SaveRetiredThenInsertAsync([newVersion], ChangedMeanwhileMessage, ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        // Документы без выбранного шаблона устарели (issue #362), если их шаблон сменился:
        // новая версия стала шаблоном по умолчанию — или его у типа нет, и они берут любой
        // действующий, а действующие только что сменились (issue #1920: от исторической версии
        // гасится действующая и не по умолчанию).
        var reset = newVersion.IsDefault || !await HasDefaultActiveAsync(newVersion.DocumentTypeId, ct)
            ? await invalidator.OnDefaultChangedAsync(newVersion.DocumentTypeId, ct)
            : 0;
        return new TemplateMutationResult(newVersion, reset + repinnedReset);
    }

    /// <summary>Название, обрезанное по краям, — непустое и в пределах колонки (иначе база ответила бы 500).</summary>
    private static string ValidName(string? raw)
    {
        // Обрезается по краям, но сравнивается точно — как группирует страница (D1).
        var name = (raw ?? "").Trim();
        if (name.Length == 0) throw new InvalidRequestException("Укажите название шаблона.");
        if (name.Length > Template.NameMaxLength)
            throw new InvalidRequestException($"Название шаблона длиннее {Template.NameMaxLength} знаков.");
        return name;
    }

    /// <summary>
    /// Название шаблона в типе не повторяется (#1924): шаблон — все версии с одним типом и одним
    /// названием, и создание под занятым названием добавляло бы действующую v1 в чужой шаблон.
    /// Гонку двух созданий ловит уникальный индекс (D3), здесь — внятный отказ без гонки.
    /// </summary>
    private async Task EnsureNameFreeAsync(Guid documentTypeId, string name, CancellationToken ct)
    {
        if (await names.IsTakenAsync(documentTypeId, name, ct))
            throw new ConflictException($"Шаблон с таким названием уже есть: «{name}».");
    }

    private async Task<bool> HasDefaultActiveAsync(Guid documentTypeId, CancellationToken ct)
        => (await repo.FindAsync(t => t.DocumentTypeId == documentTypeId && t.IsDefault && t.IsActive, ct)).Count > 0;

    // Простое сохранение (issue #360, Ctrl+S): правит содержимое активной версии на месте, без
    // новой версии и без дублирования ассетов (та же версия). Бросает, если версия не активна.
    public async Task<TemplateMutationResult> Handle(SaveTemplateContentCommand cmd, CancellationToken ct)
    {
        var template = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"Template {cmd.Id} not found");
        template.UpdateContent(cmd.Content);
        repo.Update(template);
        await repo.SaveChangesAsync(ct);
        // Содержимое версии изменилось на месте → устаревают документы, запиннутые на неё
        // (и no-pin, если версия дефолтная-активная) — сброс в Draft (issue #362).
        var reset = await invalidator.OnTemplateContentChangedAsync(template.Id, ct);
        return new TemplateMutationResult(template, reset);
    }

    public async Task<Template> Handle(DuplicateTemplateCommand cmd, CancellationToken ct)
    {
        var source = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"Template {cmd.Id} not found");
        string? chosen = null;
        if (!string.IsNullOrWhiteSpace(cmd.NewName))
        {
            chosen = ValidName(cmd.NewName);
            await EnsureNameFreeAsync(source.DocumentTypeId, chosen, ct);
        }
        // Без названия — первое свободное (D2): вторая «(копия)» влилась бы в первую. Свободное могли
        // занять между подбором и записью: тогда подбираем следующее, а не отвечаем «название занято» —
        // названия человек не выбирал.
        for (var attempt = 1; ; attempt++)
        {
            var copy = source.Duplicate(chosen ?? await FreeCopyNameAsync(source, ct));
            try
            {
                await versionWriter.SaveRetiredThenInsertAsync([copy], NameTakenMessage, ct);
            }
            catch (ConflictException) when (chosen is null && attempt < 3)
            {
                continue;
            }
            // Копия — новый шаблон, закреплённых за ней нет: ассеты можно дописать после.
            if (await AddAssetCopiesAsync(source.Id, copy.Id, ct) > 0) await templateAssetRepo.SaveChangesAsync(ct);
            return copy;
        }
    }

    /// <summary>
    /// Первое свободное «… (копия)», «… (копия 2)», … — одним запросом занятых названий. Название
    /// источника укорачивается, если с приставкой не помещается в колонку.
    /// </summary>
    private async Task<string> FreeCopyNameAsync(Template source, CancellationToken ct)
    {
        // Любое «… (копия N)» начинается с этих знаков: приставка короче двадцати.
        var prefix = source.Name.Length > Template.NameMaxLength - 20
            ? source.Name[..(Template.NameMaxLength - 20)]
            : source.Name;
        var taken = await names.StartingWithAsync(source.DocumentTypeId, prefix, ct);
        for (var n = 1; ; n++)
        {
            var name = CopyName(source.Name, n);
            if (!taken.Contains(name)) return name;
        }
    }

    private static string CopyName(string source, int n)
    {
        var suffix = n == 1 ? " (копия)" : $" (копия {n})";
        var room = Template.NameMaxLength - suffix.Length;
        return (source.Length > room ? source[..room].TrimEnd() : source) + suffix;
    }

    // Индивидуальные ассеты шаблона (issue #62) дублируются ПО ССЫЛКЕ на тот же blob (без
    // повторной загрузки байт) при создании новой версии/копии — до явной замены конкретного
    // ассета на новой версии (Handle(ReplaceTemplateAssetCommand) трогает только одну строку).
    // Только добавляет в единицу работы: когда записать, решает вызывающий.
    private async Task<int> AddAssetCopiesAsync(Guid fromTemplateId, Guid toTemplateId, CancellationToken ct)
    {
        var assets = await templateAssetRepo.FindAsync(
            a => a.Scope == TemplateAssetScope.Template && a.ScopeId == fromTemplateId, ct);
        foreach (var a in assets)
        {
            var copy = TemplateAsset.Create(
                TemplateAssetScope.Template, toTemplateId, a.Kind, a.Name, a.FileName, a.MimeType, a.BlobPath, a.FontFamilyName);
            await templateAssetRepo.AddAsync(copy, ct);
        }
        return assets.Count;
    }

    public async Task Handle(DeleteTemplateCommand cmd, CancellationToken ct)
    {
        var t = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();

        // Документы, запиннутые на удаляемую версию, надо разрулить — иначе они осиротеют.
        var docs = await objRepo.GetDocumentsOfTypeAsync(t.DocumentTypeId, ct);
        var pinned = docs.Where(o => o.PinsTemplate(t.Id)).ToList();

        if (pinned.Count > 0 && !cmd.ReassignUsersToDefault)
            throw new ConflictException(
                $"Версия используется в {pinned.Count} докум. Удаление снимет привязку (документы вернутся на шаблон по умолчанию).");

        // Снимаем пин (→ резолв в дефолт) + сбрасываем PDF (контекст резолва сменится). Блобы — после коммита.
        var blobs = new List<string>();
        var touched = new HashSet<DomainObject>(pinned);
        foreach (var o in pinned)
        {
            o.UnpinTemplate(t.Id);
            blobs.AddRange(o.ResetToDraft());
        }
        // Переопределения параметров версии живут и без пина (issue #1960) — снимаем у всех документов типа.
        foreach (var o in docs)
            if (o.DropTemplateParams(t.Id)) touched.Add(o);
        foreach (var o in touched) objRepo.Update(o);
        // Отметки «Недавних» удаляемой версии — к текущей из оставшихся, а последней — снимаются (shell-09,
        // Р-5): иначе очистка версий молча убирала бы живой шаблон с Главной.
        await recent.TemplateVersionLeavingAsync(t.Id, ct);
        repo.Remove(t);
        await repo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, TemplateUsage>> Handle(GetTemplatesUsageQuery q, CancellationToken ct)
    {
        var docs = await objRepo.GetDocumentsOfTypeAsync(q.DocumentTypeId, ct);
        // Проходим версии типа один раз, накапливая пины по templateId (+ примеры имён).
        var templates = await repo.FindAsync(t => t.DocumentTypeId == q.DocumentTypeId, ct);
        var acc = new Dictionary<Guid, (int Count, List<string> Names)>();
        foreach (var t in templates)
        {
            var users = docs.Where(o => o.PinsTemplate(t.Id)).ToList();
            if (users.Count == 0) continue;
            acc[t.Id] = (users.Count, users.Take(5).Select(o => o.DisplayName ?? "(без имени)").ToList());
        }
        return acc.ToDictionary(kv => kv.Key, kv => new TemplateUsage(kv.Value.Count, kv.Value.Names));
    }

    /// <summary>Шаблон, который возьмёт документ типа без выбора (D7, [ID-3]): тот же запасной выбор,
    /// что у генерации, а не первая попавшаяся действующая строка.</summary>
    public async Task<Template?> Handle(GetActiveTemplateQuery q, CancellationToken ct)
        => Generation.TemplateChoice.Fallback(await repo.FindAsync(t => t.DocumentTypeId == q.DocumentTypeId, ct));

    /// <summary>Шаблоны типа в порядке запасного выбора (D6, [ID-3]): тогда «первый действующий» формы
    /// генерации на клиенте — тот же, что возьмёт сервер.</summary>
    public async Task<IReadOnlyList<Template>> Handle(ListTemplatesQuery q, CancellationToken ct)
        => Generation.TemplateChoice.Order(await repo.FindAsync(t => t.DocumentTypeId == q.DocumentTypeId, ct));

    public async Task<Template> Handle(UpdateTemplateParametersCommand cmd, CancellationToken ct)
    {
        var template = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"Template {cmd.Id} not found");
        template.SetParameters(cmd.Parameters);
        repo.Update(template);
        await repo.SaveChangesAsync(ct);
        return template;
    }

    public async Task<TemplateMutationResult> Handle(SetTemplateDefaultCommand cmd, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        var target = all.FirstOrDefault(t => t.Id == cmd.Id)
            ?? throw new NotFoundException($"Template {cmd.Id} not found");

        foreach (var t in all.Where(t => t.DocumentTypeId == target.DocumentTypeId && t.IsDefault))
        {
            t.SetDefault(false);
            repo.Update(t);
        }

        target.SetDefault(true);
        repo.Update(target);
        await repo.SaveChangesAsync(ct);
        // Дефолт типа сменился → no-pin документы резолвятся в новый default-active → устарели (issue #362).
        var reset = await invalidator.OnDefaultChangedAsync(target.DocumentTypeId, ct);
        return new TemplateMutationResult(target, reset);
    }
}

public class TemplateAssetHandlers(IRepository<TemplateAsset> repo) :
    IRequestHandler<ListTemplateAssetsQuery, IReadOnlyList<TemplateAsset>>,
    IRequestHandler<CreateTemplateAssetCommand, TemplateAsset>,
    IRequestHandler<ReplaceTemplateAssetCommand, TemplateAsset>,
    IRequestHandler<DeleteTemplateAssetCommand>
{
    public Task<IReadOnlyList<TemplateAsset>> Handle(ListTemplateAssetsQuery q, CancellationToken ct)
        => repo.FindAsync(a => a.Scope == q.Scope && a.ScopeId == q.ScopeId, ct);

    public async Task<TemplateAsset> Handle(CreateTemplateAssetCommand cmd, CancellationToken ct)
    {
        // Адрес проверяет то же до загрузки файла; здесь — сторож для любого другого отправителя (D2).
        if (cmd.Kind == TemplateAssetKind.Image && !TemplateAsset.IsValidImageName(cmd.Name))
            throw new InvalidRequestException($"Имя ассета-картинки — имя файла, а не путь: {TemplateAsset.ImageNameRule}.");
        var asset = TemplateAsset.Create(
            cmd.Scope, cmd.ScopeId, cmd.Kind, cmd.Name, cmd.FileName, cmd.MimeType, cmd.BlobPath, cmd.FontFamilyName);
        await repo.AddAsync(asset, ct);
        await repo.SaveChangesAsync(ct);
        return asset;
    }

    public async Task<TemplateAsset> Handle(ReplaceTemplateAssetCommand cmd, CancellationToken ct)
    {
        var asset = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"TemplateAsset {cmd.Id} not found");
        asset.Replace(cmd.FileName, cmd.MimeType, cmd.BlobPath, cmd.FontFamilyName);
        repo.Update(asset);
        await repo.SaveChangesAsync(ct);
        return asset;
    }

    // Без проверки использования (issue #62): текстовый скан Typst-кода на предмет ссылки на этот
    // ассет давал бы ложные срабатывания в обе стороны (динамическое имя не найдёт, случайное
    // совпадение подстроки — ложно заблокирует) — хуже честного "Typst сам укажет на ошибку при
    // следующей генерации" (TypstGenerator пробрасывает stderr компилятора как текст исключения).
    public async Task Handle(DeleteTemplateAssetCommand cmd, CancellationToken ct)
    {
        var asset = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        repo.Remove(asset);
        await repo.SaveChangesAsync(ct);
    }
}
