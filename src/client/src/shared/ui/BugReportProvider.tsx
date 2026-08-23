import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { BugReportDialog, type BugReportPrefill } from './BugReportDialog';
import { BugReportCtx, setBugReportOpener, type BugReportApi } from './bugReportBus';

/**
 * Монтирует единственную форму «Сообщить об ошибке» и подписывает её на общий вход (issue #834).
 *
 * Стоит ВЫШЕ корневой границы ошибок: поймав сбой, граница размонтирует своих детей — окажись форма
 * внутри, кнопка пропала бы ровно на том экране, ради которого она и заведена.
 */
export function BugReportProvider({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const [prefill, setPrefill] = useState<BugReportPrefill>({});
  // Счётчик открытий = ключ формы: каждое открытие начинает сообщение заново, с предзаполнением
  // той двери, из которой пришли. Набранное не теряется случайно — Modal сторожит несохранённое
  // подтверждением при закрытии.
  const [openedTimes, setOpenedTimes] = useState(0);

  const openDialog = useCallback((next: BugReportPrefill = {}) => {
    setPrefill(next);
    setOpenedTimes(n => n + 1);
    setOpen(true);
  }, []);

  useEffect(() => setBugReportOpener(openDialog), [openDialog]);

  const api = useMemo<BugReportApi>(() => ({ open: openDialog }), [openDialog]);

  return (
    <BugReportCtx.Provider value={api}>
      {children}
      <BugReportDialog key={openedTimes} open={open} prefill={prefill} onClose={() => setOpen(false)} />
    </BugReportCtx.Provider>
  );
}
