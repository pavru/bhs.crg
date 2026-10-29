import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { Icon } from '../Icon';
import { Add, Check, ChevronLeft, ChevronRight, Remove, RotateRight } from '../icons';
import { useId, useState, type KeyboardEvent, type ReactNode } from 'react';
import { IconButton } from '../Button';
import { Chip } from '../Chip';
import { normalizePage, ZOOM_PRESETS, zoomLabel, type ZoomMode } from './viewerState';

/**
 * Панель просмотрщика — сплошной строкой у нижнего края области листов (вариант В макета
 * «Просмотрщик PDF: панель», выбор владельца 25.10.2026; [UI-11]). Поверх листа она не плавает:
 * закрывала бы текст и пряталась бы без наведения.
 *
 * Уже 420 px (колонка счёта бывает 340) `−` и `+` уходят в меню масштаба, страницы и поворот остаются.
 * `group`, а не `toolbar`: тот обещает переход стрелками, а стрелки заняты полем номера ([UI-19]).
 */

export const NARROW_PX = 420;

export interface ToolbarProps {
  numPages: number;
  page: number;
  onPage: (page: number) => void;
  mode: ZoomMode;
  onMode: (mode: ZoomMode) => void;
  onZoomIn: () => void;
  onZoomOut: () => void;
  canZoomIn: boolean;
  canZoomOut: boolean;
  onRotate: () => void;
  narrow: boolean;
}

export function ViewerToolbar(p: ToolbarProps) {
  return (
    <div role="group" aria-label="Просмотр файла" data-testid="viewer-toolbar"
      className="shrink-0 h-10 flex items-center gap-1 px-2 border-t border-outline-variant bg-surface">
      {p.numPages > 1 && (
        <>
          <IconButton label="Предыдущая страница" size="sm" disabled={p.page <= 1} onClick={() => p.onPage(p.page - 1)}>
            <Icon icon={ChevronLeft} size={18} />
          </IconButton>
          <PageField page={p.page} numPages={p.numPages} onPage={p.onPage} />
          <IconButton label="Следующая страница" size="sm" disabled={p.page >= p.numPages} onClick={() => p.onPage(p.page + 1)}>
            <Icon icon={ChevronRight} size={18} />
          </IconButton>
          <span aria-hidden className="w-px h-5 bg-outline-variant mx-1 shrink-0" />
        </>
      )}
      {!p.narrow && (
        <IconButton label="Уменьшить" title="Уменьшить (Ctrl −)" size="sm" disabled={!p.canZoomOut} onClick={p.onZoomOut}>
          <Icon icon={Remove} size={18} />
        </IconButton>
      )}
      <ZoomMenu {...p} />
      {!p.narrow && (
        <IconButton label="Увеличить" title="Увеличить (Ctrl +)" size="sm" disabled={!p.canZoomIn} onClick={p.onZoomIn}>
          <Icon icon={Add} size={18} />
        </IconButton>
      )}
      <span className="flex-1" />
      <IconButton label="Повернуть по часовой" size="sm" onClick={p.onRotate}>
        <Icon icon={RotateRight} size={18} />
      </IconButton>
    </div>
  );
}

/**
 * Номер страницы: Enter переходит, Esc и уход фокуса возвращают текущий, недопустимое молча
 * возвращается к текущему. Ширина — под три цифры: подпись над полем в панели лишняя.
 */
function PageField({ page, numPages, onPage }: { page: number; numPages: number; onPage: (p: number) => void }) {
  const total = useId();
  // Пока поле не в работе, оно показывает текущую страницу; набранное живёт только во время правки.
  const [text, setText] = useState(String(page));
  const [editing, setEditing] = useState(false);
  const commit = () => {
    const next = normalizePage(text, numPages);
    setEditing(false);
    setText(String(next ?? page));
    if (next !== null && next !== page) onPage(next);
  };
  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') { e.preventDefault(); commit(); }
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); setEditing(false); setText(String(page)); }
  };
  return (
    <span className="inline-flex items-center gap-1 text-xs text-on-surface-variant whitespace-nowrap">
      <input aria-label="Номер страницы" aria-describedby={total} inputMode="numeric" value={editing ? text : String(page)}
        onFocus={e => { setText(String(page)); setEditing(true); e.currentTarget.select(); }} onBlur={commit} onKeyDown={onKeyDown}
        // Набор после Enter или Esc — фокус в поле остался, а onFocus второй раз не придёт: правка
        // начинается с первой клавиши, иначе поле показывало бы текущий номер, а копило невидимое.
        onChange={e => { setText(e.target.value); setEditing(true); }}
        className="w-[4ch] h-6 rounded border border-outline-variant bg-surface text-center text-xs text-on-surface tabular-nums focus:outline-none focus:border-primary" />
      <span id={total}>из {numPages}</span>
    </span>
  );
}

const itemCls =
  'flex items-center justify-between gap-3 px-3 py-1.5 text-sm text-on-surface-variant cursor-pointer select-none outline-none ' +
  'state-layer disabled-content data-[disabled]:pointer-events-none';

/** Меню масштаба по образцу `LayoutChooser`: чип с меню, открывается вверх — панель внизу. */
function ZoomMenu(p: ToolbarProps) {
  const item = (key: string, label: ReactNode, onSelect: () => void, opts: { checked?: boolean; hint?: string; disabled?: boolean } = {}) => (
    <DropdownMenu.Item key={key} className={itemCls} onSelect={onSelect} disabled={opts.disabled}>
      <span className={opts.checked ? 'text-on-surface font-medium' : undefined}>{label}</span>
      {opts.checked ? <Icon icon={Check} size={18} className="text-primary" aria-hidden />
        : opts.hint && <span className="text-xs text-on-surface-variant">{opts.hint}</span>}
    </DropdownMenu.Item>
  );
  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger asChild>
        <Chip menu aria-label={`Масштаб: ${zoomLabel(p.mode)}`} data-testid="viewer-zoom">{zoomLabel(p.mode)}</Chip>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content side="top" align="start" sideOffset={6}
          className="z-50 min-w-48 rounded-md border border-outline-variant bg-surface py-1"
          style={{ boxShadow: 'var(--md-sys-elevation-level3)' }}>
          {item('width', 'По ширине', () => p.onMode('width'), { checked: p.mode === 'width', hint: 'Ctrl 0' })}
          {item('page', 'Страница целиком', () => p.onMode('page'), { checked: p.mode === 'page' })}
          <DropdownMenu.Separator className="my-1 h-px bg-outline-variant" />
          {ZOOM_PRESETS.map(z => item(String(z), zoomLabel(z), () => p.onMode(z), { checked: p.mode === z }))}
          {p.narrow && (
            <>
              <DropdownMenu.Separator className="my-1 h-px bg-outline-variant" />
              {item('out', 'Уменьшить', p.onZoomOut, { hint: 'Ctrl −', disabled: !p.canZoomOut })}
              {item('in', 'Увеличить', p.onZoomIn, { hint: 'Ctrl +', disabled: !p.canZoomIn })}
            </>
          )}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
