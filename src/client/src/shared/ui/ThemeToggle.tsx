import { Icon } from './Icon';
import { Check } from './icons';
import { useTheme } from '@/shared/ui/themeContext';
import { THEME_OPTIONS } from '@/shared/ui/themeOptions';
import { Tooltip } from './Tooltip';

/**
 * Выбор темы — MD3 segmented button (issue #157): выбранный сегмент tonal, с галочкой. Стоит в
 * профиле: тема — личная настройка, и человек ищет её там (ТЗ AUTH-16.6). Второй вход — меню профиля
 * в шапке (#1430), со своей разметкой пунктов меню, но с теми же `THEME_OPTIONS`.
 *
 * Где значение хранится, компонент не знает: это забота `ThemeProvider` (сервер + зеркало,
 * issue #953).
 */
export function ThemeToggle({ label }: {
  /** Имя группы — своё у каждого места: с общим именем проверки находили не тот выбор (ревью PR #1000). */
  label: string;
}) {
  const { theme, setTheme } = useTheme();
  return (
    <div role="group" aria-label={label}
      className="flex h-10 rounded-full border border-outline overflow-hidden">
      {THEME_OPTIONS.map(({ value, icon, label: optionLabel, hint }, i) => {
        const active = theme === value;
        return (
          <Tooltip key={value} content={hint ? `${optionLabel} — ${hint}` : optionLabel}>
            <button type="button" onClick={() => setTheme(value)} aria-label={optionLabel} aria-pressed={active}
              className={'flex-1 flex items-center justify-center gap-1 text-body-small state-layer focus-ring-inset ' +
                `${i > 0 ? 'border-l border-outline' : ''} ` +
                (active ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface-variant')}>
              {active && <Icon icon={Check} size={18} className="shrink-0" />}
              <Icon icon={icon} size={18} />
            </button>
          </Tooltip>
        );
      })}
    </div>
  );
}
