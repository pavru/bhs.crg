using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Recognition;
using MediatR;

namespace BHS.CRG.Application.Recognition;

/// <summary>
/// CRUD профилей распознавания (issue #408). Правила, которые здесь защищаются:
/// — вид не меняется после создания (он выбирает применяемый промпт);
/// — системные поля вида нельзя удалить или переименовать (иначе молча ломается разбиение альбома);
/// — встроенный профиль нельзя удалить — только «сбросить к заводским» (сидер всё равно воссоздаст).
/// </summary>
public class RecognitionProfileHandlers(
    IRepository<RecognitionProfile> repo,
    IRecognitionProfileProvider provider) :
    IRequestHandler<ListRecognitionProfilesQuery, IReadOnlyList<RecognitionProfileDto>>,
    IRequestHandler<ListRecognitionKindsQuery, IReadOnlyList<RecognitionKindInfo>>,
    IRequestHandler<CreateRecognitionProfileCommand, RecognitionProfileDto>,
    IRequestHandler<UpdateRecognitionProfileCommand, RecognitionProfileDto>,
    IRequestHandler<ResetRecognitionProfileCommand, RecognitionProfileDto>,
    IRequestHandler<DeleteRecognitionProfileCommand>
{
    public async Task<IReadOnlyList<RecognitionProfileDto>> Handle(ListRecognitionProfilesQuery _, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        return [.. all.OrderByDescending(p => p.IsBuiltIn).ThenBy(p => p.Name).Select(ToDto)];
    }

    public Task<IReadOnlyList<RecognitionKindInfo>> Handle(ListRecognitionKindsQuery _, CancellationToken ct)
        => Task.FromResult(provider.ListKinds());

    public async Task<RecognitionProfileDto> Handle(CreateRecognitionProfileCommand cmd, CancellationToken ct)
    {
        if (!Enum.TryParse<RecognitionProfileKind>(cmd.Kind, out var kind))
            throw new ArgumentException($"Неизвестный вид профиля «{cmd.Kind}».");
        var info = provider.DescribeKind(kind);
        Validate(cmd.Name, cmd.Fields, cmd.RowColumns, info, isBuiltIn: false);

        var profile = RecognitionProfile.Create(
            cmd.Name, kind,
            RecognitionProfileJson.WriteFields(cmd.Fields),
            RecognitionProfileJson.WriteFieldsOrNull(cmd.RowColumns),
            RecognitionProfileJson.WriteShape(info.SupportsShape ? cmd.Shape : null));
        await repo.AddAsync(profile, ct);
        await repo.SaveChangesAsync(ct);
        return ToDto(profile);
    }

    public async Task<RecognitionProfileDto> Handle(UpdateRecognitionProfileCommand cmd, CancellationToken ct)
    {
        var profile = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"RecognitionProfile {cmd.Id} not found");
        var info = provider.DescribeKind(profile.Kind);
        Validate(cmd.Name, cmd.Fields, cmd.RowColumns, info, profile.IsBuiltIn);

        profile.Update(cmd.Name,
            RecognitionProfileJson.WriteFields(cmd.Fields),
            RecognitionProfileJson.WriteFieldsOrNull(cmd.RowColumns),
            RecognitionProfileJson.WriteShape(info.SupportsShape ? cmd.Shape : null));
        repo.Update(profile);
        await repo.SaveChangesAsync(ct);
        return ToDto(profile);
    }

    public async Task<RecognitionProfileDto> Handle(ResetRecognitionProfileCommand cmd, CancellationToken ct)
    {
        var profile = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"RecognitionProfile {cmd.Id} not found");
        if (!profile.IsBuiltIn)
            throw new InvalidOperationException("Сбросить к заводским можно только встроенный профиль.");

        profile.ResetToBuiltIn();
        repo.Update(profile);
        await repo.SaveChangesAsync(ct);

        // Заводское содержимое возвращает сидер — сразу, чтобы результат был виден без перезапуска.
        await provider.ReseedBuiltInAsync(ct);

        var restored = await repo.GetByIdAsync(cmd.Id, ct) ?? profile;
        return ToDto(restored);
    }

    public async Task Handle(DeleteRecognitionProfileCommand cmd, CancellationToken ct)
    {
        var profile = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"RecognitionProfile {cmd.Id} not found");
        if (profile.IsBuiltIn)
            throw new InvalidOperationException(
                "Встроенный профиль удалить нельзя — он будет создан заново при следующем запуске. " +
                "Используйте «Сбросить к заводским».");
        repo.Remove(profile);
        await repo.SaveChangesAsync(ct);
    }

    private static void Validate(
        string name,
        IReadOnlyList<RecognitionProfileField> fields,
        IReadOnlyList<RecognitionProfileField> rowColumns,
        RecognitionKindInfo info,
        bool isBuiltIn)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Укажите название профиля.");
        if (fields.Count == 0 && rowColumns.Count == 0)
            throw new ArgumentException("Задайте хотя бы одно поле или колонку.");

        EnsureNamesValid(fields, "поля");
        EnsureNamesValid(rowColumns, "колонки");

        // Несущие поля вида: их удаление/переименование молча ломает разбиение альбома, поэтому
        // правка встроенного профиля обязана их сохранить (описание менять можно).
        if (isBuiltIn && info.SystemFieldNames.Count > 0)
        {
            var present = fields.Select(f => f.Name.Trim()).ToHashSet();
            var missing = info.SystemFieldNames.Where(n => !present.Contains(n)).ToList();
            if (missing.Count > 0)
                throw new ArgumentException(
                    $"Нельзя удалить или переименовать обязательные поля: {string.Join(", ", missing)}. " +
                    "На них завязано разбиение документов — можно менять только описание.");
        }
    }

    private static void EnsureNamesValid(IReadOnlyList<RecognitionProfileField> fields, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            var n = f.Name.Trim();
            if (string.IsNullOrEmpty(n))
                throw new ArgumentException($"У {what} есть запись без имени.");
            if (!seen.Add(n))
                throw new ArgumentException($"Имя «{n}» повторяется — имена должны быть уникальны.");
        }
    }

    private RecognitionProfileDto ToDto(RecognitionProfile p) => new(
        p.Id, p.Name, p.Code, p.Kind.ToString(),
        RecognitionProfileJson.ReadFields(p.Fields),
        RecognitionProfileJson.ReadFields(p.RowColumns),
        RecognitionProfileJson.ReadShape(p.Shape),
        p.IsBuiltIn, p.IsModified, p.BuiltInOutdated,
        provider.DescribeKind(p.Kind));
}
