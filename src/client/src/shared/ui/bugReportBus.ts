import { createContext, useContext } from 'react';
import type { BugReportPrefill } from './BugReportDialog';

/**
 * Общий вход к форме «Сообщить об ошибке» (issue #834).
 *
 * Дверей к форме несколько: пункт боковой панели, кнопка на экране сбоя и действие на тосте с
 * ошибкой. Две последние живут вне React-контекста — `ErrorBoundary` это классовый компонент,
 * поймавший исключение, а тост рисуется где угодно. Поэтому кроме хука здесь есть модульная
 * функция: подписчик ровно один (провайдер смонтирован в App), и событие на window добавило бы
 * только посредника, прячущего, кто кого зовёт.
 *
 * Отдельным файлом от провайдера — чтобы модуль компонента экспортировал только компонент
 * (правило react-refresh).
 */
export interface BugReportApi {
  open: (prefill?: BugReportPrefill) => void;
}

export const BugReportCtx = createContext<BugReportApi | null>(null);

let openFromAnywhere: ((prefill: BugReportPrefill) => void) | null = null;

/** Подписка провайдера. Возвращает отписку. */
export function setBugReportOpener(open: (prefill: BugReportPrefill) => void): () => void {
  openFromAnywhere = open;
  return () => { if (openFromAnywhere === open) openFromAnywhere = null; };
}

/** Открыть форму из кода вне React-контекста (экран сбоя, тост). Без провайдера — молча ничего. */
export function openBugReport(prefill: BugReportPrefill = {}): void {
  openFromAnywhere?.(prefill);
}

/** Есть ли куда открывать: экран сбоя не рисует кнопку, если провайдера нет. */
export function canOpenBugReport(): boolean {
  return openFromAnywhere !== null;
}

/** Доступ к форме из React-дерева. Вне провайдера — тот же модульный вход. */
export function useBugReportDialog(): BugReportApi {
  return useContext(BugReportCtx) ?? { open: openBugReport };
}
