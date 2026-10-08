import { useEffect, useState } from 'react';

/**
 * Перетаскивание файлов в окно — общее для всех экранов (issue #1093).
 */

/** Тащат именно файлы, а не строку списка или выделенный текст. */
export const carriesFiles = (transfer: DataTransfer | null): boolean =>
  transfer !== null && [...transfer.types].includes('Files');

/**
 * Гасит промах: файл, отпущенный мимо цели, браузер ОТКРЫВАЕТ вместо страницы — и форма с
 * несохранёнными правками пропадает целиком. Ставится один раз, в корне приложения.
 *
 * <p>Цели это не мешает: её обработчик срабатывает раньше (React слушает на корне приложения, ниже
 * окна) и сам отменяет действие по умолчанию — такое событие здесь не трогается. Всё остальное окно
 * отвечает «сюда нельзя» — курсором, без рамок и надписей.</p>
 */
export function useDropMissGuard() {
  useEffect(() => {
    const guard = (e: DragEvent) => {
      if (e.defaultPrevented || !carriesFiles(e.dataTransfer)) return;
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = 'none';
    };
    window.addEventListener('dragover', guard);
    window.addEventListener('drop', guard);
    return () => {
      window.removeEventListener('dragover', guard);
      window.removeEventListener('drop', guard);
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
