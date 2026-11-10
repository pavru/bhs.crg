import { forwardRef, type ReactNode } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CheckCircle, Edit, Payments, SearchCheck, Undo, type IconDef } from '@/shared/ui/icons';
import { Button, IconButton } from '@/shared/ui/Button';
import { fullTextIfCut } from '@/shared/ui/fullTextIfCut';
import type { PrimaryAction } from './invoicePrimary';
import type { SummarySave } from './invoiceSummarySave';
import { InvoiceSaveState } from './InvoiceSaveState';

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
 * 400 px. Тогда «Изменить», «Отменить правку» и основное действие становятся значками с тем же именем — иначе
 * кнопки выходили за колонку, и соседняя панель ложилась поверх «Разобран».</p>
 *
 * <p>Кнопки «Сохранить» в сводке нет ни у одного положения счёта (costs-invoice-screen-12-autosave, D-12.3): черновик
 * пишется сам, разобранный правится режимом «Изменить» и пишется «Разобран», оплаченный черновик — «Сохранить» в
 * «Сроках и оплате».</p>
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
  pay: { label: 'Внести оплату…', icon: Payments },
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
  /**
   * Запись (costs-invoice-screen-12-autosave, D-12.3) — см. `SummarySave`; `null` — записывать нечего и некому
   * (заперт, исправление, нет права).
   */
  save: SummarySave | null;
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
      <div className="h-full flex items-center gap-2 @4xl:gap-3 px-3 @4xl:px-4">
        {nav}
        <div className="flex-1 min-w-16 @4xl:min-w-32 flex flex-col justify-center">
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
        {save?.kind === 'carrier' && (
          <>
            <Wide at="4xl">
              <InvoiceSaveState status={save.status} savedAt={save.savedAt} onRetry={save.onRetry} pending={save.pending} />
            </Wide>
            <Narrow at="4xl">
              <InvoiceSaveState status={save.status} savedAt={save.savedAt} onRetry={save.onRetry} pending={save.pending} narrow />
            </Narrow>
          </>
        )}
        {save?.kind === 'carrier' && save.onCancel && (
          <>
            <Wide at="4xl"><Button variant="text" icon={<Icon icon={Undo} size={18} />} onClick={save.onCancel}>Отменить правку</Button></Wide>
            <Narrow at="4xl"><IconButton icon={Undo} label="Отменить правку" onClick={save.onCancel} /></Narrow>
          </>
        )}
        {save?.kind === 'read' && (
          <>
            <Wide at="4xl"><Button variant="outlined" icon={<Icon icon={Edit} size={18} />} onClick={save.onEdit}>Изменить</Button></Wide>
            <Narrow at="4xl"><IconButton variant="outlined" icon={Edit} label="Изменить" onClick={save.onEdit} /></Narrow>
          </>
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
 * это главное, что человек делает со счётом, и словом оно читается без наведения, — поэтому ему 576 (форма
 * рядом с файлом на 1280 — 744), «Сохранить» — 896: с индикатором состояния (11) сводка рядом с файлом на 1366 (797)
 * подпись «Сохранить» уже не вмещала, и основное действие уезжало под панель файла. Причину неактивного называет подсказка и у значка. Скрытая копия выпадает и из дерева доступности.
 */
const WIDE = { xl: 'hidden @xl:contents', '4xl': 'hidden @4xl:contents' } as const;
const NARROW = { xl: 'contents @xl:hidden', '4xl': 'contents @4xl:hidden' } as const;

function Wide({ at, children }: { at: keyof typeof WIDE; children: ReactNode }) {
  return <span className={WIDE[at]}>{children}</span>;
}

function Narrow({ at, children }: { at: keyof typeof NARROW; children: ReactNode }) {
  return <span className={NARROW[at]}>{children}</span>;
}
