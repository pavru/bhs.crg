import type { WindowDropOver } from '@/shared/ui/fileDrop';
import { ruCount } from '@/shared/utils/pluralize';
import type { ScanBatch } from './scanBatch';

/** Почему бросок сейчас не принимается — словами покрытия. */
function busyWords(batch: ScanBatch | null, checking: boolean): string {
  if (batch?.phase === 'checking' || checking) return 'Проверяем файлы — бросок примем после них';
  if (batch && batch.phase !== 'done' && batch.total > 0)
    return `Идёт загрузка ${Math.min(batch.settled + 1, batch.total)} из ${batch.total} — бросок примем после неё`;
  return 'Идёт загрузка файла — бросок примем после неё';
}

/**
 * Покрытие всего окна (D1): поверх оболочки, `pointer-events: none` — бросок ловит захват окна, а не оно.
 * Говорит, что будет с брошенным: один файл — свой черновик, несколько — пакет в фоне.
 */
export function DropCover({ over, opened, batch, checking, listShown }: {
  over: WindowDropOver; opened: { number: string | null; hasFile?: boolean } | null; batch: ScanBatch | null; checking: boolean;
  /** Виден ли список: где его нет, ход пакета — в центре активности (D5). */
  listShown: boolean;
}) {
  const busy = busyWords(batch, checking);
  const name = opened ? (opened.number ? `счёт ${opened.number}` : 'открытый счёт') : null;
  const lines = over.state === 'busy' ? [] : over.count > 1
    ? [`Экран останется на месте: пакет идёт в фоне, ход — в ${listShown ? 'полосе над списком' : 'центре активности'}.`,
      'Файлы, уже приложенные к счетам, не заведём — придержим в итоге пакета.']
    : [...(name ? [`Откроем новый черновик; ${name} не изменится.`] : []),
      'Если файл уже приложен к другому счёту — сначала спросим.',
      // Бросок счёт не меняет никогда (D1) — и покрытие говорит, где замена: в «⋮», с вопросом (D6).
      ...(opened?.hasFile ? ['Заменить файл этого счёта — «⋮ → Заменить файл…»'] : [])];
  return (
    <div className="fixed inset-0 z-50 p-2 pointer-events-none" data-testid="invoice-drop-cover" aria-hidden>
      <div className={`h-full rounded-lg border-2 border-dashed flex flex-col items-center justify-center gap-2 p-6 text-center ${
        over.state === 'busy' ? 'border-outline bg-surface-container-highest/90' : 'border-primary bg-primary-container/85'}`}>
        <p className={`text-title-large ${over.state === 'busy' ? 'text-on-surface' : 'text-on-primary-container'}`}>
          {over.state === 'busy' ? busy : over.count > 1
            ? `Отпустите — заведём ${ruCount(over.count, 'черновик', 'черновика', 'черновиков')}, по одному на файл`
            : 'Отпустите — заведём черновик из файла'}
        </p>
        {lines.map(line => <p key={line} className="text-body-medium text-on-primary-container">{line}</p>)}
      </div>
    </div>
  );
}
