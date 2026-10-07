using BHS.CRG.Application.Recognition;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Сидинг встроенных профилей распознавания при старте (issue #406) — идемпотентно, тем же приёмом,
/// что сид ролей в Program.cs. Намеренно СТАРТОВЫЙ сидер, а не данные внутри EF-миграции: данные в
/// миграции становятся замороженной историей, и каждое улучшение дефолта требовало бы новой миграции.
///
/// Правила:
/// 1. Встроенный профиль обновляется по <c>Code</c>, пока пользователь его не правил — так улучшения
///    дефолтов в новых версиях доезжают до всех, кто профиль не трогал.
/// 2. Если пользователь правил (<c>IsModified</c>), правка НИКОГДА не затирается апгрейдом. Но и молча
///    отставать профиль не должен: при расхождении с заводским хешем выставляется
///    <c>BuiltInOutdated</c> — повод показать «заводской профиль обновился / сбросить к заводским».
///    Без этого правка одного описания молча замораживала бы профиль целиком.
/// 3. «Сбросить к заводским» снимает <c>IsModified</c> — и ближайший старт вернёт дефолт.
/// 4. Сидятся объявления ВСЕХ владельцев сборки, включая выключенные модули (issue #1075): иначе
///    включение модуля означало бы появление его профилей только после перезапуска, а выключение —
///    строки, о которых никто не знает, чьи они. На установке без модуля его профили лежат в базе
///    и не предлагаются.
/// 5. Владелец проставляется из объявления; у строки без владельца (копия, снятая до появления
///    колонки) — по виду.
/// </summary>
public static class RecognitionProfileSeeder
{
    public static async Task SeedAsync(
        AppDbContext db, RecognitionProfileCatalog catalog, CancellationToken ct = default)
    {
        var existing = await db.RecognitionProfiles.ToListAsync(ct);
        var byCode = existing.Where(p => p.Code != null).ToDictionary(p => p.Code!, StringComparer.Ordinal);
        var changed = false;

        foreach (var def in catalog.All)
        {
            var hash = def.Hash;

            if (!byCode.TryGetValue(def.Code, out var profile))
            {
                db.RecognitionProfiles.Add(Domain.Recognition.RecognitionProfile.CreateBuiltIn(
                    def.Code, def.Name, def.Kind, def.Owner,
                    RecognitionProfileJson.WriteFields(def.Fields),
                    RecognitionProfileJson.WriteFieldsOrNull(def.RowColumns),
                    RecognitionProfileJson.WriteShape(def.Shape),
                    hash));
                changed = true;
                continue;
            }

            // Владелец — из объявления, и у правленого профиля тоже: правка содержимого не делает
            // профиль чужим.
            if (profile.Module != def.Owner)
            {
                profile.AssignModule(def.Owner);
                changed = true;
            }

            if (profile.IsModified)
            {
                // Правку не трогаем, но если заводской ушёл вперёд — отмечаем, чтобы это было видно.
                if (profile.BuiltInHash != hash && !profile.BuiltInOutdated)
                {
                    profile.MarkBuiltInOutdated();
                    changed = true;
                }
                continue;
            }

            // Сравниваем ТЕКУЩЕЕ содержимое строки с заводским (а не сохранённый хеш с заводским —
            // тот описывает заводскую версию и после «сбросить к заводским» отличий бы не показал).
            if (RecognitionProfileCatalog.HashOfCurrent(profile) == hash && profile.BuiltInHash == hash)
                continue;   // совпадает с заводским — не дёргаем UpdatedAt

            profile.ApplySeed(def.Name,
                RecognitionProfileJson.WriteFields(def.Fields),
                RecognitionProfileJson.WriteFieldsOrNull(def.RowColumns),
                RecognitionProfileJson.WriteShape(def.Shape),
                hash);
            changed = true;
        }

        // Строки без владельца — свои профили из копии, снятой до появления колонки. Вид не объявлен
        // никем — оставляем пустым: такой профиль недоступен, и это честнее выдуманного владельца.
        foreach (var orphan in existing.Where(p => p.Module.Length == 0))
        {
            if (catalog.OwnerOfKind(orphan.Kind) is not { } owner) continue;
            orphan.AssignModule(owner.Code);
            changed = true;
        }

        if (changed) await db.SaveChangesAsync(ct);
    }
}
