import { useEffect, useEffectEvent, useRef } from 'react';
import { useLocation, useSearchParams } from 'react-router';
import { hasPermission, useAccess } from '@/shared/api/access';
import { INTENT_PARAM, type PaletteAction } from './paletteActions';

/**
 * Экран принимает действие командной палитры (issue #1433): адрес пришёл с `?do=<действие>` — экран
 * исполняет его своей кнопкой, `run`, ровно один раз.
 *
 * <p>Параметр снимается из адреса ЗАМЕНОЙ записи истории, а не новой: иначе обновление страницы или
 * «Назад» завели бы второй черновик. И снимается до исполнения: выбор файла открывается синхронно,
 * пока у браузера ещё жив жест пользователя (Enter в палитре), — позже браузер окно выбора не откроет.</p>
 *
 * <p>Права ждут загрузки: до неё кнопки на экране нет, и исполнять нечем. Права нет — параметр
 * снимается молча: палитра такого действия не предлагает, адрес набран руками.</p>
 */
export function usePaletteIntent(action: PaletteAction, run: () => void): void {
  const [params, setParams] = useSearchParams();
  const { key } = useLocation();
  const { data: access } = useAccess();
  const asked = params.get(INTENT_PARAM) === action.id;
  // Запись истории, по которой действие уже исполнено: строгий режим React зовёт эффект дважды, а
  // снятие параметра доходит до адреса не сразу.
  const done = useRef<string | null>(null);
  const execute = useEffectEvent(run);

  useEffect(() => {
    if (!asked || access === undefined || done.current === key) return;
    done.current = key;
    setParams(prev => {
      const next = new URLSearchParams(prev);
      next.delete(INTENT_PARAM);
      return next;
    }, { replace: true });
    if (hasPermission(access, action.permission)) execute();
  }, [asked, access, key, action.permission, setParams]);
}
