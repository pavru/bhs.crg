import { createContext, useContext, useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { QK, useFreshInvoice } from '@/shared/api/invoices';
import { useInvoiceRecognition, type InvoiceRecognition } from '@/shared/api/invoiceRecognition';

/**
 * Слежение формы за распознаванием скана (issue #1077).
 *
 * <p>Пока скан читается, состояние опрашивается; на исходе счёт перечитывается — прочитанное уже
 * лежит в нём. Этот переход форма видела САМА, и потому правку распознаванием за чужую не принимает:
 * вид, пришедший этим перечитыванием, назван в `accepted`, и форма молча переносит на него свои
 * несохранённые правки. Набранное человеком остаётся на экране, прочитанное для этих полей
 * предлагается под ними (`fieldOffer`).</p>
 *
 * <p>⚠️ Чужая правка, попавшая в то же перечитывание, тоже окажется принятой — окно в один запрос.
 * Отличить её нечем: версия у счёта одна. Занятые поля распознавание не трогает, поэтому потерять
 * здесь можно только то, что чужая правка легла в поле, которое человек правит прямо сейчас.</p>
 *
 * @param enabled следить ли: у счёта без скана распознавать нечего.
 */
export function useRecognitionWatch(invoiceId: string, enabled: boolean) {
  const query = useInvoiceRecognition(invoiceId, enabled);
  const readFresh = useFreshInvoice();
  const qc = useQueryClient();
  const state = query.data?.state;
  const seen = useRef(state);
  const [accepted, setAccepted] = useState<string | null>(null);
  const [settling, setSettling] = useState(false);

  useEffect(() => {
    const before = seen.current;
    seen.current = state;
    if (before !== 'running' || state === undefined || state === 'running') return;

    // Без отмены в уборке: форма перемонтируется на каждый счёт, а уборка этого эффекта срабатывает и
    // на следующую смену состояния — отменённое перечитывание оставило бы «перечитывается» навсегда.
    setSettling(true);
    readFresh(invoiceId)
      .then(fresh => setAccepted(fresh.version))
      // Счёт не перечитался — форма осталась на прежнем виде; о новом она узнает обычным путём.
      .catch(() => {})
      .finally(() => setSettling(false));
    // Строка списка и число чипа «Не распознано» меняются тем же исходом.
    void qc.invalidateQueries({ queryKey: [QK, 'list'] });
    void qc.invalidateQueries({ queryKey: [QK, 'queues'] });
    // readFresh и qc стабильны по смыслу; перезапуск эффекта на каждый рендер оборвал бы перечитывание.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state, invoiceId]);

  return {
    /** Состояние распознавания; `undefined` — ещё не пришло или не спрашивали. */
    recognition: enabled ? query.data : undefined,
    /** Состояние не пришло: это не «не распознан». */
    failed: enabled && query.isError,
    retry: () => void query.refetch(),
    running: state === 'running',
    /** Версия счёта, пришедшая перечитыванием после исхода: её правки — распознавания, а не чужие. */
    accepted,
    /** Исход уже известен, счёт ещё перечитывается: полосу «устарело» показывать рано. */
    settling,
  };
}

/** Состояние распознавания для полей формы — чтобы не тянуть его через каждый блок. */
export const RecognitionContext = createContext<InvoiceRecognition | undefined>(undefined);

export const useRecognition = () => useContext(RecognitionContext);
