import {
  useLocale, LOCALE_OPTIONS, SYSTEM_LOCALE, resolveLocale, formatDate, formatNumber,
} from '@/shared/hooks/useLocale';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { useTheme } from '@/shared/ui/themeContext';

const PREVIEW_DATE = new Date(2025, 11, 31, 14, 5, 0); // 31 дек 2025 14:05
const PREVIEW_NUMBER = 1234567.89;

/**
 * Оформление и язык — ЛИЧНЫЕ настройки (issue #954, ТЗ AUTH-16.6).
 *
 * ⚠️ Язык переехал сюда из «Настройки системы» (issue #954). Там он лежал за правом
 * `core.system.manage`, то есть сменить формат дат и чисел мог только администратор экземпляра —
 * личная настройка была заперта административным правом, и выглядело это как «такой настройки
 * нет». Тема стояла только в боковой панели: человек, искавший её в профиле, не находил.
 *
 * Обе настройки хранит сервер (issue #953) и обе приезжают на любой компьютер, где человек войдёт.
 */
export function AppearanceSection() {
  const [locale, setLocale, localeSave] = useLocale();
  const { saveState: themeSave } = useTheme();

  const resolved = resolveLocale(locale);

  /**
   * ⚠️ «Сохранено» показывается ПО ОТВЕТУ СЕРВЕРА, а не по нажатию (ревью PR #1000). Прежняя
   * редакция зажигала надпись сразу: при отказе запроса выбор откатывался на глазах, а зелёное
   * «Сохранено» продолжало гореть — отказ, переодетый в результат.
   *
   * Состояние берётся у обеих настроек: молчать об отказе темы, докладывая об успехе языка, значило
   * бы то же самое, только про другую строку.
   */
  const failed = localeSave === 'error' || themeSave === 'error';
  const saved = !failed && (localeSave === 'saved' || themeSave === 'saved');

  return (
    <div className="space-y-4">
      <div>
        <div className="text-xs text-fg4 mb-1" id="profile-theme-label">Тема оформления</div>
        {/* ⚠️ Своё имя у группы, не «Тема оформления» (ревью PR #1000). Такой же переключатель
            стоит в боковой панели на КАЖДОМ экране, и с одинаковым именем получалось две вещи
            сразу: на этой странице две группы с одним названием — для читающего с экрана они
            неразличимы, — а живая проверка «в профиле есть выбор темы» удовлетворялась панелью и
            проходила даже с убранным отсюда переключателем. */}
        <ThemeToggle label="Тема оформления, личная настройка" />
      </div>

      <div className="text-xs text-fg4">Язык и формат дат</div>
      <p className="text-xs text-fg3">
        Определяет формат дат и чисел в интерфейсе. Тема и язык хранятся в учётной записи и
        приедут на любой компьютер, где вы войдёте.
      </p>

      <div className="space-y-1">
        {LOCALE_OPTIONS.map(opt => {
          const isSelected = locale === opt.value;
          return (
            <button
              key={opt.value}
              type="button"
              onClick={() => setLocale(opt.value)}
              aria-pressed={isSelected}
              className={`w-full flex items-center gap-3 px-3 py-2 rounded-md text-sm text-left transition-colors ${
                isSelected
                  ? 'bg-brand-subtle border border-brand-subtle text-brand-pressed'
                  : 'border border-transparent text-fg2 hover:bg-base'
              }`}
            >
              <span className={`w-3.5 h-3.5 rounded-full border-2 shrink-0 flex items-center justify-center ${
                isSelected ? 'border-brand' : 'border-stroke-strong'
              }`}>
                {isSelected && <span className="w-2 h-2 rounded-full bg-brand block" />}
              </span>
              <span className="flex-1">{opt.label}</span>
              {opt.value === SYSTEM_LOCALE && (
                <span className="text-xs text-fg4 font-mono">{navigator.language}</span>
              )}
            </button>
          );
        })}
      </div>

      {/* Preview */}
      <div className="rounded-lg bg-base border border-stroke p-3 space-y-1.5">
        <p className="text-xs font-medium text-fg3 mb-2">Предпросмотр ({resolved})</p>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Дата:</span>
          <span className="text-fg1 font-mono">
            {formatDate(PREVIEW_DATE, locale)}
          </span>
        </div>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Дата и время:</span>
          <span className="text-fg1 font-mono">
            {formatDate(PREVIEW_DATE, locale, {
              day: '2-digit', month: '2-digit', year: 'numeric',
              hour: '2-digit', minute: '2-digit',
            })}
          </span>
        </div>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Число:</span>
          <span className="text-fg1 font-mono">
            {formatNumber(PREVIEW_NUMBER, locale)}
          </span>
        </div>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Валюта:</span>
          <span className="text-fg1 font-mono">
            {formatNumber(PREVIEW_NUMBER, locale, { style: 'currency', currency: 'RUB' })}
          </span>
        </div>
      </div>

      {saved && <p className="text-sm text-success">Сохранено</p>}
      {failed && (
        <p className="text-sm text-danger">
          Не удалось сохранить — выбор вернулся к прежнему. Повторите попытку.
        </p>
      )}
    </div>
  );
}
