import type { ReactNode } from 'react';
import { Button } from './Button';
import { Icon } from './Icon';
import { CheckCircle, ErrorIcon, Info, Warning, type IconDef } from './icons';

/**
 * Сообщение области — banner ([UI-73]): состояние страницы, панели или окна, пока оно длится (данные
 * устарели, перечитать не удалось, идёт исправление). Стоит во всю ширину в начале своей области, над
 * содержимым, — не плавающий слой. Результат действия вне экрана — snackbar, одно поле — текст под ним,
 * решение до продолжения — окно (docs/UI.md, «что сказать — чем»).
 *
 * <p>Banner — расширение (Material 2, D8 зонтика `sharedui-md3-00-epic`): в MD3 его нет, а плашка в рамке,
 * которой его заменяли, у каждого экрана была своя. Вид — `surface`, знак 24 px `on-<тон>-container` в
 * круге 40 px `<тон>-container` (роль на своём контейнере запрещает `container-text`), текст
 * `body-medium`, до двух text-действий справа, разделитель `outline-variant` снизу.</p>
 *
 * <p>Узкая область — действия под текстом: ширину меряет сама область (`@container`), а не экран —
 * banner в панели list-detail на рабочем столе узок так же, как на телефоне. Закрыть banner можно, только
 * если сообщение снимаемое: «Понятно» — одно из действий, а не крестик.</p>
 */

export type BannerTone = 'error' | 'warning' | 'info' | 'success';

const TONE: Record<BannerTone, { circle: string; icon: IconDef }> = {
  error: { circle: 'bg-error-container text-on-error-container', icon: ErrorIcon },
  warning: { circle: 'bg-warning-container text-on-warning-container', icon: Warning },
  info: { circle: 'bg-primary-container text-on-primary-container', icon: Info },
  success: { circle: 'bg-success-container text-on-success-container', icon: CheckCircle },
};

export interface BannerAction {
  label: string;
  onClick: () => void;
  icon?: IconDef;
  /** Действие ждёт ответа — кнопка с индикатором хода, повторно не нажимается. */
  loading?: boolean;
}

interface BannerProps {
  tone: BannerTone;
  /** Свой знак вместо знака тона — когда смысл точнее («нет связи»), тон тот же. */
  icon?: IconDef;
  /** До двух text-действий; больше — не banner, а окно или экран. */
  actions?: readonly BannerAction[];
  /**
   * Срочное: текст — `alert`, а не `status`. Banner обычно появляется сразу с текстом, а такую вставку
   * `status` дикторы пропускают молча — работающий на прежних данных о них не узнал бы. По умолчанию —
   * у тона `error`.
   */
  urgent?: boolean;
  className?: string;
  'data-testid'?: string;
  children: ReactNode;
}

export function Banner({ tone, icon, actions = [], urgent = tone === 'error', className = '', 'data-testid': testId, children }: BannerProps) {
  const t = TONE[tone];
  return (
    <div data-testid={testId} className={`@container shrink-0 bg-surface border-b border-outline-variant ${className}`}>
      <div className="flex flex-col gap-2 px-4 py-3 @lg:flex-row @lg:items-center @lg:gap-4">
        <div className="flex flex-1 items-start gap-4">
          <span className={`shrink-0 grid place-items-center size-10 rounded-full ${t.circle}`}>
            <Icon icon={icon ?? t.icon} size={24} />
          </span>
          <p role={urgent ? 'alert' : 'status'} className="flex-1 self-center text-body-medium text-on-surface">{children}</p>
        </div>
        {actions.length > 0 && (
          <div className="flex shrink-0 justify-end gap-2">
            {actions.slice(0, 2).map(a => (
              <Button key={a.label} variant="text" loading={a.loading} onClick={a.onClick}
                icon={a.icon && <Icon icon={a.icon} size={18} />}>
                {a.label}
              </Button>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
