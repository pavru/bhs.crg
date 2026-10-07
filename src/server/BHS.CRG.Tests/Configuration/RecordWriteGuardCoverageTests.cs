using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перечень ВСЕХ мест, где данные объекта по схеме типа попадают в базу, — и вердикт у каждого:
/// охраняется или намеренно нет (issue #957).
///
/// Тест написан против нашей повторяющейся ошибки — «закрыл один вход из нескольких». Охрана,
/// подключённая к трём адресам из четырёх, выглядит работающей ровно так же, как подключённая ко
/// всем; разницу видно только в тот день, когда кто-то сохранит через четвёртый. Заведя новый
/// адрес записи, автор упрётся в красный тест и примет решение — пока оно ещё дёшево.
///
/// Проверяется В ОБЕ СТОРОНЫ: у объявленного охраняемым рядом обязан стоять вызов охраны, у
/// объявленного свободным — обязан НЕ стоять. Односторонняя проверка тихо соглашалась бы с тем, что
/// охрану из адреса убрали.
///
/// <para>⚠️ С issue #1185 вердикт значит больше: в той же точке стоит правило архива — новая ссылка
/// на архивную запись отвергается. «Охраняемый» теперь читается и как «здесь ссылки ВЫБИРАЮТ», а
/// «свободный» — и как «здесь стоявшие ссылки переносят»: позови машинный путь охрану как создание,
/// у него «как лежит» было бы пусто, и каждая старая ссылка на архивную запись стала бы отказом.</para>
///
/// <para><b>Проекты модулей — в том же перечне</b> (задача M3, issue #1071). Запись, которую модуль
/// хранит в своей таблице, ядро не сохраняет и проверить не может: охрану зовёт сам модуль, портом
/// <c>IModuleWriteGuard</c>. Забытый вызов там выглядит так же, как забытый у ядра, — никак. Путь
/// записи у модуля свой (метод его сущности), поэтому каждый тип с носителем «таблица модуля»
/// обязан назвать его в <see cref="ModuleTableWrites" />: тип без названного пути — красный тест.</para>
/// </summary>
public class RecordWriteGuardCoverageTests
{
    private static readonly string[] Projects =
        SolutionModules.WithCore("BHS.CRG.Application", "BHS.CRG.Api");

    /// <summary>
    /// Чем тип модуля с носителем «таблица модуля» кладёт данные по схеме в свою запись: код типа →
    /// выражение, по которому это место находят. Ключи сверяются с объявлениями поставленных модулей.
    ///
    /// <para>⚠️ Выражение — ИМЯ МЕТОДА сущности, без имени переменной перед точкой: <c>draft.…</c> и
    /// <c>write.Invoice.…</c> — тот же путь записи, что <c>invoice.…</c>. Поэтому метод обязан зваться
    /// неповторимо (не <c>Apply</c> — так зовутся правки строк и частей разноски).</para>
    /// </summary>
    private static readonly Dictionary<string, string> ModuleTableWrites = new()
    {
        ["СчётНаОплату"] = @"\.ApplyRequisites\(",
    };

    /// <summary>Как данные попадают в объект: присвоение или конструктор с готовыми данными.</summary>
    private static readonly Regex DataWrite = new(
        @"\.SetData\(|\.Update\(cmd\.DisplayName|\.Update\(cmd\.DocumentTypeId|DomainObject\.Create\(|DomainObject\.CloneAsDocument\(|QualityDocument\.Create\(|"
        + string.Join('|', ModuleTableWrites.Values),
        RegexOptions.Compiled);

    /// <summary>
    /// Вызов охраны: у ядра — <c>WriteGuard.EnsureAllowedAsync</c>, у модуля — метод порта
    /// <c>RefusalsAsync</c> либо обёртка над ним (отказы порт возвращает, а не бросает, и превращает
    /// их в отказ запросу модуль). По имени метода, а не переменной: порт вправе зваться как угодно.
    /// </summary>
    private static readonly Regex GuardCall = new(
        @"WriteGuard\.EnsureAllowedAsync|\bawait\s+EnsureAllowedAsync\(|\.RefusalsAsync\(", RegexOptions.Compiled);

    /// <summary>Сколько строк выше места записи ищется вызов охраны.</summary>
    private const int GuardLookback = 15;

    private const bool Guarded = true;
    private const bool Free = false;

    /// <summary>
    /// Ключ — «файл|строка кода», значение — вердикт и причина. Причина обязательна и у охраняемых
    /// тоже: перечень читают, чтобы понять замысел, а не чтобы убедиться, что он непустой.
    /// </summary>
    private static readonly Dictionary<string, (bool Guarded, string Why)> Writes = new()
    {
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|obj.SetData(cmd.Requisites);"] =
            (Guarded, "реквизиты документа — главный ручной путь"),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|var entry = DomainObject.Create(cmd.CompositeTypeId, cmd.DisplayName, cmd.Data, cmd.Scope, cmd.ScopeId, cmd.Aliases);"] =
            (Guarded, "создание записи общих данных"),
        ["BHS.CRG.Application/Documents/CommonDataHandlers.cs|entry.Update(cmd.DisplayName, data, cmd.Aliases);"] =
            (Guarded, "правка записи общих данных — проверяется ПОСЛЕ слияния с привязками наборов"),
        ["BHS.CRG.Application/QualityDocs/Handlers.cs|var doc = QualityDocument.Create(cmd.DocumentTypeId, cmd.DisplayName, cmd.Requisites, cmd.Scope, cmd.ScopeId, cmd.Source);"] =
            (Guarded, "создание документа качества — сегодняшнего прообраза записи модуля"),
        ["BHS.CRG.Application/QualityDocs/Handlers.cs|doc.Update(cmd.DocumentTypeId, cmd.DisplayName, cmd.Requisites);"] =
            (Guarded, "правка документа качества"),
        ["BHS.CRG.Api/Endpoints/Documents/PrintFormEndpoints.cs|instance.SetData(patched);"] =
            (Guarded, "печатная форма кладёт прочитанные значения как есть — и пишет прямо в слое API, мимо MediatR"),
        ["BHS.CRG.Modules.Costs/Endpoints/InvoiceEndpoints.cs|invoice.ApplyRequisites(columns, rest, dueDateByHand: !marks.Contains(InvoiceRequisites.DueDateKey));"] =
            (Guarded, "создание счёта: реквизиты пришли из формы или из распознавания, лежащего нет — всё вносится впервые"),
        ["BHS.CRG.Modules.Costs/Endpoints/InvoiceEndpoints.cs|invoice.ApplyRequisites(columns, rest, dueDateByHand: true);"] =
            (Guarded, "правка шапки счёта — проверяется против лежащего, дополненного неприсланным состоянием"),

        ["BHS.CRG.Application/Documents/DocumentTypeHandlers.cs|inst.SetData(System.Text.Json.JsonDocument.Parse(root.ToJsonString()));"] =
            (Free, "перенос ключа поля и починка аудита: их работа и есть трогать кривые данные; " +
                   "охрана сделала бы битую запись непочинимой, а правку схемы — обрывающейся на середине"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|source.SetData(data);"] =
            (Free, "перенос документа в другой комплект переписывает СВОИ же значения (вычищает " +
                   "неразрешимые ссылки); отказ сделал бы документ непереносимым"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var clone = DomainObject.CloneAsDocument(source, targetSet.Id, data, baseName);"] =
            (Free, "копия документа в другой комплект повторяет значения источника: ссылки в ней стояли, " +
                   "их никто не выбирал — правило архива отвергло бы копию документа закрытого периода"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var clone = DomainObject.CloneAsDocument(source, setId, data, $\"Копия {baseName}\");"] =
            (Free, "копия документа в том же комплекте — то же самое: значения источника, ссылки стояли"),
        ["BHS.CRG.Application/Documents/DocumentSetHandlers.cs|var obj = DomainObject.Create(cmd.DocumentTypeId, null, JsonDocument.Parse(\"{}\"),"] =
            (Free, "создание пустого документа в комплекте: вносить нечего"),
        ["BHS.CRG.Application/QualityDocs/SearchCommands.cs|var doc = QualityDocument.Create(cmd.DocumentTypeId, name, System.Text.Json.JsonDocument.Parse(\"{}\"),"] =
            (Free, "импорт из интернета заводит документ с пустыми реквизитами — вносить нечего"),
        ["BHS.CRG.Application/Generation/GenerateDocumentHandler.cs|instance.SetData(stamp(instance.Data));"] =
            (Free, "штамп метаданных на последнем шаге выпуска: отказ из-за чужого старого значения " +
                   "остановил бы генерацию; запертых полей штамп не пишет"),
        ["BHS.CRG.Application/Catalog/Handlers.cs|entity.Update(cmd.DisplayName, cmd.Data);"] =
            (Free, "устаревший каталог сущностей: типизирован строкой entityType, схемы типа у него " +
                   "нет вовсе — охране не с чем сверять"),
    };

    [Fact]
    public void Каждый_путь_записи_данных_назван_и_рассужден()
    {
        var found = FindWrites();

        var undeclared = found.Keys.Where(k => !Writes.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(undeclared.Count == 0,
            "Появился путь записи данных, о котором охрана не знает:\n" + string.Join("\n", undeclared) +
            "\n\nВпишите его в Writes: Guarded — если рядом обязан стоять вызов охраны " +
            "(WriteGuard.EnsureAllowedAsync у ядра, порт IModuleWriteGuard у модуля), Free — с причиной, " +
            "почему охраны там быть не должно.");

        var stale = Writes.Keys.Where(k => !found.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "В перечне записи, которых в коде больше нет: " + string.Join("\n", stale) +
            "\nУберите строки — иначе перечень описывает несуществующее.");
    }

    [Fact]
    public void Охраняемый_путь_действительно_зовёт_охрану_а_свободный_нет()
    {
        var found = FindWrites();
        var wrong = new List<string>();

        foreach (var (key, (guarded, why)) in Writes)
        {
            if (!found.TryGetValue(key, out var guardNearby)) continue; // о пропаже говорит соседний тест
            if (guarded && !guardNearby)
                wrong.Add($"{key}\n    объявлен охраняемым ({why}), но вызова охраны рядом нет");
            if (!guarded && guardNearby)
                wrong.Add($"{key}\n    объявлен свободным ({why}), а охрана рядом стоит — решение изменилось?");
        }

        Assert.True(wrong.Count == 0, "Перечень охраны разошёлся с кодом:\n" + string.Join("\n", wrong));
    }

    /// <summary>
    /// Каждый тип модуля, чьи записи лежат в таблице модуля, назвал свой путь записи — и назвал
    /// действующий. Без первого новый такой тип писал бы данные по схеме, а перечень его не видел бы
    /// вовсе; без второго переименованный метод увёл бы путь из-под перечня при зелёном тесте.
    /// </summary>
    [Fact]
    public void Каждый_тип_в_таблице_модуля_назвал_свой_путь_записи()
    {
        var own = BHS.CRG.Api.Modules.DeliveredModules.All()
            .SelectMany(m => m.RecordTypes)
            .Where(t => t.Storage == BHS.CRG.Modules.ModuleStorage.ModuleTable)
            .Select(t => t.Code)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(own.Count > 0,
            "Среди поставленных модулей нет ни одного типа с носителем «таблица модуля» — проверять нечего. " +
            "Счёт таким был: либо объявление сменило носитель, либо модули собраны не те.");
        Assert.Equal(own, ModuleTableWrites.Keys.Order(StringComparer.Ordinal).ToList());

        var found = FindWrites().Keys.Select(k => k[(k.IndexOf('|') + 1)..]).ToList();
        var unused = ModuleTableWrites
            .Where(w => !found.Any(line => Regex.IsMatch(line, w.Value)))
            .Select(w => $"{w.Key}: {w.Value}")
            .ToList();
        Assert.True(unused.Count == 0,
            "Путь записи назван, а в коде по нему ничего не находится: " + string.Join("; ", unused) +
            ".\nМетод переименовали? Поправьте выражение — иначе запись этого типа перечень не видит.");
    }

    /// <summary>Место записи → стоит ли рядом (выше) вызов охраны.</summary>
    private static Dictionary<string, bool> FindWrites()
    {
        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var project in Projects)
            foreach (var file in SourceTree.Files(project))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!DataWrite.IsMatch(lines[i])) continue;
                    var key = $"{SourceTree.Relative(file)}|{lines[i].Trim()}";
                    var guarded = false;
                    for (var back = Math.Max(0, i - GuardLookback); back < i; back++)
                        if (GuardCall.IsMatch(lines[back])) guarded = true;
                    // Одинаковые строки в одном файле сливаются: если хоть одна из них охраняется,
                    // считаем охраняемой пару — иначе перечень потребовал бы различать их номерами
                    // строк, а номера сдвигает любая правка выше по файлу.
                    found[key] = found.TryGetValue(key, out var was) && was || guarded;
                }
            }
        return found;
    }
}
