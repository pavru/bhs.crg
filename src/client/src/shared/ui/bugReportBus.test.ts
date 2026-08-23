import { describe, expect, it, vi } from 'vitest';
import { canOpenBugReport, openBugReport, setBugReportOpener } from './bugReportBus';

/**
 * Общий вход к форме «Сообщить об ошибке» (issue #834).
 *
 * Что здесь закреплено: пока провайдер не подписался, дверь ЧЕСТНО отвечает «меня нет» — экран
 * сбоя на этом и решает, рисовать ли кнопку. Нарисованная кнопка, за которой пусто, — это отказ,
 * переодетый в результат: человек нажимает и не получает ничего, даже сообщения.
 *
 * Чего тест НЕ ловит и не может: порядка «синхронная регистрация против эффекта». Провайдер
 * регистрируется и при рендере, и в эффекте именно потому, что оба пути нужны, — и обнаружилось это
 * живой проверкой (сбой первого рендера случается до эффектов, а cleanup StrictMode снимал
 * синхронную запись). Здесь проверяется контракт шины, там — её сборка с React.
 */
describe('bugReportBus', () => {
  it('без подписчика дверь не обещает того, чего нет', () => {
    const off = setBugReportOpener(() => {});
    off();
    expect(canOpenBugReport()).toBe(false);
    // Вызов без подписчика молча ничего не делает — падать тут нечему.
    expect(() => openBugReport({ origin: 'toast' })).not.toThrow();
  });

  it('подписчик получает предзаполнение той двери, из которой пришли', () => {
    const open = vi.fn();
    const off = setBugReportOpener(open);
    try {
      expect(canOpenBugReport()).toBe(true);
      openBugReport({ origin: 'boundary', received: 'Внутренняя ошибка сервера', stack: 'at Foo' });
      expect(open).toHaveBeenCalledWith(
        { origin: 'boundary', received: 'Внутренняя ошибка сервера', stack: 'at Foo' });
    } finally { off(); }
  });

  it('отписка снимает только СВОЮ регистрацию', () => {
    const first = vi.fn();
    const second = vi.fn();
    const offFirst = setBugReportOpener(first);
    const offSecond = setBugReportOpener(second);
    // Провайдер регистрируется дважды (рендер + эффект), и cleanup первого прохода не должен
    // гасить дверь, открытую вторым: ровно на этом кнопка однажды перестала работать.
    offFirst();
    try {
      expect(canOpenBugReport()).toBe(true);
      openBugReport();
      expect(second).toHaveBeenCalled();
      expect(first).not.toHaveBeenCalled();
    } finally { offSecond(); }
  });
});
