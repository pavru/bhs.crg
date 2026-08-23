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

  // Регистрируем дверь ДВАЖДЫ, и оба раза по делу — найдено живой проверкой, тестами не ловится.
  //
  // Синхронно при рендере: сбой первого рендера приложения (белый экран сразу после загрузки — тот
  // самый случай, ради которого дверь на экране сбоя и заведена) происходит ДО того, как выполнятся
  // эффекты. Граница нарисовала бы панель, спросив canOpenBugReport() и получив «нет», и второй раз
  // она не перерисовывается: кнопки не было бы вовсе. Провайдер рендерится раньше детей, поэтому
  // синхронная запись успевает всегда.
  //
  // И в эффекте: в разработке React монтирует эффекты дважды (StrictMode), и cleanup первого прохода
  // снял бы синхронную регистрацию — кнопка рисовалась бы, а нажатие не делало НИЧЕГО. Ровно это и
  // случилось при первой проверке: дверь на месте, за дверью пусто.
  //
  // Запись идемпотентна и в состояние React не лезет: это модульная переменная, а не setState.
  setBugReportOpener(openDialog);
  useEffect(() => setBugReportOpener(openDialog), [openDialog]);

  const api = useMemo<BugReportApi>(() => ({ open: openDialog }), [openDialog]);

  return (
    <BugReportCtx.Provider value={api}>
      {children}
      <BugReportDialog key={openedTimes} open={open} prefill={prefill} onClose={() => setOpen(false)} />
    </BugReportCtx.Provider>
  );
}
