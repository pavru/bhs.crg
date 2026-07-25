using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сидинг встроенных профилей распознавания (issue #406). Главное поведение под защитой — компромисс,
/// на котором держится решение хранить дефолты в БД: апгрейд обновляет встроенные профили, но НИКОГДА
/// не затирает те, что правил пользователь.
/// </summary>
[Collection("Integration")]
public class RecognitionProfileSeederTests(IntegrationTestFixture fixture)
{
    private static AppDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    [Fact]
    public async Task Seed_CreatesAllBuiltInProfiles_AndIsIdempotent()
    {
        using var scope = fixture.Services.CreateScope();
        var db = Db(scope);

        await RecognitionProfileSeeder.SeedAsync(db);
        var codes = await db.RecognitionProfiles.Where(p => p.Code != null).Select(p => p.Code!).ToListAsync();
        foreach (var def in BuiltInRecognitionProfiles.All)
            Assert.Contains(def.Code, codes);

        // Повторный прогон ничего не дублирует и не трогает UpdatedAt (нечего обновлять).
        var before = await db.RecognitionProfiles.AsNoTracking()
            .Where(p => p.Code != null).Select(p => new { p.Id, p.UpdatedAt }).ToListAsync();
        db.ChangeTracker.Clear();
        await RecognitionProfileSeeder.SeedAsync(db);
        var after = await db.RecognitionProfiles.AsNoTracking()
            .Where(p => p.Code != null).Select(p => new { p.Id, p.UpdatedAt }).ToListAsync();

        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.OrderBy(x => x.Id).Select(x => x.UpdatedAt),
                     after.OrderBy(x => x.Id).Select(x => x.UpdatedAt));
    }

    [Fact]
    public async Task Seed_DoesNotOverwriteUserEditedProfile_UntilReset()
    {
        using var scope = fixture.Services.CreateScope();
        var db = Db(scope);
        await RecognitionProfileSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        var profile = await db.RecognitionProfiles.FirstAsync(p => p.Code == BuiltInProfileCodes.SpecificationTable);
        var original = RecognitionProfileJson.ReadFields(profile.RowColumns).Count;

        // Пользователь добавил свою колонку (у табличного профиля колонки живут в RowColumns).
        var edited = RecognitionProfileJson.ReadFields(profile.RowColumns).ToList();
        edited.Add(new RecognitionProfileField("МойСтолбец", "Добавлено пользователем", "string"));
        profile.Update(profile.Name, profile.Fields, RecognitionProfileJson.WriteFields(edited), profile.Shape);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Апгрейд (повторный сидинг) правку сохраняет.
        await RecognitionProfileSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();
        var afterSeed = await db.RecognitionProfiles.AsNoTracking()
            .FirstAsync(p => p.Code == BuiltInProfileCodes.SpecificationTable);
        Assert.True(afterSeed.IsModified);
        Assert.Contains(RecognitionProfileJson.ReadFields(afterSeed.RowColumns), f => f.Name == "МойСтолбец");

        // «Сбросить к заводским» → ближайший сидинг возвращает дефолт.
        var toReset = await db.RecognitionProfiles.FirstAsync(p => p.Code == BuiltInProfileCodes.SpecificationTable);
        toReset.ResetToBuiltIn();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RecognitionProfileSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();
        var restored = await db.RecognitionProfiles.AsNoTracking()
            .FirstAsync(p => p.Code == BuiltInProfileCodes.SpecificationTable);
        Assert.False(restored.IsModified);
        Assert.DoesNotContain(RecognitionProfileJson.ReadFields(restored.RowColumns), f => f.Name == "МойСтолбец");
        Assert.Equal(original, RecognitionProfileJson.ReadFields(restored.RowColumns).Count);
    }

    [Fact]
    public async Task Seed_MarksBuiltInOutdated_WhenFactoryMovedOnUnderUserEdit()
    {
        // Без этой отметки IsModified замораживал бы профиль ЦЕЛИКОМ и молча: правка одного описания
        // отключала бы будущие улучшения всех остальных полей, и пользователь бы об этом не узнал.
        using var scope = fixture.Services.CreateScope();
        var db = Db(scope);
        await RecognitionProfileSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        var profile = await db.RecognitionProfiles.FirstAsync(p => p.Code == BuiltInProfileCodes.CoverTitle);
        var edited = RecognitionProfileJson.ReadFields(profile.Fields).ToList();
        edited[0] = edited[0] with { Description = "уточнено пользователем" };
        profile.Update(profile.Name, RecognitionProfileJson.WriteFields(edited), null, null);
        // Симулируем «заводской ушёл вперёд в новой версии»: хеш сида больше не совпадает с текущим.
        db.Entry(profile).Property(nameof(profile.BuiltInHash)).CurrentValue = "OUTDATED";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RecognitionProfileSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        var after = await db.RecognitionProfiles.AsNoTracking().FirstAsync(p => p.Code == BuiltInProfileCodes.CoverTitle);
        Assert.True(after.IsModified);        // правка сохранена
        Assert.True(after.BuiltInOutdated);   // но расхождение с заводским теперь видно
        Assert.Equal("уточнено пользователем", RecognitionProfileJson.ReadFields(after.Fields)[0].Description);

        // Возвращаем профиль в заводское состояние, чтобы не мешать другим тестам.
        var toReset = await db.RecognitionProfiles.FirstAsync(p => p.Code == BuiltInProfileCodes.CoverTitle);
        toReset.ResetToBuiltIn();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await RecognitionProfileSeeder.SeedAsync(db);
    }

    [Fact]
    public async Task Provider_ResolvesBuiltInByTag_AndRejectsNonTableTag()
    {
        using var scope = fixture.Services.CreateScope();
        var db = Db(scope);
        await RecognitionProfileSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();

        var provider = new RecognitionProfileProvider(db);
        var cable = await provider.GetForTagAsync(Domain.Schema.FunctionalTag.GostDocCableJournal);
        Assert.NotNull(cable);
        Assert.Equal(RecognitionProfileKind.CableJournal, cable!.Kind);
        Assert.True(provider.IsTableTag(Domain.Schema.FunctionalTag.GostDocSpecification));

        Assert.Null(await provider.GetForTagAsync("что-то другое"));
        Assert.False(provider.IsTableTag("что-то другое"));
    }
}
