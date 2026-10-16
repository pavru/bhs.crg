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
 *
 * `vertical` — сегменты столбиком, для свёрнутой боковой панели (issue #1355): в её 40 px три
 * сегмента в ряд стали бы по 13 px. Выбор тот же, меняется только раскладка.
 */
export function ThemeToggle({ label = 'Тема оформления', vertical = false }: { label?: string; vertical?: boolean }) {
  const { theme, setTheme } = useTheme();
  return (
    <div role="group" aria-label={label}
      className={`flex rounded-full border border-stroke-strong overflow-hidden ${vertical ? 'flex-col w-10' : 'h-10'}`}>
      {themeOptions.map(({ value, icon: Icon, label: optionLabel }, i) => {
        const active = theme === value;
        return (
          <button key={value} type="button" onClick={() => setTheme(value)}
            title={optionLabel} aria-label={optionLabel} aria-pressed={active}
            // В столбике — своя высота, а не flex-1: нулевая основа перебила бы её, и сегмент
            // сжался бы до иконки.
            className={`${vertical ? 'h-8' : 'flex-1'} flex items-center justify-center gap-1 text-xs transition-colors ` +
              `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand ` +
              `${i > 0 ? (vertical ? 'border-t' : 'border-l') + ' border-stroke-strong' : ''} ` +
              (active ? 'bg-tonal text-on-tonal' : 'text-fg3 hover:bg-black/5 dark:hover:bg-white/10')}>
            {active && !vertical && <Check size={14} className="shrink-0" />}
            <Icon size={16} />
          </button>
        );
      })}
    </div>
  );
}
