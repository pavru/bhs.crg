import { useId, useRef, useState, type DragEvent } from 'react';
import { formatBytes } from '@/shared/format/format';
import { useMedia } from '@/shared/hooks/useMedia';
import { Button } from './Button';
import { CircularProgress } from './CircularProgress';
import { carriesFiles, droppedFiles, useFileDragActive, zoneRefusal } from './fileDrop';
import { Icon } from './Icon';
import { Description, ErrorIcon, UploadFile } from './icons';

export interface FileDropZoneProps {
  /** Что за файл: «Файл сметы» — имя скрытого поля (e2e `getByLabel`) и описание кнопки для диктора. */
  label: string;
  /** Принятый файл — один; отказанный не отдаётся. */
  onFile: (file: File) => void;
  /**
   * Проверка экрана — одна для броска и окна выбора: причина без имени («файл пуст») или `null`. Вид,
   * пустоту и размер решает она, а не площадка (у счёта — тот же `precheck`, что у пакета, #1265).
   */
  check?: (file: File) => string | null;
  /** Только фильтр окна выбора (`<input accept>`): бросок по нему не судится. */
  accept?: string;
  /** Строка под кнопкой — вид и предел: «PDF, PNG и JPEG · до 50 МБ». Её диктор слышит на кнопке после `label`. */
  hint?: string;
  /** Выбранный файл: площадка свёрнута в строку и принимает бросок дальше — он заменяет выбранный. */
  file?: { name: string; size: number } | null;
  /** Заголовок покоя: «Перетащите файл счёта сюда». */
  prompt?: string;
  /** Подпись кнопки на телефоне, где перетаскивать нечем: «Выбрать файл сметы». */
  pickLabel?: string;
  /** Идёт загрузка — текст хода. Площадка файл не принимает ни броском, ни выбором. */
  busy?: string;
  /** Выключена; строка — причина вместо заголовка («Сначала выберите стройку»). */
  disabled?: boolean | string;
}

const COARSE = '(pointer: coarse)';

/**
 * Площадка выбора и броска одного файла ([UI-89], change `sharedui-file-drop-zone`). Вид — вариант Б
 * «Площадка» макета «Площадка файла · FileDropZone» (https://claude.ai/artifact/DkC1pS2r15uGtos1SS5m56),
 * выбор владельца 08.11.2026.
 *
 * - **Два пути — один исход.** Бросок и кнопка выбора отдают экрану один и тот же `File`; отказ у обоих тот
 *   же — `zoneRefusal`: папка → несколько → `check` экрана, «имя — причина» строкой под площадкой.
 * - **Своего захвата окна нет.** Обработчик площадки отменяет действие по умолчанию раньше стража окна
 *   (`useDropMissGuard`); цель на всё окно — другой механизм (`claimWindowDrop`, costs-invoice-screen-14).
 * - **«Над площадкой» — флаг, а не счётчик:** снимается выходом за край (`relatedTarget`), броском и концом
 *   перетаскивания где угодно в окне. Кнопка в «отпустите» скрыта, а не убрана: узел, убранный из-под
 *   курсора, `dragleave` не присылает, и «отпустите» залипало бы.
 * - **Не примет — не зовёт.** В загрузке и выключенной «наготове» не рисуется, а бросок получает «нельзя».
 * - **Диктору** «что за файл» и вид несёт описание кнопки (`aria-describedby` → `label`, `hint`): поле с
 *   `hidden` для него не существует. Именем `label` назван только сам `input` — второй элемент с тем же
 *   `aria-label` (например, группа площадки) сделал бы `getByLabel` неоднозначным. Отказ — `role="alert"`,
 *   пересоздаваемый каждой попыткой (тот же текст второй раз иначе не читается), выбранный файл и начало
 *   загрузки — живая область `polite`. Фокус никуда не переводится.
 */
export function FileDropZone({
  label, onFile, check, accept, hint, file, prompt = 'Перетащите файл сюда', pickLabel = 'Выбрать файл', busy, disabled,
}: FileDropZoneProps) {
  const input = useRef<HTMLInputElement>(null);
  const labelId = useId();
  const hintId = useId();
  const dragging = useFileDragActive();
  const coarse = useMedia(COARSE);
  const [over, setOver] = useState(false);
  const [refusal, setRefusal] = useState<{ text: string; attempt: number } | null>(null);

  const off = !!disabled;
  const accepting = !busy && !off;
  // Перетаскивание кончилось где угодно в окне (бросок мимо, Esc) — «отпустите» не залипает. Сброс — при
  // рендере по смене `dragging`, а не эффектом: эффект показал бы залипшее «отпустите» лишний кадр.
  const [wasDragging, setWasDragging] = useState(dragging);
  if (wasDragging !== dragging) {
    setWasDragging(dragging);
    if (!dragging) setOver(false);
  }
  const ready = accepting && dragging;
  const hover = accepting && over;

  const take = (dropped: { files: File[]; folders: string[] }) => {
    const refused = zoneRefusal(dropped, check);
    if (refused !== null) {
      setRefusal(r => ({ text: refused, attempt: (r?.attempt ?? 0) + 1 }));
      return;
    }
    if (dropped.files.length === 0) return;
    setRefusal(null);
    onFile(dropped.files[0]);
  };

  const onDragOver = (e: DragEvent) => {
    if (!carriesFiles(e.dataTransfer)) return;
    e.preventDefault();
    if (!accepting) { e.dataTransfer.dropEffect = 'none'; return; }
    e.dataTransfer.dropEffect = 'copy';
    setOver(true);
  };
  const onDragLeave = (e: DragEvent) => {
    if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOver(false);
  };
  const onDrop = (e: DragEvent) => {
    if (!carriesFiles(e.dataTransfer)) return;
    e.preventDefault();
    setOver(false);
    if (accepting) take(droppedFiles(e.dataTransfer));
  };

  const pick = (
    <Button variant={file && !busy ? 'text' : 'tonal'} disabled={!accepting} onClick={() => input.current?.click()}
      aria-describedby={hint ? `${labelId} ${hintId}` : labelId} className={hover ? 'invisible' : undefined}>
      {file && !busy ? 'Другой файл' : coarse ? pickLabel : 'Выбрать файл'}
    </Button>
  );
  const frame = hover ? 'border-2 border-solid border-primary bg-primary-container text-on-primary-container'
    : ready ? 'border-2 border-dashed border-primary'
    : null;
  const announced = busy ?? (file ? `Выбран файл ${file.name}, ${formatBytes(file.size)}` : '');
  const handlers = { onDragEnter: onDragOver, onDragOver, onDragLeave, onDrop };

  return (
    <div className="space-y-1">
      <span id={labelId} hidden>{label}</span>
      {file && !busy ? (
        <div {...handlers} data-disabled={off || undefined}
          className={`flex min-h-18 items-center gap-4 rounded-md py-3 pr-3 ${frame
            ? `${frame} pl-3.75`
            : 'border border-outline-variant bg-surface pl-4 disabled-outlined'}`}>
          <Icon icon={Description} size={24} aria-hidden className={`shrink-0 ${frame ? '' : 'text-on-surface-variant'}`} />
          <div className="min-w-0 flex-1">
            <p className="text-body-medium break-all">{file.name}</p>
            <p className={`text-body-small ${frame ? '' : 'text-on-surface-variant'}`}>{formatBytes(file.size)}</p>
          </div>
          {pick}
          {hint && <span id={hintId} hidden>{hint}</span>}
        </div>
      ) : (
        <div {...handlers} data-disabled={off || undefined}
          className={`flex flex-col items-center justify-center gap-2 rounded-md text-center ${coarse || (off && !busy) ? 'min-h-30' : 'min-h-46'}
            ${frame ? `${frame} p-5.75` : 'border border-dashed border-outline p-6 disabled-outlined'}`}>
          {busy ? <CircularProgress size={40} className="text-primary" />
            : !off && <Icon icon={UploadFile} size={40} aria-hidden className={hover ? '' : ready ? 'text-primary' : 'text-on-surface-variant'} />}
          {busy ? (
            <>
              <p className="text-title-medium text-on-surface">{busy}</p>
              <p className="text-body-small text-on-surface-variant">Бросок примем после неё</p>
            </>
          ) : off ? (
            typeof disabled === 'string' && <p className="text-title-medium">{disabled}</p>
          ) : !coarse && (
            <>
              <p className="text-title-medium">{hover ? 'Отпустите — возьмём файл' : ready ? 'Бросьте файл сюда' : prompt}</p>
              <p className={`text-body-medium ${hover ? 'invisible' : 'text-on-surface-variant'}`}>или</p>
            </>
          )}
          {pick}
          {hint && (
            <p id={hintId} className={`pt-1 text-body-small ${hover ? 'invisible' : off && !busy ? '' : 'text-on-surface-variant'}`}>{hint}</p>
          )}
        </div>
      )}
      {refusal && (
        <p key={refusal.attempt} role="alert" className="flex items-start gap-1 text-body-small text-error">
          <Icon icon={ErrorIcon} size={18} aria-hidden className="shrink-0" />
          <span className="min-w-0 break-all">{refusal.text}</span>
        </p>
      )}
      <span aria-live="polite" className="sr-only">{announced}</span>
      <input ref={input} type="file" hidden accept={accept} aria-label={label} disabled={!accepting}
        onChange={e => {
          const picked = e.target.files?.[0];
          // Сброс — чтобы тот же файл второй раз подряд снова давал событие (повтор после неудачи).
          e.target.value = '';
          if (picked) take({ files: [picked], folders: [] });
        }} />
    </div>
  );
}
