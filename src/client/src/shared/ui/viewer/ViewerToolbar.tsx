import { Add, ChevronLeft, ChevronRight, Remove, RotateRight } from '../icons';
import { useId, useState, type KeyboardEvent } from 'react';
import { IconButton } from '../Button';
import { Chip } from '../Chip';
import { Menu, MenuItem, MenuRadioGroup, MenuRadioItem, MenuSeparator } from '../Menu';
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
    // Высота 48: кнопки-иконки — 40 ([UI-68]), и при h-10 с border-t им осталось бы 39 px.
    <div role="group" aria-label="Просмотр файла" data-testid="viewer-toolbar"
      className="shrink-0 h-12 flex items-center gap-1 px-2 border-t border-outline-variant bg-surface">
      {p.numPages > 1 && (
        <>
          <IconButton label="Предыдущая страница" disabled={p.page <= 1} onClick={() => p.onPage(p.page - 1)} icon={ChevronLeft} />
          <PageField page={p.page} numPages={p.numPages} onPage={p.onPage} />
          <IconButton label="Следующая страница" disabled={p.page >= p.numPages} onClick={() => p.onPage(p.page + 1)} icon={ChevronRight} />
          <span aria-hidden className="w-px h-5 bg-outline-variant mx-1 shrink-0" />
        </>
      )}
      {!p.narrow && (
        <IconButton label="Уменьшить" title="Уменьшить (Ctrl −)" disabled={!p.canZoomOut} onClick={p.onZoomOut} icon={Remove} />
      )}
      <ZoomMenu {...p} />
      {!p.narrow && (
        <IconButton label="Увеличить" title="Увеличить (Ctrl +)" disabled={!p.canZoomIn} onClick={p.onZoomIn} icon={Add} />
      )}
      <span className="flex-1" />
      <IconButton label="Повернуть по часовой" onClick={p.onRotate} icon={RotateRight} />
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
    <span className="inline-flex items-center gap-1 text-body-small text-on-surface-variant whitespace-nowrap">
      <input aria-label="Номер страницы" aria-describedby={total} inputMode="numeric" value={editing ? text : String(page)}
        onFocus={e => { setText(String(page)); setEditing(true); e.currentTarget.select(); }} onBlur={commit} onKeyDown={onKeyDown}
        // Набор после Enter или Esc — фокус в поле остался, а onFocus второй раз не придёт: правка
        // начинается с первой клавиши, иначе поле показывало бы текущий номер, а копило невидимое.
        onChange={e => { setText(e.target.value); setEditing(true); }}
        className="w-[4ch] h-6 rounded-xs border border-outline-variant bg-surface text-center text-body-small text-on-surface tabular-nums focus:outline-none focus:border-primary" />
      <span id={total}>из {numPages}</span>
    </span>
  );
}

/**
 * Меню масштаба по образцу `LayoutChooser`: чип с меню, открывается вверх — панель внизу. Режим —
 * выбор из вариантов: галочка у текущего ([UI-76]). `modal={false}` — листы под меню прокручиваются.
 */
function ZoomMenu(p: ToolbarProps) {
  const radio = (mode: ZoomMode, label: string, hint?: string) => (
    <MenuRadioItem key={String(mode)} value={String(mode)} trailing={hint} onSelect={() => p.onMode(mode)}>{label}</MenuRadioItem>
  );
  return (
    <Menu modal={false} dense side="top" sideOffset={6} className="min-w-48"
      trigger={<Chip selected={false} menu aria-label={`Масштаб: ${zoomLabel(p.mode)}`} data-testid="viewer-zoom">{zoomLabel(p.mode)}</Chip>}>
      <MenuRadioGroup value={String(p.mode)}>
        {radio('width', 'По ширине', 'Ctrl 0')}
        {radio('page', 'Страница целиком')}
        <MenuSeparator />
        {ZOOM_PRESETS.map(z => radio(z, zoomLabel(z)))}
      </MenuRadioGroup>
      {p.narrow && (
        <>
          <MenuSeparator />
          <MenuItem icon={Remove} trailing="Ctrl −" disabled={!p.canZoomOut} onSelect={p.onZoomOut}>Уменьшить</MenuItem>
          <MenuItem icon={Add} trailing="Ctrl +" disabled={!p.canZoomIn} onSelect={p.onZoomIn}>Увеличить</MenuItem>
        </>
      )}
    </Menu>
  );
}
