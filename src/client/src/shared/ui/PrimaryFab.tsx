import { useGuardedIntent } from './NavigationGuard';
import { intentHref, type PaletteAction } from './paletteActions';

/**
 * Основное действие экрана на телефоне (issue #1434) — расширенная плавающая кнопка MD3 с подписью,
 * а не безымянный «+»: значок плюса не говорит, ЧТО появится.
 *
 * Кнопка не исполняет действие сама, а зовёт ту же кнопку экрана, что палитра (`?do=`,
 * `usePaletteIntent`): у действия один исполнитель — с тем же вопросом о правках, той же занятостью и
 * тем же выбором файла. Экран — тот же, поэтому адрес дописывается, а не заменяется
 * (`useGuardedIntent`).
 */
export function PrimaryFab({ action }: { action: PaletteAction }) {
  const toScreen = useGuardedIntent();
  const Icon = action.icon;
  return (
    <button type="button" data-testid="primary-fab" onClick={() => toScreen(intentHref(action))}
      className="absolute right-4 bottom-4 z-30 flex items-center gap-3 h-14 max-w-[calc(100%-2rem)] pl-4 pr-5 rounded-2xl bg-tonal text-on-tonal text-sm font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand focus-visible:ring-offset-2 focus-visible:ring-offset-surface"
      style={{ boxShadow: 'var(--f-shadow28)' }}>
      <Icon size={22} className="shrink-0" aria-hidden />
      <span className="truncate">{action.label}</span>
    </button>
  );
}
