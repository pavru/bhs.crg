import { useEffect, useEffectEvent, useRef } from 'react';
import { useLocation, useSearchParams } from 'react-router';
import { hasPermission, useAccess } from '@/shared/api/access';
import { INTENT_PARAM, type PaletteAction } from './paletteActions';
import { useToast } from './Toast';

/**
 * Экран принимает действие командной палитры (issue #1433): адрес пришёл с `?do=<действие>` — экран
 * исполняет его своей кнопкой, `run`, ровно один раз.
 *
 * <p>Параметр снимается из адреса ЗАМЕНОЙ записи истории, а не новой: иначе обновление страницы или
 * «Назад» завели бы второй черновик.</p>
 *
 * <p>⚠️ Исполняется в ДВА шага: сначала параметр снимается, и только на следующей отрисовке, когда
 * адреса с ним уже нет, зовётся `run` (ревью PR #1478). Сразу звать нельзя: `run` — функция экрана из
 * отрисовки, где параметр ещё стоял, и её `setSearchParams` строит адрес от того же снимка. Накладная
 * так и вернула `do` в адрес, открывая заведённый черновик, — и действие исполнилось снова: 297
 * черновиков за полминуты. Шаг укладывается в миллисекунды, и выбор файла открывается, пока у браузера
 * ещё жив жест пользователя (Enter в палитре).</p>
 *
 * <p>Права ждут загрузки: до неё кнопки на экране нет, и исполнять нечем. Права нет — параметр
 * снимается молча: палитра такого действия не предлагает, адрес набран руками.</p>
 *
 * <p>`busy` — кнопка сейчас закрыта (идёт загрузка, заводится черновик): действие не исполняется, о
 * чём сказано вслух (ревью PR #1478). Палитра — второй вход в ту же кнопку и не может принимать то,
 * чего не примет кнопка: вторая загрузка поверх первой, второй черновик от двойного Enter. Отложить
 * нельзя: выбор файла позже, без жеста пользователя, браузер не откроет.</p>
 */
export function usePaletteIntent(action: PaletteAction, run: () => void, { busy = false }: { busy?: boolean } = {}): void {
  const [params, setParams] = useSearchParams();
  const { key } = useLocation();
  const { data: access } = useAccess();
  const asked = params.get(INTENT_PARAM) === action.id;
  // Запись истории, по которой действие уже принято: строгий режим React зовёт эффект дважды, а
  // снятие параметра доходит до адреса не сразу.
  const done = useRef<string | null>(null);
  // Принято, но не исполнено: ждёт отрисовки без параметра.
  const pending = useRef(false);
  const toast = useToast();
  const execute = useEffectEvent(() => {
    if (busy) toast.info(`«${action.label}» не выполнено: экран ещё занят предыдущим. Повторите, когда оно закончится.`);
    else run();
  });

  useEffect(() => {
    if (asked) {
      if (access === undefined || done.current === key) return;
      done.current = key;
      pending.current = hasPermission(access, action.permission);
      setParams(prev => {
        const next = new URLSearchParams(prev);
        next.delete(INTENT_PARAM);
        return next;
      }, { replace: true });
      return;
    }
    if (!pending.current) return;
    pending.current = false;
    execute();
  }, [asked, access, key, action.permission, setParams]);
}
