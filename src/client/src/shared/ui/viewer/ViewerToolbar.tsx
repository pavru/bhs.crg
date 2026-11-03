import { Add, ChevronLeft, ChevronRight, Remove, RotateRight } from '../icons';
import { useId, useState, type KeyboardEvent } from 'react';
import { IconButton } from '../Button';
import { Chip } from '../Chip';
import { Menu, MenuItem, MenuRadioGroup, MenuRadioItem, MenuSeparator } from '../Menu';
import { TextField } from '../TextField';
import { normalizePage, ZOOM_PRESETS, zoomLabel, type ZoomMode } from './viewerState';

/**
 * Панель просмотрщика — docked toolbar 64 у нижнего края области листов, одной плоскостью с холстом
 * (`surface-container`, без черты): граница видна там, где о панель обрезается лист (макет А «MD3
 * буквально», выбор владельца 03.11.2026; [UI-11]). Поверх листа она не плавает: закрывала бы низ
 * листа — печать и итоги счёта. Группы разделены промежутком 16, а не чертой.
 *
 * Уже 420 px (колонка счёта на 1366 — 340) `−` и `+` уходят в меню масштаба, а чип показывает
 * действующий масштаб в процентах: «По ширине» в узкую панель не входит. Режим тогда виден галочкой в
 * меню. `group`, а не `toolbar`: тот обещает переход стрелками, а стрелки заняты полем номера ([UI-19]).
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
  /** Действующий масштаб (1 = 100 %) — его показывает чип узкой панели. */
  scale: number;
}

export function ViewerToolbar(p: ToolbarProps) {
  return (
    // Высота 48: кнопки-иконки — 40 ([UI-68]), и при h-10 с border-t им осталось бы 39 px.
    <div role="group" aria-label="Просмотр файла" data-testid="viewer-toolbar"
      className="shrink-0 h-16 flex items-center gap-1 px-2 bg-surface-container">
      {p.numPages > 1 && (
        <>
          <IconButton label="Предыдущая страница" disabled={p.page <= 1} onClick={() => p.onPage(p.page - 1)} icon={ChevronLeft} />
          <PageField page={p.page} numPages={p.numPages} onPage={p.onPage} />
          <IconButton label="Следующая страница" disabled={p.page >= p.numPages} onClick={() => p.onPage(p.page + 1)} icon={ChevronRight} />
          <span aria-hidden className="w-2 shrink-0" />
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
 * Номер страницы — плотное поле 40 с числом страниц суффиксом «/ M» внутри: Enter переходит, Esc и уход
 * фокуса возвращают текущий, недопустимое молча возвращается к текущему. Подпись поля — для диктора,
 * а «/ M» он читает как «из M». Ширина — под три цифры номера и суффикс.
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
    <TextField label="Номер страницы" density="dense" containerClassName="w-20 shrink-0" aria-describedby={total}
      inputMode="numeric" value={editing ? text : String(page)} className="tabular-nums"
      onFocus={e => { setText(String(page)); setEditing(true); e.currentTarget.select(); }} onBlur={commit} onKeyDown={onKeyDown}
      onChange={e => { setText(e.target.value); setEditing(true); }}
      trailing={(
        <span className="text-body-medium text-on-surface-variant tabular-nums whitespace-nowrap pointer-events-none">
          <span aria-hidden>/ {numPages}</span><span id={total} className="sr-only">из {numPages}</span>
        </span>
      )} />
  );
}

/**
 * Меню масштаба по образцу `LayoutChooser`: чип с меню, открывается вверх — панель внизу. Режим —
 * выбор из вариантов: галочка у текущего ([UI-76]). `modal={false}` — листы под меню прокручиваются.
 * В узкой панели чип показывает процент, а имя для диктора называет и режим.
 */
function ZoomMenu(p: ToolbarProps) {
  const percent = `${Math.round(p.scale * 100)} %`;
  const shown = p.narrow ? percent : zoomLabel(p.mode);
  const spoken = p.narrow && typeof p.mode !== 'number' ? `${percent}, ${zoomLabel(p.mode).toLowerCase()}` : shown;
  const radio = (mode: ZoomMode, label: string, hint?: string) => (
    <MenuRadioItem key={String(mode)} value={String(mode)} trailing={hint} onSelect={() => p.onMode(mode)}>{label}</MenuRadioItem>
  );
  return (
    <Menu modal={false} dense side="top" sideOffset={6} width="min-w-48"
      trigger={<Chip selected={false} menu aria-label={`Масштаб: ${spoken}`} data-testid="viewer-zoom">{shown}</Chip>}>
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
