namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Снимок каталога, который генерация отдаёт компилятору, — снаружи, без флага «не убирать за
/// собой» в рабочем коде (design D6 change core-generation-input-one-path).
///
/// <para>Как: до запроса запоминаем каталоги во временной папке, потом опрашиваем новые, пока не
/// найдётся тот, где <c>template.typ</c> несёт метку теста. Дальше копим ОБЪЕДИНЕНИЕ путей, которые в
/// нём видели, пока запрос не завершится: файлы в каталоге только прибавляются, а его удаление идёт
/// после компиляции. Шаблон сида считает в цикле — компиляция длится не меньше секунды, и опрос раз в
/// несколько миллисекунд застаёт каталог полным с большим запасом.</para>
///
/// <para>⚠️ Каталог ищется под <see cref="Path.GetTempPath" /> по имени-<see cref="Guid" /> — так его
/// создаёт генерация. Сменится место — сторож откажет словами «каталог компиляции не найден», а не
/// молчаливым зелёным.</para>
/// </summary>
public sealed class CompileDirectoryWatch : IAsyncDisposable
{
    private readonly string _marker;
    private readonly HashSet<string> _before;
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private string? _dir;
    private string? _dataJson;

    private CompileDirectoryWatch(string marker)
    {
        _marker = marker;
        _before = Directory.EnumerateDirectories(Path.GetTempPath()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Начать наблюдение ДО запроса генерации.</summary>
    public static CompileDirectoryWatch Start(string marker) => new(marker);

    /// <summary>Пути файлов каталога от его корня, через «/», без результата компиляции.</summary>
    public IReadOnlySet<string> Paths => _paths;

    /// <summary><c>data.json</c> каталога — как его прочитал бы компилятор.</summary>
    public string DataJson => _dataJson ?? throw new InvalidOperationException("data.json в каталоге не застали");

    /// <summary>Закончить наблюдение — после того, как ответ на запрос получен.</summary>
    public async Task<CompileDirectoryWatch> StopAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        if (_dir is not null) Scan(_dir);   // каталог мог остаться: удаление «по возможности»
        Assert.True(_dir is not null,
            "Каталог компиляции генерации не найден: под временной папкой нет каталога с template.typ, " +
            "несущим метку теста. Генерация не дошла до записи шаблона или пишет каталог в другое место.");
        return this;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_dir is null) _dir = Find();
                else Scan(_dir);
            }
            catch (IOException) { /* файл пишется или каталог удаляется — следующий круг */ }
            catch (UnauthorizedAccessException) { }
            try { await Task.Delay(3, _stop.Token); } catch (OperationCanceledException) { }
        }
    }

    private string? Find()
    {
        foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath()))
        {
            if (_before.Contains(dir) || _checked.Contains(dir)) continue;
            if (!Guid.TryParse(Path.GetFileName(dir), out _)) { _checked.Add(dir); continue; }
            var template = Path.Combine(dir, "template.typ");
            if (!File.Exists(template)) continue;   // ещё не записан — вернёмся на следующем круге
            string text;
            try { text = File.ReadAllText(template); } catch (IOException) { continue; }
            if (text.Contains(_marker, StringComparison.Ordinal)) { Scan(dir); return dir; }
            // «Чужой» — только когда template.typ дописан: следом за ним пишется systemlib.typ. Иначе
            // чтение посреди записи (метки ещё нет) вычеркнуло бы нужный каталог насовсем.
            if (File.Exists(Path.Combine(dir, "systemlib.typ"))) _checked.Add(dir);
        }
        return null;
    }

    private void Scan(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (rel == "output.pdf") continue;
            _paths.Add(rel);
            if (rel == "data.json" && _dataJson is null) _dataJson = TryReadJson(file);
        }
    }

    private static string? TryReadJson(string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            System.Text.Json.JsonDocument.Parse(text).Dispose();   // дописан целиком?
            return text;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync();
        try { await _loop; } catch { /* уже разобрано */ }
        if (_dir is not null)
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        _stop.Dispose();
    }
}
