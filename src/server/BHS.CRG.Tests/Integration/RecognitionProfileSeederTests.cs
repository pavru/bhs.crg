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
        var original = RecognitionProfileJson.ReadFields(profile.Fields).Count;

        // Пользователь добавил свою колонку.
        var edited = RecognitionProfileJson.ReadFields(profile.Fields).ToList();
        edited.Add(new RecognitionProfileField("МойСтолбец", "Добавлено пользователем", "string"));
        profile.Update(profile.Name, RecognitionProfileJson.WriteFields(edited), null);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Апгрейд (повторный сидинг) правку сохраняет.
        await RecognitionProfileSeeder.SeedAsync(db);
        db.ChangeTracker.Clear();
        var afterSeed = await db.RecognitionProfiles.AsNoTracking()
            .FirstAsync(p => p.Code == BuiltInProfileCodes.SpecificationTable);
        Assert.True(afterSeed.IsModified);
        Assert.Contains(RecognitionProfileJson.ReadFields(afterSeed.Fields), f => f.Name == "МойСтолбец");

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
        Assert.DoesNotContain(RecognitionProfileJson.ReadFields(restored.Fields), f => f.Name == "МойСтолбец");
        Assert.Equal(original, RecognitionProfileJson.ReadFields(restored.Fields).Count);
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
