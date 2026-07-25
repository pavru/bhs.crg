using System.Text.Json;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Сидинг встроенных профилей распознавания при старте (issue #406) — идемпотентно, тем же приёмом,
/// что сид ролей в Program.cs.
///
/// Ключевое правило: встроенный профиль обновляется по <c>Code</c> ТОЛЬКО пока пользователь его не
/// правил (<c>IsModified == false</c>). Так наши улучшения дефолтов в новых версиях доезжают до всех,
/// кто профиль не трогал, а ручная правка никогда не затирается апгрейдом. «Сбросить к заводским»
/// снимает флаг — и ближайший старт вернёт дефолт.
/// </summary>
public static class RecognitionProfileSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        var existing = await db.RecognitionProfiles.Where(p => p.Code != null).ToListAsync(ct);
        var byCode = existing.ToDictionary(p => p.Code!, StringComparer.Ordinal);
        var changed = false;

        foreach (var def in BuiltInRecognitionProfiles.All)
        {
            var fields = RecognitionProfileJson.WriteFields(def.Fields);
            var shape = RecognitionProfileJson.WriteShape(def.Shape);

            if (!byCode.TryGetValue(def.Code, out var profile))
            {
                db.RecognitionProfiles.Add(
                    RecognitionProfile.CreateBuiltIn(def.Code, def.Name, def.Kind, fields, shape));
                changed = true;
                continue;
            }

            if (profile.IsModified) continue;                 // правил пользователь — не трогаем
            if (!Differs(profile, def.Name, fields, shape)) continue;  // нечего обновлять — не дёргаем UpdatedAt

            profile.ApplySeed(def.Name, fields, shape);
            changed = true;
        }

        if (changed) await db.SaveChangesAsync(ct);
    }

    /// <summary>Сравнение по СМЫСЛУ, а не по сырому тексту: PostgreSQL хранит jsonb нормализованно
    /// (переупорядочивает ключи объектов), поэтому прочитанный из БД текст почти никогда не совпадает
    /// побайтово со свежесериализованным — наивное сравнение переписывало бы все профили на каждом
    /// старте. Прогоняем обе стороны через одну и ту же модель и сериализатор.</summary>
    private static bool Differs(RecognitionProfile profile, string name, JsonDocument fields, JsonDocument? shape)
        => profile.Name != name
        || Canonical(profile.Fields) != Canonical(fields)
        || CanonicalShape(profile.Shape) != CanonicalShape(shape);

    private static string Canonical(JsonDocument? doc)
        => JsonSerializer.Serialize(RecognitionProfileJson.ReadFields(doc), RecognitionProfileJson.Options);

    private static string CanonicalShape(JsonDocument? doc)
        => JsonSerializer.Serialize(RecognitionProfileJson.ReadShape(doc), RecognitionProfileJson.Options);
}
