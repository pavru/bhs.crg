import { useEffect, useRef, useState } from 'react';
import { FileUp, Maximize2, ScanLine } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { loadInvoiceScan } from '@/shared/api/invoices';

/**
 * Скан счёта РЯДОМ с формой (ТЗ COST-6.2, задача C1).
 *
 * <p>Рядом, а не в новой вкладке: человек сверяет форму с бумагой, и переключение вкладок на каждое
 * поле — это и есть та работа, от которой распознавание должно избавлять.</p>
 *
 * <p>⚠️ Документ показывается ЦЕЛИКОМ (решение владельца 29.09.2026, п. 17). Фрагмента с подсветкой
 * места в первой версии нет — и поэтому же отменена проверка «отдаёт ли профиль координаты полей»:
 * ответ ничего не менял бы.</p>
 */
export function InvoiceScanPanel({ invoiceId, fileName, mimeType }: {
  invoiceId: string;
  fileName: string | null;
  mimeType: string | null;
}) {
  const { url, failed } = useScan(invoiceId);

  if (failed) {
    return (
      <Note>
        Скан не загрузился. Он приложен к счёту — значит дело в связи или в хранилище, а не в самом
        файле: обновите страницу.
      </Note>
    );
  }

  if (!url) return <Note>Скан загружается…</Note>;

  const isPdf = (mimeType ?? '').includes('pdf');

  return (
    <div className="h-full flex flex-col min-h-0">
      <div className="flex items-center gap-2 px-3 py-2 border-b border-stroke shrink-0">
        <ScanLine size={14} className="text-fg3 shrink-0" />
        <span className="text-xs text-fg2 truncate flex-1">{fileName ?? 'Скан счёта'}</span>
        <Button size="sm" icon={<Maximize2 size={13} />} onClick={() => window.open(url, '_blank')}>
          Во весь экран
        </Button>
      </div>
      <div className="flex-1 min-h-0 bg-base">
        {isPdf
          ? <iframe src={url} title={fileName ?? 'Скан счёта'} className="w-full h-full border-0 bg-white" />
          : <img src={url} alt={fileName ?? 'Скан счёта'} className="w-full h-full object-contain" />}
      </div>
    </div>
  );
}

/**
 * Замена панели на узком экране — ЧЕСТНАЯ, с названной причиной (решение владельца 28.09.2026).
 *
 * Молчаливое исчезновение панели читается как «скана нет»: человек видит форму без бумаги и не знает,
 * то ли скан не приложен, то ли система его не показывает.
 */
export function ScanTooNarrow({ invoiceId, width }: { invoiceId: string; width: number }) {
  const [busy, setBusy] = useState(false);

  return (
    <div className="flex items-start gap-2 rounded-lg border border-stroke bg-surface2 px-3 py-2">
      <ScanLine size={14} className="text-fg3 shrink-0 mt-0.5" />
      <div className="min-w-0 flex-1">
        <p className="text-xs text-fg2">
          Скан приложен, но рядом с формой он не показывается: для двух панелей нужна ширина
          не меньше 1280 пикселей, а здесь {width}.
        </p>
        <Button size="sm" variant="text" loading={busy} icon={<Maximize2 size={13} />}
          onClick={async () => {
            setBusy(true);
            // Открываем ОТДЕЛЬНЫМ окном — это и есть замена панели: бумага остаётся доступной, просто
            // не рядом.
            try {
              const { url } = await loadInvoiceScan(invoiceId);
              window.open(url, '_blank');
            } finally { setBusy(false); }
          }}>
          Открыть скан отдельным окном
        </Button>
      </div>
    </div>
  );
}

/** Приложить или заменить скан. Кнопкой, а не полем формы: файл уезжает своим адресом. */
export function ScanUploadButton({ hasScan, busy, onPick }: {
  hasScan: boolean; busy: boolean; onPick: (file: File) => void;
}) {
  const input = useRef<HTMLInputElement>(null);

  return (
    <>
      <Button size="sm" variant="outlined" loading={busy} icon={<FileUp size={13} />}
        onClick={() => input.current?.click()}>
        {hasScan ? 'Заменить скан' : 'Приложить скан'}
      </Button>
      <input ref={input} type="file" className="hidden" accept="application/pdf,image/*"
        onChange={e => {
          const file = e.target.files?.[0];
          // Значение сбрасываем: иначе выбор ТОГО ЖЕ файла второй раз не вызовет события, и повтор
          // после неудачной загрузки выглядел бы как «кнопка не работает».
          e.target.value = '';
          if (file) onPick(file);
        }} />
    </>
  );
}

function Note({ children }: { children: React.ReactNode }) {
  return <div className="h-full grid place-items-center p-4 text-xs text-fg3 text-center">{children}</div>;
}

/**
 * Скан одним чтением, с освобождением объектной ссылки.
 *
 * ⚠️ `URL.revokeObjectURL` в уборке обязателен: без него каждое открытие счёта оставляет в памяти
 * страницы файл целиком, а замечают такое на сотом счёте у заказчика.
 */
function useScan(invoiceId: string) {
  const [loaded, setLoaded] = useState<{ id: string; url: string | null; failed: boolean } | null>(null);

  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;

    loadInvoiceScan(invoiceId)
      .then(({ url }) => {
        objectUrl = url;
        if (cancelled) { URL.revokeObjectURL(url); return; }
        setLoaded({ id: invoiceId, url, failed: false });
      })
      .catch(() => { if (!cancelled) setLoaded({ id: invoiceId, url: null, failed: true }); });

    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [invoiceId]);

  // Ответ считается своим только по совпадению счёта. Сбрасывать состояние в эффекте нельзя (лишний
  // кадр), а без сверки идентификатора при переключении счёта на миг показался бы ЧУЖОЙ скан —
  // человек решил бы, что бумага приложена не та.
  return loaded?.id === invoiceId ? loaded : { url: null, failed: false };
}
