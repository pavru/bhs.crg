using MediatR;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// MediatR — ветка 12.x, последняя под Apache 2.0 (issue #1405).
///
/// <para>С 13.0 MediatR выходит под коммерческой лицензией Lucky Penny Software или copyleft RPL‑1.5, и
/// ни одна из них нам не подходит (DEV_NOTES, «Поставка»). Сборка этому никак не сопротивляется: новая
/// версия ставится ботом обновления или рукой, всё собирается и работает, а о лицензии говорит только
/// предупреждение в журнале продукта — у заказчика, где его никто не читает. Так решение и стояло на
/// 14.1.0 с первого коммита.</para>
///
/// <para>Версия читается у загруженной сборки, а не из csproj: проверяется то, что поедет в поставку,
/// в том числе если пакет придёт транзитивно с другой версией.</para>
/// </summary>
public class MediatRLicenseTests
{
    [Fact]
    public void MediatR_остаётся_на_ветке_под_Apache_2_0()
    {
        var version = typeof(IMediator).Assembly.GetName().Version!;

        Assert.True(version.Major < 13,
            $"MediatR {version}: с 13.0 библиотека под коммерческой лицензией Lucky Penny или RPL‑1.5, и в " +
            "продукте ей нужен ключ. Решение — 12.x (issue #1405, DEV_NOTES «Поставка»). Не поднимайте " +
            "версию: понадобится новая — переход на Mediator (MIT), issue #1406.");
    }
}
