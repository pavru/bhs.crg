using System.Reflection;
using NPOI.SS.UserModel;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// NPOI — ветка 2.7, не выше 2.7.4: последний выпуск, где пакет и все его зависимости под одной
/// Apache 2.0 (issue #1756).
///
/// <para>В 2.7.5 и 2.7.6 NPOI тянет NSax под LGPL‑3.0, а с 2.8.0 бинарники на NuGet идут под EULA «Open
/// Source Maintenance Fee» со сбором для организаций с выручкой от US$10 000 в год. Ни то ни другое
/// нам не подходит (DEV_NOTES, «Поставка»). Сама библиотека новой версии не сопротивляется: всё
/// собирается и работает, а у 2.8 появляется только предупреждение сборки. Границу держит и диапазон
/// версии в csproj; тест — на случай, когда диапазон перепишут вместе с числом. Пакеты NuGet бот
/// обновления сегодня не смотрит — если его включат, NPOI ему нужно исключить выше 2.7.4 (записано в
/// <c>.github/dependabot.yml</c>).</para>
///
/// <para>Версия читается у загруженной сборки, а не из csproj: проверяется то, что поедет в поставку,
/// в том числе если пакет придёт транзитивно с другой версией. Номер пакета — из информационной
/// версии (<c>2.7.4+…</c>), как у MediatR: версия сборки у 2.7.2 и 2.7.4 совпадает с ним, но у
/// запрещённых выпусков это не проверено, а промах здесь — молча пропущенная LGPL.</para>
/// </summary>
public class NpoiLicenseTests
{
    private static readonly Version FirstForbidden = new(2, 7, 5);

    [Fact]
    public void NPOI_остаётся_на_ветке_2_7_без_чужой_лицензии()
    {
        var informational = typeof(IWorkbook).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(informational is null,
            "У сборки NPOI нет информационной версии: номер пакета не прочитать, и граница лицензии " +
            "не проверена. Сторож читает номер оттуда (issue #1756) — найдите, где NPOI пишет его теперь.");
        var package = informational.Split('+', '-')[0];
        var version = Version.Parse(package);
        var reason = version.Major > 2 || version is { Major: 2, Minor: >= 8 }
            ? "с 2.8.0 бинарники NPOI на NuGet под EULA «Open Source Maintenance Fee» со сбором"
            : "в 2.7.5 и выше NPOI тянет NSax под LGPL‑3.0";

        Assert.True(version < FirstForbidden,
            $"NPOI {package}: {reason}. Решение — остаться на 2.7.4 (issue #1756, DEV_NOTES «Поставка»). " +
            "Не поднимайте версию: понадобилась новая — сначала решение владельца о NPOI (сбор, " +
            "самосборка или замена), и этот тест правится тем же PR.");
    }
}
