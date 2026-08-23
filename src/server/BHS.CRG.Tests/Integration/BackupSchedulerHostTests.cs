using BHS.CRG.Infrastructure.Backup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Планировщик резервного копирования под тестовым хостом не поднимается (issue #832).
///
/// Тест сторожит выключатель, а не поведение: увидеть само происшествие можно только прогоном
/// длиннее двух минут — и тогда служба сняла бы копию ТЕСТОВОЙ базы в каталог рядом с исходниками,
/// прочитав её ровно тогда, когда соседний класс делает <c>TRUNCATE</c>, и дописав строки в
/// <c>notifications</c> посреди чужого теста. Прогон целиком укладывается примерно в те же две
/// минуты, то есть встречались бы мы с этим не каждый раз и не там, где искали бы причину.
///
/// Убрать строку из фикстуры или условие из <c>Program.cs</c> можно и не заметив — этот тест
/// заметит.
/// </summary>
[Collection("Integration")]
public class BackupSchedulerHostTests(IntegrationTestFixture fixture)
{
    [Fact]
    public void TestHost_DoesNotStartTheBackupScheduler()
        => Assert.DoesNotContain(
            fixture.Services.GetServices<IHostedService>(),
            s => s is BackupScheduleService);
}
