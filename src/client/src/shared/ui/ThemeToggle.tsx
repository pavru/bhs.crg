import { Check, Monitor, Moon, Sun } from 'lucide-react';
import { useTheme, type Theme } from '@/shared/ui/themeContext';

const themeOptions: { value: Theme; icon: typeof Sun; label: string }[] = [
  { value: 'light',  icon: Sun,     label: 'Светлая'   },
  { value: 'dark',   icon: Moon,    label: 'Тёмная'    },
  { value: 'system', icon: Monitor, label: 'Системная' },
];

/**
 * Выбор темы — MD3 segmented button (issue #157): выбранный сегмент tonal, с галочкой.
 *
 * ⚠️ Общий компонент, а не копия в каждом месте (issue #954). Выбор стоит в двух: в боковой панели,
 * где он под рукой, и в профиле, потому что тема — личная настройка и человек ищет её там
 * (ТЗ AUTH-16.6). Две реализации одного выбора разошлись бы молча — подписями, порядком, набором
 * значений, — а выглядели бы как одно и то же.
 *
 * Где значение хранится, компонент не знает: это забота `ThemeProvider` (сервер + зеркало,
 * issue #953).
 */
export function ThemeToggle({ label = 'Тема оформления' }: { label?: string }) {
  const { theme, setTheme } = useTheme();
  return (
    <div role="group" aria-label={label}
      className="flex h-10 rounded-full border border-stroke-strong overflow-hidden">
      {themeOptions.map(({ value, icon: Icon, label: optionLabel }, i) => {
        const active = theme === value;
        return (
          <button key={value} type="button" onClick={() => setTheme(value)}
            title={optionLabel} aria-label={optionLabel} aria-pressed={active}
            className={`flex-1 flex items-center justify-center gap-1 text-xs transition-colors ` +
              `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand ` +
              `${i > 0 ? 'border-l border-stroke-strong' : ''} ` +
              (active ? 'bg-tonal text-on-tonal' : 'text-fg3 hover:bg-black/5 dark:hover:bg-white/10')}>
            {active && <Check size={14} className="shrink-0" />}
            <Icon size={16} />
          </button>
        );
      })}
    </div>
  );
}
