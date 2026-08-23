using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Support;
using BHS.CRG.Domain.Support;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Приём сообщений об ошибках и их разбор (issue #834).
///
/// Ключевое, что здесь проверяется, — адресность уведомлений. Сообщение об ошибке это РАБОТА, и у
/// неё должен быть исполнитель: опубликуй мы её общесистемно (<c>UserId == null</c>), первый же
/// смахнувший крестиком снял бы запись со всех администраторов сразу (issue #821). Ошибка такого
/// рода не падает и не логируется — она просто оставляет сообщения неразобранными.
/// </summary>
[Collection("Integration")]
public class BugReportServiceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IBugReportService Service(IServiceScope s)
        => s.ServiceProvider.GetRequiredService<IBugReportService>();

    private async Task<Guid> CreateUserAsync(string display, string? role)
    {
        using var scope = fixture.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var rm = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (role is not null && !await rm.RoleExistsAsync(role))
            Assert.True((await rm.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);

        var email = $"{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = display };
        Assert.True((await um.CreateAsync(user, "Passw0rd!")).Succeeded);
        if (role is not null) Assert.True((await um.AddToRoleAsync(user, role)).Succeeded);
        return user.Id;
    }

    /// <summary>
    /// Оставить администратором только указанных: приложение при старте выдаёт роль Admin каждому
    /// пользователю без роли, а учётные записи между классами тестов не чистятся — иначе рассылка
    /// уходила бы сотням накопившихся записей. Тот же приём, что в <see cref="UpdateNotifierTests" />.
    /// </summary>
    private async Task KeepOnlyAdminsAsync(params Guid[] keep)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var adminRoleId = await db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).FirstAsync();
        await db.UserRoles
            .Where(ur => ur.RoleId == adminRoleId && !keep.Contains(ur.UserId))
            .ExecuteDeleteAsync();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task Submit_NotifiesEveryAdminPersonally_AndLeadsToTheScreen()
    {
        var firstAdmin = await CreateUserAsync("Первый администратор", "Admin");
        var secondAdmin = await CreateUserAsync("Второй администратор", "Admin");
        var author = await CreateUserAsync("Пользователь", "User");
        await KeepOnlyAdminsAsync(firstAdmin, secondAdmin);

        using (var scope = fixture.Services.CreateScope())
            await Service(scope).SubmitAsync(author, "Кнопка «Сохранить» не нажимается.", null, null);

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var mine = await db.Notifications.AsNoTracking()
            .Where(n => n.Source == BugReportService.NotificationSource).ToListAsync();

        Assert.Equal(2, mine.Count);
        Assert.Contains(mine, n => n.UserId == firstAdmin);
        Assert.Contains(mine, n => n.UserId == secondAdmin);
        // Ни одной общесистемной: у неё прочтение общее на всех, и первый прочитавший погасил бы
        // запись у остальных.
        Assert.DoesNotContain(mine, n => n.UserId is null);
        // Автор — не адресат: он только что нажал «Отправить» и результат видел.
        Assert.DoesNotContain(mine, n => n.UserId == author);
        // Колокольчик — сигнал, а не рабочее место: уведомление ведёт на экран разбора.
        Assert.All(mine, n => Assert.Equal(BugReportService.AdminScreenLink, n.LinkUrl));
    }

    /// <summary>Администратор, отправивший сообщение сам, не уведомляет сам себя.</summary>
    [Fact]
    public async Task Submit_ByAdmin_DoesNotNotifyTheAuthor()
    {
        var onlyAdmin = await CreateUserAsync("Единственный администратор", "Admin");
        await KeepOnlyAdminsAsync(onlyAdmin);

        using (var scope = fixture.Services.CreateScope())
            await Service(scope).SubmitAsync(onlyAdmin, "Сам нашёл, сам записал.", null, null);

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Notifications.AsNoTracking()
            .Where(n => n.Source == BugReportService.NotificationSource).ToListAsync());
    }

    [Fact]
    public async Task Submit_RefusesEmptyMessage()
    {
        var author = await CreateUserAsync("Пользователь", "User");
        using var scope = fixture.Services.CreateScope();

        var ex = await Assert.ThrowsAsync<InvalidRequestException>(
            () => Service(scope).SubmitAsync(author, "   ", null, null));
        Assert.Contains("Опишите", ex.Message);
    }

    [Fact]
    public async Task Submit_RefusesOverlongMessage()
    {
        var author = await CreateUserAsync("Пользователь", "User");
        using var scope = fixture.Services.CreateScope();

        await Assert.ThrowsAsync<InvalidRequestException>(
            () => Service(scope).SubmitAsync(author, new string('я', BugReportService.MessageLimit + 1), null, null));
    }

    /// <summary>
    /// Версия сервера подмешивается к техблоку клиента, а сам техблок сохраняется как прислали.
    /// Обе нужны порознь: вкладка SPA переживает обновление сервера.
    /// </summary>
    [Fact]
    public async Task Submit_AddsServerVersion_AndKeepsClientTech()
    {
        var author = await CreateUserAsync("Пользователь", "User");
        Guid id;
        using (var scope = fixture.Services.CreateScope())
            id = await Service(scope).SubmitAsync(author, "Не открывается комплект.",
                Json("""{"version":"0.1.0","route":"/document-sets"}"""), null);

        using var check = fixture.Services.CreateScope();
        var detail = await Service(check).GetAsync(id);
        var tech = Assert.IsType<JsonElement>(detail.Tech, exactMatch: false);

        Assert.Equal("0.1.0", tech.GetProperty("version").GetString());
        Assert.Equal("/document-sets", tech.GetProperty("route").GetString());
        Assert.True(tech.TryGetProperty("server", out var server));
        Assert.False(string.IsNullOrWhiteSpace(server.GetProperty("version").GetString()));
    }

    /// <summary>
    /// Техблок собирает клиент, а не человек: перебор по размеру НЕ отказ — слова пользователя
    /// дороже. Но и молча уронить контекст нельзя, иначе администратор гадал бы, почему у этого
    /// сообщения его нет.
    /// </summary>
    [Fact]
    public async Task Submit_OversizedTech_KeepsMessage_AndSaysContextWasDropped()
    {
        var author = await CreateUserAsync("Пользователь", "User");
        var huge = Json($$"""{"stack":"{{new string('x', BugReportService.TechLimit + 1024)}}"}""");

        Guid id;
        using (var scope = fixture.Services.CreateScope())
            id = await Service(scope).SubmitAsync(author, "Белый экран.", huge, null);

        using var check = fixture.Services.CreateScope();
        var detail = await Service(check).GetAsync(id);

        Assert.Equal("Белый экран.", detail.Message);
        var tech = Assert.IsType<JsonElement>(detail.Tech, exactMatch: false);
        Assert.True(tech.TryGetProperty("dropped", out _));
        Assert.False(tech.TryGetProperty("stack", out _));
    }

    [Fact]
    public async Task Draft_StartsAsGeneratedText_SurvivesEdit_AndResetsWhenCleared()
    {
        var author = await CreateUserAsync("Пользователь", "User");
        Guid id;
        using (var scope = fixture.Services.CreateScope())
            id = await Service(scope).SubmitAsync(author, "Съехала таблица.", null, null);

        using (var scope = fixture.Services.CreateScope())
        {
            var fresh = await Service(scope).GetAsync(id);
            Assert.False(fresh.DraftEdited);
            Assert.Contains("Съехала таблица.", fresh.IssueDraft);

            var edited = await Service(scope).SaveDraftAsync(id, "Таблица съезжает при печати.");
            Assert.True(edited.DraftEdited);
            Assert.Equal("Таблица съезжает при печати.", edited.IssueDraft);
        }

        using (var scope = fixture.Services.CreateScope())
        {
            // Правка пережила перечитывание — иначе форма затирала бы её заготовкой при каждом
            // открытии карточки.
            Assert.Equal("Таблица съезжает при печати.", (await Service(scope).GetAsync(id)).IssueDraft);

            // Стёрли правку — вернулась заготовка, а не пустое поле.
            var reset = await Service(scope).SaveDraftAsync(id, "   ");
            Assert.False(reset.DraftEdited);
            Assert.Contains("Съехала таблица.", reset.IssueDraft);
        }
    }

    /// <summary>
    /// Повторная передача отвергается. Дубль в трекере убирают руками, а следа первого issue в
    /// системе не осталось бы вовсе: номер перезаписался бы вторым.
    ///
    /// Проверка стоит ДО похода в сеть — поэтому тест обходится без GitHub и без подмены HTTP.
    /// </summary>
    [Fact]
    public async Task Forward_Twice_IsRefused()
    {
        var author = await CreateUserAsync("Пользователь", "User");
        Guid id;
        using (var scope = fixture.Services.CreateScope())
            id = await Service(scope).SubmitAsync(author, "Не печатается акт.", null, null);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var report = await db.Set<BugReport>().FirstAsync(r => r.Id == id);
            report.MarkForwarded(842, "https://github.com/pavru/bhs.crg/issues/842");
            await db.SaveChangesAsync();
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var ex = await Assert.ThrowsAsync<ConflictException>(
                () => Service(scope).ForwardToGithubAsync(id, "Заголовок"));
            Assert.Contains("842", ex.Message);
        }
    }

    /// <summary>
    /// Заголовок пишет администратор, и без него передавать нельзя: в трекере issue ищут именно по
    /// заголовку. Проверка тоже до сети — пустой заголовок не должен стоить обращения к GitHub.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Forward_WithoutTitle_IsRefused(string title)
    {
        var author = await CreateUserAsync("Пользователь", "User");
        using var scope = fixture.Services.CreateScope();
        var id = await Service(scope).SubmitAsync(author, "Не печатается акт.", null, null);

        await Assert.ThrowsAsync<InvalidRequestException>(
            () => Service(scope).ForwardToGithubAsync(id, title));
    }

    [Fact]
    public async Task MarkFixed_NotifiesAuthor_WithVersion()
    {
        var author = await CreateUserAsync("Пользователь", "User");
        await KeepOnlyAdminsAsync();

        Guid id;
        using (var scope = fixture.Services.CreateScope())
            id = await Service(scope).SubmitAsync(author, "Не печатается акт.", null, null);

        using (var scope = fixture.Services.CreateScope())
        {
            var after = await Service(scope).MarkFixedAsync(id, "0.145.0");
            Assert.Equal(BugReportStatus.Fixed, after.Status);
            Assert.Equal("0.145.0", after.FixedInVersion);
        }

        using var check = fixture.Services.CreateScope();
        var notifications = check.ServiceProvider.GetRequiredService<INotificationService>();
        var forAuthor = (await notifications.GetAsync(author))
            .Where(n => n.Source == BugReportService.NotificationSource).ToList();
        var one = Assert.Single(forAuthor);
        Assert.Contains("0.145.0", one.Message);
    }

    [Fact]
    public async Task MarkFixed_RefusesWithoutVersion()
    {
        var author = await CreateUserAsync("Пользователь", "User");
        using var scope = fixture.Services.CreateScope();
        var id = await Service(scope).SubmitAsync(author, "Не печатается акт.", null, null);

        // Автору уходит именно версия — без неё уведомление сообщало бы «исправлено где-то».
        await Assert.ThrowsAsync<InvalidRequestException>(() => Service(scope).MarkFixedAsync(id, " "));

        // И не длиннее колонки. Найдено живой проверкой: без этой проверки администратор получал
        // «Внутреннюю ошибку сервера» — падала вставка, а не наш отказ.
        await Assert.ThrowsAsync<InvalidRequestException>(
            () => Service(scope).MarkFixedAsync(id, new string('9', BugReportService.VersionLimit + 1)));
    }

    [Fact]
    public async Task Reject_NotifiesAuthor_Reopen_DoesNot()
    {
        var author = await CreateUserAsync("Пользователь", "User");
        await KeepOnlyAdminsAsync();

        Guid id;
        using (var scope = fixture.Services.CreateScope())
            id = await Service(scope).SubmitAsync(author, "Почему нельзя удалить стройку?", null, null);

        using (var scope = fixture.Services.CreateScope())
            Assert.Equal(BugReportStatus.Rejected, (await Service(scope).RejectAsync(id)).Status);

        using (var scope = fixture.Services.CreateScope())
        {
            // Возврат в разбор — исправление ошибки администратора, а не событие в жизни сообщения.
            var back = await Service(scope).ReopenAsync(id);
            Assert.Equal(BugReportStatus.New, back.Status);
        }

        using var check = fixture.Services.CreateScope();
        var notifications = check.ServiceProvider.GetRequiredService<INotificationService>();
        var forAuthor = (await notifications.GetAsync(author))
            .Where(n => n.Source == BugReportService.NotificationSource).ToList();
        Assert.Single(forAuthor);
        Assert.Contains("отклонено", Assert.Single(forAuthor).Title);
    }

    /// <summary>
    /// Автора могли удалить: список обязан открыться и без него. Внешнего ключа на учётные записи у
    /// сообщений нет намеренно — удаление человека не должно уносить с собой отчёты о дефектах.
    /// </summary>
    [Fact]
    public async Task List_ShowsAuthorName_AndSurvivesDeletedAuthor()
    {
        var author = await CreateUserAsync("Иванов И.И.", "User");
        using (var scope = fixture.Services.CreateScope())
        {
            await Service(scope).SubmitAsync(author, "Первое сообщение.", null, null);

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Add(BugReport.Create(Guid.NewGuid(), "Сообщение от исчезнувшего.", null, null));
            await db.SaveChangesAsync();
        }

        using var check = fixture.Services.CreateScope();
        var list = await Service(check).ListAsync();

        Assert.Contains(list.Items, r => r.Author == "Иванов И.И." && r.Summary == "Первое сообщение.");
        Assert.Contains(list.Items, r => r.Author == "удалённый пользователь");
        // Сколько всего — отдельным числом: список усечён пределом, а других дорог к сообщению нет.
        Assert.Equal(list.Items.Count, list.Total);
    }
}
