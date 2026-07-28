using BHS.CRG.Application.Common;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Api.Endpoints.Templates;

public static class TypstUserLibEndpoints
{
    public static void MapTypstUserLibEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/typst-userlib").RequireAuthorization("Admin");

        g.MapGet("/", async (IUserLibProvider provider, CancellationToken ct) =>
        {
            var snapshot = await provider.GetAsync(ct);
            // Замечания отдаём и при чтении: иначе дубликаты имён — а Typst молча берёт объявление
            // из файла, импортированного последним, — были бы видны только сразу после сохранения и
            // исчезали при перезагрузке. Разбор дерева чистый, Typst не запускает, чтение не дорожает.
            return Results.Ok(new
            {
                content = snapshot.Entrypoint,
                files = snapshot.Files.Select(f => new { path = f.Path, content = f.Content }),
                warnings = UserLibAnalysis.Warnings(snapshot.Entrypoint, snapshot.Files)
                    .Select(w => new { path = w.Path, message = w.Message }),
            });
        });

        // Сохранение — ВСЕГО дерева разом (issue #473). Пофайловое сохранение позволило бы
        // зафиксировать половину рефакторинга: правка «util/text.typ» и правка зовущего её
        // «gost/f3.typ» обязаны лечь вместе, иначе между двумя запросами библиотека не собирается —
        // а её импортирует каждый шаблон.
        //
        // `files: null` означает «дерево не трогать» и оставлено осознанно: так продолжает работать
        // клиент, который шлёт только точку входа.
        g.MapPut("/", async (SaveTypstUserLibRequest req, IRepository<TypstUserLib> libRepo,
            IRepository<TypstUserLibFile> fileRepo, IUserLibProvider provider,
            IUserLibChecker checker, CancellationToken ct) =>
        {
            if (req.Files is { } incoming)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var normalized = new List<UserLibFile>(incoming.Count);
                foreach (var f in incoming)
                {
                    if (!UserLibPath.TryNormalize(f.Path, out var path, out var error))
                        return Results.BadRequest(new { error = $"«{f.Path}»: {error}" });
                    if (!seen.Add(path))
                        return Results.BadRequest(new { error = $"Путь «{path}» встречается дважды." });
                    normalized.Add(new UserLibFile(path, f.Content ?? string.Empty));
                }

                // Пути, различающиеся только регистром, на Linux — разные файлы, на Windows — один.
                // Разойтись это может уже в продакшене, поэтому отказываем сразу.
                var caseClash = normalized.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(x => x.Count() > 1);
                if (caseClash is not null)
                    return Results.BadRequest(new
                    {
                        error = $"Пути различаются только регистром: {string.Join(", ", caseClash.Select(f => f.Path))}.",
                    });

                await ApplyTreeAsync(fileRepo, normalized, ct);
            }

            var lib = (await libRepo.GetAllAsync(ct)).FirstOrDefault();
            if (lib is null)
            {
                lib = TypstUserLib.Create(req.Content);
                await libRepo.AddAsync(lib, ct);
            }
            else
            {
                lib.UpdateContent(req.Content);
                libRepo.Update(lib);
            }
            await libRepo.SaveChangesAsync(ct);

            // Проверка не блокирует сохранение: инвариант «сохранение = черновик», и на середине
            // рефакторинга работу надо дать отложить. Результат возвращаем, чтобы состояние
            // «библиотека не собирается» было видно сразу, а не при следующей генерации.
            var snapshot = await provider.GetAsync(ct);
            UserLibCheckResult check;
            try
            {
                check = await checker.CheckAsync(snapshot.Entrypoint, snapshot.Files, ct);
            }
            catch (Exception ex)
            {
                // Недоступный Typst CLI — не повод потерять сохранение; честно говорим, что не проверили.
                check = new UserLibCheckResult(
                    [new UserLibError(UserLibAnalysis.EntrypointName, 0, 0,
                        $"Проверить библиотеку не удалось: {ex.Message}")],
                    UserLibAnalysis.Warnings(snapshot.Entrypoint, snapshot.Files));
            }

            return Results.Ok(new
            {
                content = snapshot.Entrypoint,
                files = snapshot.Files.Select(f => new { path = f.Path, content = f.Content }),
                check = new
                {
                    ok = check.Ok,
                    errors = check.Errors.Select(e => new
                    {
                        path = e.Path, line = e.Line, column = e.Column, message = e.Message, inBuild = e.InBuild,
                    }),
                    warnings = check.Warnings.Select(w => new { path = w.Path, message = w.Message }),
                },
            });
        });
    }

    /// <summary>
    /// Приведение дерева в БД к присланному состоянию. Существующие файлы обновляются на месте, а не
    /// удаляются и создаются заново — иначе на каждом сохранении терялись бы <c>CreatedAt</c> и
    /// идентификаторы, по которым файл узнаётся между сохранениями.
    /// </summary>
    private static async Task ApplyTreeAsync(
        IRepository<TypstUserLibFile> fileRepo, IReadOnlyList<UserLibFile> desired, CancellationToken ct)
    {
        var existing = (await fileRepo.GetAllAsync(ct)).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var desiredPaths = desired.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var file in desired)
        {
            if (existing.TryGetValue(file.Path, out var row))
            {
                if (!string.Equals(row.Content, file.Content, StringComparison.Ordinal))
                {
                    row.Update(file.Content);
                    fileRepo.Update(row);
                }
            }
            else
            {
                await fileRepo.AddAsync(TypstUserLibFile.Create(file.Path, file.Content), ct);
            }
        }

        foreach (var (path, row) in existing)
            if (!desiredPaths.Contains(path))
                fileRepo.Remove(row);

        await fileRepo.SaveChangesAsync(ct);
    }
}

record UserLibFileRequest(string Path, string? Content);

record SaveTypstUserLibRequest(string Content, IReadOnlyList<UserLibFileRequest>? Files = null);
