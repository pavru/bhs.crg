import { useTheme } from '@/shared/ui/themeContext';
import { THEME_OPTIONS } from '@/shared/ui/themeOptions';
import { SegmentedButton } from './SegmentedButton';

/**
 * Выбор темы — segmented button MD3 (issue #157, [UI-68]): радиогруппа, стрелка выбирает тему сразу,
 * выбранный сегмент — с галочкой. Сегмент — знак, подпись — имя для диктора и подсказка. Стоит в
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
    <SegmentedButton aria-label={label} value={theme} onChange={setTheme} fullWidth
      items={THEME_OPTIONS.map(({ value, icon, label: optionLabel, hint }) => ({
        value, icon, label: optionLabel, hideLabel: true, title: hint ? `${optionLabel} — ${hint}` : optionLabel,
      }))} />
  );
}
