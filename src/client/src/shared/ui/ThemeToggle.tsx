import { Check } from 'lucide-react';
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
      className="flex h-10 rounded-full border border-stroke-strong overflow-hidden">
      {THEME_OPTIONS.map(({ value, icon: Icon, label: optionLabel, hint }, i) => {
        const active = theme === value;
        return (
          <Tooltip key={value} content={hint ? `${optionLabel} — ${hint}` : optionLabel}>
            <button type="button" onClick={() => setTheme(value)} aria-label={optionLabel} aria-pressed={active}
              className={'flex-1 flex items-center justify-center gap-1 text-xs transition-colors ' +
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand ' +
                `${i > 0 ? 'border-l border-stroke-strong' : ''} ` +
                (active ? 'bg-tonal text-on-tonal' : 'text-fg3 hover:bg-black/5 dark:hover:bg-white/10')}>
              {active && <Check size={14} className="shrink-0" />}
              <Icon size={16} />
            </button>
          </Tooltip>
        );
      })}
    </div>
  );
}
