import { useEffect, useState } from 'react';
import { ruCount } from '@/shared/utils/pluralize';

/**
 * Перетаскивание файлов в окно — общее для всех экранов (issue #1093).
 */

/** Тащат именно файлы, а не строку списка или выделенный текст. */
export const carriesFiles = (transfer: DataTransfer | null): boolean =>
  transfer !== null && [...transfer.types].includes('Files');

/**
 * Захват броска окном страницы (costs-invoice-screen-14, D1): страница, у которой цель — всё окно (шапка и
 * рейл живут в оболочке, обернуть их нельзя), регистрирует захват, и страж отдаёт событие ему, а не
 * отклоняет. Захват один на окно: последний зарегистрированный.
 */
export interface WindowDropClaim {
  /** Принять сейчас (`accept`, курсор «копировать») или нет — идёт прежняя загрузка (`busy`, «нельзя»). */
  state: () => 'accept' | 'busy';
  onFiles: (files: File[], folders: string[]) => void;
  /** Над окном тащат файлы: сколько и что с ними будет; `null` — перестали, бросили или открыт слой. */
  onOver: (over: WindowDropOver | null) => void;
}

export interface WindowDropOver {
  count: number;
  state: 'accept' | 'busy';
}

let claim: WindowDropClaim | null = null;

/** Зарегистрировать захват; ответ снимает его (только свой — не сменивший его следующий). */
export function claimWindowDrop(next: WindowDropClaim): () => void {
  claim = next;
  return () => { if (claim === next) { claim.onOver(null); claim = null; } };
}

/**
 * Открыт модальный слой — окно Radix (`role="dialog"` с `data-state="open"`), не всплывающая панель: у той
 * тот же `role`, но она внутри обёртки поппера. Пока слой открыт, захват молчит — слой важнее (D1).
 */
export const modalLayerOpen = (): boolean =>
  [...document.querySelectorAll('[role="dialog"][data-state="open"], [role="alertdialog"][data-state="open"]')]
    .some(el => !el.closest('[data-radix-popper-content-wrapper]'));

/**
 * Гасит промах: файл, отпущенный мимо цели, браузер ОТКРЫВАЕТ вместо страницы — и форма с
 * несохранёнными правками пропадает целиком. Ставится один раз, в корне приложения.
 *
 * <p>Цели это не мешает: её обработчик срабатывает раньше (React слушает на корне приложения, ниже
 * окна) и сам отменяет действие по умолчанию — такое событие здесь не трогается. Есть захват окна
 * ({@link claimWindowDrop}) и не открыт модальный слой — событие отдаётся ему. Всё остальное окно
 * отвечает «сюда нельзя» — курсором, без рамок и надписей.</p>
 */
export function useDropMissGuard() {
  useEffect(() => {
    // Счётчик входов, а не точка за краем окна (ревью PR #1819): отмена Esc и уход из окна в Firefox приходят
    // `dragleave` с точкой внутри окна, а из проводника `dragend` окну не приходит вовсе — покрытие залипало бы.
    let depth = 0;
    // Модальный слой — один раз на вход, а не на каждый `dragover` (их несколько в секунду, ревью PR #1823).
    // На самом броске он смотрится заново.
    let layer: boolean | null = null;
    const guard = (e: DragEvent) => {
      if (e.defaultPrevented || !carriesFiles(e.dataTransfer)) return;
      e.preventDefault();
      const dropping = e.type === 'drop';
      if (dropping) depth = 0;
      const blocked = dropping || layer === null ? modalLayerOpen() : layer;
      const taker = claim && !blocked ? claim : null;
      if (!taker) {
        claim?.onOver(null);
        if (e.dataTransfer) e.dataTransfer.dropEffect = 'none';
        return;
      }
      const state = taker.state();
      if (e.dataTransfer) e.dataTransfer.dropEffect = state === 'accept' ? 'copy' : 'none';
      if (!dropping) { taker.onOver({ count: e.dataTransfer?.items.length ?? 1, state }); return; }
      taker.onOver(null);
      if (state !== 'accept' || !e.dataTransfer) return;
      const { files, folders } = droppedFiles(e.dataTransfer);
      if (files.length + folders.length > 0) taker.onFiles(files, folders);
    };
    const enter = (e: DragEvent) => {
      if (!carriesFiles(e.dataTransfer)) return;
      depth++;
      layer = modalLayerOpen();
    };
    // Покрытие гаснет и тогда, когда файл увели из окна или перетаскивание отменили.
    const end = () => { depth = 0; layer = null; claim?.onOver(null); };
    const leave = (e: DragEvent) => {
      if (!carriesFiles(e.dataTransfer)) return;
      depth = Math.max(0, depth - 1);
      if (depth === 0) end();
    };
    window.addEventListener('dragenter', enter);
    window.addEventListener('dragover', guard);
    window.addEventListener('drop', guard);
    window.addEventListener('dragend', end);
    window.addEventListener('dragleave', leave);
    return () => {
      window.removeEventListener('dragenter', enter);
      window.removeEventListener('dragover', guard);
      window.removeEventListener('drop', guard);
      window.removeEventListener('dragend', end);
      window.removeEventListener('dragleave', leave);
    };
  }, []);
}

/**
 * Над окном тащат файл — цели показывают, что они наготове. Попасть помогает не размер цели, а то,
 * что её видно ещё до того, как курсор до неё дошёл.
 */
export function useFileDragActive(): boolean {
  const [active, setActive] = useState(false);
  useEffect(() => {
    // Счётчик, а не флаг: вход в дочерний элемент приходит раньше выхода из родителя.
    let depth = 0;
    const enter = (e: DragEvent) => { if (carriesFiles(e.dataTransfer)) { depth++; setActive(true); } };
    const leave = (e: DragEvent) => {
      if (!carriesFiles(e.dataTransfer)) return;
      depth = Math.max(0, depth - 1);
      if (depth === 0) setActive(false);
    };
    const end = () => { depth = 0; setActive(false); };
    window.addEventListener('dragenter', enter);
    window.addEventListener('dragleave', leave);
    window.addEventListener('drop', end);
    window.addEventListener('dragend', end);
    return () => {
      window.removeEventListener('dragenter', enter);
      window.removeEventListener('dragleave', leave);
      window.removeEventListener('drop', end);
      window.removeEventListener('dragend', end);
    };
  }, []);
  return active;
}

/**
 * Что бросили: файлы и — отдельно — папки. Папка приходит в `files` записью нулевого размера без
 * вида, неотличимой от пустого файла; внутрь не заходим, но и молча не теряем — её называют по имени.
 */
export function droppedFiles(transfer: DataTransfer): { files: File[]; folders: string[] } {
  const files: File[] = [];
  const folders: string[] = [];
  const items = [...transfer.items].filter(i => i.kind === 'file');
  items.forEach(item => {
    const file = item.getAsFile();
    if (item.webkitGetAsEntry?.()?.isDirectory) folders.push(file?.name ?? 'папка');
    else if (file) files.push(file);
  });
  // Браузер без списка элементов — берём файлы как есть.
  return items.length > 0 ? { files, folders } : { files: [...transfer.files], folders };
}

/** Слова отказа папке — одни у пакета сканов (`scanBatch`) и у площадки файла (`FileDropZone`). */
export const FOLDER_REFUSAL = 'это папка — перетащите файлы из неё';

/** То же нескольким папкам сразу. */
export const FOLDERS_REFUSAL = 'это папки — перетащите файлы из них';

/** Слова отказа нескольким файлам там, где ждут один. */
export const SEVERAL_REFUSAL = 'здесь один файл: перетащите их по одному';

/**
 * Отказ площадки одного файла ([UI-89]) — «имя — причина» или `null`, если файл отдаётся экрану.
 *
 * <p>Порядок — папка → несколько → проверка экрана; называется первая причина, а у папок — все папки броска. Сама площадка судит только
 * то, что видно до файла: вид, пустоту и размер решает `check` экрана (у счёта — тот же `precheck`, что у
 * пакета, #1265). Поэтому `check` зовётся только для одного файла и никогда — для папки.</p>
 */
export function zoneRefusal(
  dropped: { files: readonly File[]; folders: readonly string[] },
  check?: (file: File) => string | null,
): string | null {
  const { files, folders } = dropped;
  // Папки называются все: отказ, назвавший одну, молча потерял бы остальные.
  if (folders.length > 0) return `${folders.join(', ')} — ${folders.length > 1 ? FOLDERS_REFUSAL : FOLDER_REFUSAL}`;
  if (files.length > 1) return `${ruCount(files.length, 'файл', 'файла', 'файлов')} — ${SEVERAL_REFUSAL}`;
  if (files.length === 0 || !check) return null;
  const reason = check(files[0]);
  return reason === null ? null : `${files[0].name} — ${reason}`;
}
