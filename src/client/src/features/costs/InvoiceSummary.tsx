import { forwardRef, type ReactNode } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CheckCircle, Payments, Save, SearchCheck, type IconDef } from '@/shared/ui/icons';
import { Button, IconButton } from '@/shared/ui/Button';
import { fullTextIfCut } from '@/shared/ui/fullTextIfCut';
import type { PrimaryAction } from './invoicePrimary';

/**
 * Сводка счёта — единственная неподвижная часть формы (costs-invoice-screen-10-layout, D-10.6, ТЗ
 * COST-6.2). Держит «видно всегда» (ответ 3 листа 0): номер и пометки состояния, поставщика, дату и
 * сумму, носитель записи, одно основное действие и «⋮».
 *
 * <p>Высота — 64: подписи не переносятся, лишнее режется многоточием, полный текст обрезанного — подсказкой.
 * Всё остальное — в прокрутке под ней, поэтому поле, к которому ведёт Tab, под сводку не прячется.</p>
 *
 * <p>Места «‹ N из M ›», «Счёт из файла» и «Файл» — слоты: их наполняет страница, она знает список и место
 * (D-10.3, D-10.4).</p>
 *
 * <p>Ширину меряет сама сводка (`@container`), а не окно: рядом со списком и файлом форма бывает уже
 * 400 px. Тогда «Сохранить» и основное действие становятся значками с тем же именем — иначе кнопки
 * выходили за колонку, и соседняя панель ложилась поверх «Разобран».</p>
 */
export interface SummaryPrimary {
  kind: Exclude<PrimaryAction, null>;
  /** Почему неактивна; `null` — активна. */
  hold: string | null;
  loading?: boolean;
  onClick: () => void;
}

const PRIMARY: Record<SummaryPrimary['kind'], { label: string; icon: IconDef }> = {
  parsed: { label: 'Разобран', icon: CheckCircle },
  pay: { label: 'Отметить оплату…', icon: Payments },
  check: { label: 'Проверить исправление…', icon: SearchCheck },
};

export const InvoiceSummary = forwardRef<HTMLDivElement, {
  /** Номер счёта — как его называет сводка; пустой — «без номера». */
  number: string;
  /** Пометки состояния (`StatusText`). */
  states: ReactNode;
  /** Поставщик · дата · сумма. */
  meta: string;
  /** «‹ N из M ›». */
  nav?: ReactNode;
  /** Место индикатора состояния (ребёнок 11). */
  indicator?: ReactNode;
  /** Запись: `null` — записывать нечего и некому (заперт, исправление, нет права). */
  save: { unsaved: boolean; saving: boolean; savedAt: string; onSave: () => void } | null;
  fromFile?: ReactNode;
  file?: ReactNode;
  primary: SummaryPrimary | null;
  menu?: ReactNode;
}>(function InvoiceSummary({ number, states, meta, nav, indicator, save, fromFile, file, primary, menu }, ref) {
  const title = number ? `Счёт № ${number}` : 'Счёт без номера';
  const action = primary && PRIMARY[primary.kind];
  return (
    <div ref={ref} tabIndex={-1} role="group" aria-label={title} data-testid="invoice-header"
      className="@container h-16 shrink-0 min-w-0 border-b border-outline-variant bg-surface outline-none">
      <div className="h-full flex items-center gap-2 @3xl:gap-3 px-3 @3xl:px-4">
        {nav}
        <div className="flex-1 min-w-16 @3xl:min-w-32 flex flex-col justify-center">
          {/* Сжимаются сначала пометки состояния, номер — последним: по нему счёт узнают. */}
          <div className="flex items-center gap-2 min-w-0">
            <h2 className="shrink-0 max-w-full truncate text-title-medium text-on-surface"
              onPointerEnter={fullTextIfCut(title)}>
              {number || 'Без номера'}
            </h2>
            <div className="flex-1 min-w-0 flex items-center overflow-hidden">{states}</div>
          </div>
          <p className="truncate text-body-small text-on-surface-variant" onPointerEnter={fullTextIfCut(meta)}>{meta}</p>
        </div>
        {indicator}
        {save && (
          <Wide at="3xl">
            <Button variant="tonal" icon={<Icon icon={Save} size={18} />} disabled={!save.unsaved}
              loading={save.saving} onClick={save.onSave} title={save.unsaved ? undefined : `Всё сохранено · ${save.savedAt}`}>
              Сохранить
            </Button>
          </Wide>
        )}
        {save && (
          <Narrow at="3xl">
            <IconButton variant="tonal" icon={Save} label="Сохранить" disabled={!save.unsaved}
              loading={save.saving} onClick={save.onSave} title={save.unsaved ? undefined : `Всё сохранено · ${save.savedAt}`} />
          </Narrow>
        )}
        {fromFile}
        {file}
        {primary && action && (
          <Wide at="xl">
            <Button variant="filled" icon={<Icon icon={action.icon} size={18} />} disabled={primary.hold !== null}
              loading={primary.loading} onClick={primary.onClick} title={primary.hold ?? undefined}>
              {action.label}
            </Button>
          </Wide>
        )}
        {/* Причина неактивного значка — его подсказкой, как у кнопки с подписью (ревью PR #1786): строка
            готовности у сверки стоит в части «Строки», а та бывает свёрнута. Неактивный значок с подсказкой
            остаётся наводимым (`aria-disabled`, `Tooltip`). */}
        {primary && action && (
          <Narrow at="xl">
            <IconButton variant="filled" icon={action.icon} label={action.label} disabled={primary.hold !== null}
              loading={primary.loading} onClick={primary.onClick} title={primary.hold ?? undefined} />
          </Narrow>
        )}
        {menu}
      </div>
    </div>
  );
});

/**
 * Подпись кнопки — с ширины сводки `at`, уже — значок (`Narrow`). Основному действию подпись нужнее всего:
 * неактивный значок подсказки не показывает, а причину «Разобран» называет подсказка, — поэтому ему 576
 * (форма рядом с файлом на 1280 — 744), «Сохранить» — 768. Скрытая копия выпадает и из дерева доступности.
 */
const WIDE = { xl: 'hidden @xl:contents', '3xl': 'hidden @3xl:contents' } as const;
const NARROW = { xl: 'contents @xl:hidden', '3xl': 'contents @3xl:hidden' } as const;

function Wide({ at, children }: { at: keyof typeof WIDE; children: ReactNode }) {
  return <span className={WIDE[at]}>{children}</span>;
}

function Narrow({ at, children }: { at: keyof typeof NARROW; children: ReactNode }) {
  return <span className={NARROW[at]}>{children}</span>;
}
