import { Icon } from './Icon';
import { CircularProgress } from './CircularProgress';
import { useGuardedIntent } from './NavigationGuard';
import { intentHref, type PaletteAction } from './paletteActions';

/**
 * Основное действие экрана на телефоне (issue #1434) — расширенная плавающая кнопка MD3 с подписью,
 * а не безымянный «+»: значок плюса не говорит, ЧТО появится.
 *
 * Кнопка не исполняет действие сама, а зовёт ту же кнопку экрана, что палитра (`?do=`,
 * `usePaletteIntent`): у действия один исполнитель — с тем же вопросом о правках и тем же выбором
 * файла. Экран — тот же, поэтому адрес дописывается, а не заменяется (`useGuardedIntent`).
 *
 * `busy` — кнопка экрана занята (`PrimaryActionButton` сообщает свой `loading`): спиннер и запрет, как
 * у неё на рабочем столе. Без этого загрузка шла бы незаметно, а повторное нажатие отвечало бы тостом
 * «экран занят» вместо индикатора (ревью PR #1502).
 */
export function PrimaryFab({ action, busy = false }: { action: PaletteAction; busy?: boolean }) {
  const toScreen = useGuardedIntent();
  const icon = action.icon;
  return (
    <button type="button" data-testid="primary-fab" onClick={() => toScreen(intentHref(action))}
      disabled={busy} aria-busy={busy || undefined}
      className="absolute right-4 bottom-4 z-30 flex items-center gap-3 h-14 max-w-[calc(100%-2rem)] pl-4 pr-5 rounded-lg bg-secondary-container text-on-secondary-container text-sm font-medium state-layer focus-ring shadow-elevation-3">
      {busy
        ? <CircularProgress size={24} className="shrink-0" />
        : <Icon icon={icon} size={24} className="shrink-0" aria-hidden />}
      <span className="truncate">{action.label}</span>
    </button>
  );
}
