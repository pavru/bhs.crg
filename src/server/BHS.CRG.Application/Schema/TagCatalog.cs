namespace BHS.CRG.Application.Schema;

/// <summary>
/// Реестр функциональных тэгов этого экземпляра: тэги ядра плюс тэги ВКЛЮЧЁННЫХ модулей
/// (ТЗ TYPE-22, issue #959).
///
/// <para>Служба, а не статический список, потому что состав зависит от набора модулей, а набор
/// задаётся при запуске. Тэг выключенного модуля не предлагается в редакторе схем: читать его на
/// этом экземпляре некому, а предложенная метка обещает поведение, которого нет.</para>
///
/// <para>⚠️ Реестр отвечает за ПРЕДЛОЖЕНИЕ тэга, а не за судьбу уже проставленных. Тэг
/// выключенного модуля, стоящий в схеме, остаётся в ней и переживает сохранение: выключение
/// модуля — не повод переписывать конфигурацию заказчика. Поэтому ни одна проверка здесь не
/// говорит «такого тэга нет» о том, что уже лежит в базе.</para>
/// </summary>
public sealed class TagCatalog
{
    private readonly Dictionary<string, TagDefinition> _byCode;

    public TagCatalog(IReadOnlyList<TagDefinition> all)
    {
        All = all;
        _byCode = all.ToDictionary(t => t.Code, StringComparer.Ordinal);
    }

    /// <summary>Тэги, предлагаемые на этом экземпляре: ядро + включённые модули.</summary>
    public IReadOnlyList<TagDefinition> All { get; }

    public TagDefinition? Find(string code) => _byCode.GetValueOrDefault(code);

    /// <summary>
    /// Собирает реестр из тэгов ядра и тэгов включённых модулей.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Код тэга объявлен дважды. Отказ при СТАРТЕ, а не тихий победитель: у двух объявлений одного
    /// кода разные подписи, области и кратность, и выбранное молча решало бы, что именно увидит
    /// администратор в редакторе и что проверит сервер при сохранении. Та же причина, по которой
    /// отказом встречается двойная настройка набора модулей.
    /// </exception>
    /// <param name="fromModules">
    /// Тэги ВКЛЮЧЁННЫХ модулей, уже переведённые в слова ядра. Перевод делает слой адресов: у этой
    /// сборки нет ссылки на сборку модулей — направление ссылок одностороннее (ТЗ CORE-2), и ровно
    /// так же в ядро попадают объявления типов модуля.
    /// </param>
    public static TagCatalog Build(IReadOnlyList<TagDefinition> core, IReadOnlyList<TagDefinition> fromModules)
    {
        var all = new List<TagDefinition>(core);
        all.AddRange(fromModules);

        var duplicates = all
            .GroupBy(t => t.Code, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"«{g.Key}» — {string.Join(", ", g.Select(t => t.Owner))}")
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                "Код функционального тэга объявлен дважды: " + string.Join("; ", duplicates) + ". " +
                "Кодом тэга код находит поле, и второе объявление молча вытеснило бы первое — " +
                "вместе с его подписью, областью применения и кратностью.");

        return new TagCatalog(all);
    }
}
