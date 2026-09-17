using System.Text.Json;
using System.Text.Json.Serialization;
using BHS.CRG.Application.Notifications;

namespace BHS.CRG.Tests.Notifications;

/// <summary>
/// Форма снимка на границе «сервер → колокольчик». Проверяется отдельно, потому что здесь
/// компилятор и <c>tsc</c> одинаково бесполезны: у клиента это обычный интерфейс TypeScript без
/// проверки во время работы, и приди состояние числом вместо строки — сравнение молча не совпало
/// бы, а панель показывала бы «в норме» при любом состоянии.
/// </summary>
public class ComponentHealthWireTests
{
    private static string Serialize(ComponentHealth health)
    {
        // Те же настройки, что у API: Program.cs добавляет JsonStringEnumConverter к веб-умолчаниям.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(health, options);
    }

    [Fact]
    public void Состояние_уезжает_строкой_а_не_числом()
    {
        var json = Serialize(new ComponentHealth("recognition.gemini", "Gemini (распознавание)",
            HealthClass.Engine, HealthState.Flapping, "не ответил", DateTimeOffset.UnixEpoch));

        Assert.Contains("\"state\":\"Flapping\"", json);
        Assert.Contains("\"code\":\"recognition.gemini\"", json);
    }

    [Theory]
    [InlineData(HealthState.Up, "true")]
    [InlineData(HealthState.Flapping, "true")]
    [InlineData(HealthState.Down, "false")]
    public void Булево_для_прежних_потребителей_остаётся_на_месте(HealthState state, string expected)
    {
        // «Отвечает через раз» — ещё не отказ: старый потребитель не должен из-за него краснеть.
        var json = Serialize(new ComponentHealth("db", "База данных",
            HealthClass.Core, state, null, DateTimeOffset.UnixEpoch));

        Assert.Contains($"\"healthy\":{expected}", json);
    }
}

/// <summary>
/// Между пробой и объявлением. Главный случай — первый тест: именно чередование «упал/поднялся»
/// дало 300 уведомлений на исправно работавшем движке (issue #917), и раньше каждая смена пробы
/// объявлялась немедленно.
/// </summary>
public class HealthHysteresisTests
{
    private const string Engine = "recognition.gemini";
    private const string Core = "db";

    private static List<HealthTransition> Run(HealthHysteresis h, string code, HealthClass @class, string pattern)
        => [.. pattern.Select(c => h.Observe(code, @class, c == 'S'))];

    [Fact]
    public void Чередование_пробы_не_объявляется_вовсе()
    {
        var h = new HealthHysteresis();

        // Ровно живой случай: проба падает через раз, компонент при этом работает.
        var moves = Run(h, Engine, HealthClass.Engine, "SFSFSFSFSFSFSFSFSFSF");

        Assert.All(moves, m => Assert.Equal(HealthTransition.None, m));
    }

    [Fact]
    public void Отказ_движка_объявляется_один_раз_после_серии()
    {
        var h = new HealthHysteresis();

        var moves = Run(h, Engine, HealthClass.Engine, "SFFFFF");

        // Три неудачи подряд — объявляем; дальнейшие неудачи молчат.
        Assert.Equal(
        [
            HealthTransition.None, HealthTransition.None, HealthTransition.None,
            HealthTransition.WentDown, HealthTransition.None, HealthTransition.None,
        ], moves);
    }

    [Fact]
    public void Возврат_движка_объявляется_после_двух_удач()
    {
        var h = new HealthHysteresis();
        Run(h, Engine, HealthClass.Engine, "FFF");

        var moves = Run(h, Engine, HealthClass.Engine, "SS");

        Assert.Equal([HealthTransition.None, HealthTransition.CameUp], moves);
    }

    [Fact]
    public void Одна_удача_посреди_отказа_серию_возврата_сбрасывает()
    {
        var h = new HealthHysteresis();
        Run(h, Engine, HealthClass.Engine, "FFF");

        // Удача, снова неудача, снова удача — двух подряд не набралось, объявлять нечего.
        var moves = Run(h, Engine, HealthClass.Engine, "SFS");

        Assert.All(moves, m => Assert.Equal(HealthTransition.None, m));
    }

    [Fact]
    public void Ядро_объявляется_первой_же_неудачей()
    {
        var h = new HealthHysteresis();

        // У базы проверка — соединение с локальным сокетом, мигать нечем, и откладывать аварию
        // на две минуты неприемлемо.
        Assert.Equal(HealthTransition.WentDown, h.Observe(Core, HealthClass.Core, ok: false));
        Assert.Equal(HealthTransition.CameUp, h.Observe(Core, HealthClass.Core, ok: true));
    }

    [Fact]
    public void Первая_удачная_проба_фиксирует_норму_молча()
    {
        var h = new HealthHysteresis();

        Assert.Equal(HealthTransition.None, h.Observe(Engine, HealthClass.Engine, ok: true));
        // Иначе каждый запуск приложения начинался бы с «восстановлен» на ровном месте.
        Assert.Equal(HealthState.Up, h.StateOf(Engine, ok: true));
    }

    [Fact]
    public void Неподтверждённый_компонент_показывает_саму_пробу()
    {
        var h = new HealthHysteresis();
        h.Observe(Engine, HealthClass.Engine, ok: false);

        // Мёртвый с самого старта не должен числиться «в норме», пока копится серия.
        Assert.Equal(HealthState.Down, h.StateOf(Engine, ok: false));
    }

    [Fact]
    public void Расхождение_пробы_с_объявленным_состоянием_показывается_отдельно()
    {
        var h = new HealthHysteresis();
        h.Observe(Engine, HealthClass.Engine, ok: true);

        h.Observe(Engine, HealthClass.Engine, ok: false);
        Assert.Equal(HealthState.Flapping, h.StateOf(Engine, ok: false));

        h.Observe(Engine, HealthClass.Engine, ok: true);
        Assert.Equal(HealthState.Up, h.StateOf(Engine, ok: true));
    }

    [Fact]
    public void Забытый_компонент_возвращается_с_чистой_историей()
    {
        var h = new HealthHysteresis();
        Run(h, Engine, HealthClass.Engine, "FFF");

        // Движок выключили — перестали проверять.
        h.Retain([Core]);

        // И включили снова: серия отказа не должна была пережить выключение.
        Assert.Equal(HealthTransition.None, h.Observe(Engine, HealthClass.Engine, ok: false));
    }

    [Fact]
    public void Счётчики_разных_компонентов_не_смешиваются()
    {
        var h = new HealthHysteresis();

        h.Observe(Engine, HealthClass.Engine, ok: false);
        h.Observe("recognition.ollama", HealthClass.Engine, ok: false);
        h.Observe(Engine, HealthClass.Engine, ok: false);

        // У Gemini две неудачи, у Ollama одна — до объявления не дотянул никто.
        Assert.Equal(HealthTransition.WentDown, h.Observe(Engine, HealthClass.Engine, ok: false));
        Assert.Equal(HealthTransition.None, h.Observe("recognition.ollama", HealthClass.Engine, ok: false));
    }
}
