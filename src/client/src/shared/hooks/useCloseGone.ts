import { useEffect, useLayoutEffect, useRef } from 'react';

/**
 * Закрыть запись, которой больше нет, — удалённую здесь или соседом (issue #1282, #1283).
 *
 * <p><b>Сначала адрес, потом кэш.</b> Запись, выброшенная из кэша при открытой форме, была бы тут же
 * запрошена заново и ответила бы «нет такой». Поэтому здесь только запоминается, что забыть, а
 * забывается оно, когда выбранной стала другая запись (или никакая).</p>
 *
 * <p><b>Кэш чистится при ЛЮБОМ уходе с такой записи</b> (ревью PR #1298, #1299), а не только по кнопке:
 * щелчок по другой строке или «назад» иначе оставили бы удалённое строкой списка. Для этого страница
 * говорит `gone` — «про выбранную запись уже известно, что её нет».</p>
 *
 * @param clear снять запись с адреса — ТОЛЬКО если в адресе всё ещё она: закрытие зовут и из
 *   отложенного кода (подтверждение удаления), и к тому времени выбрана может быть другая.
 * @param forget выбросить запись из кэша и перечитать списки.
 * @returns закрыть запись с этим идентификатором.
 */
export function useCloseGone(
  selected: string | null, gone: boolean, clear: (id: string) => void, forget: (id: string) => void,
) {
  const closing = useRef<string | null>(null);
  const forgetRef = useRef(forget);
  useLayoutEffect(() => { forgetRef.current = forget; });

  useEffect(() => { if (gone && selected) closing.current = selected; }, [gone, selected]);
  useEffect(() => {
    if (closing.current === null || closing.current === selected) return;
    forgetRef.current(closing.current);
    closing.current = null;
  }, [selected]);

  return (id: string) => {
    closing.current = id;
    clear(id);
  };
}
