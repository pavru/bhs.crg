import type { ReactNode } from 'react';
import { ExternalLink, FileX, Globe, Lock, RefreshCw, FileQuestion, Cpu } from 'lucide-react';
import { Button } from '../Button';
import type { FailureKind } from './pdfEngine';
import { openInNewTab, type PdfBytes } from './openInNewTab';

/**
 * Состояния просмотрщика словами (issue #1490, [UI-17]). Отказ выглядит отказом, а не пустым листом,
 * и называет выход:
 * - «Открыть во вкладке» — где браузер справится сам (пароль, старый браузер, сбой просмотрщика);
 * - кнопки места (`actions`, у вида для чтения — «Оригинал») — где файл не откроет и браузер.
 *
 * Файл приложен и цел — это сказано там, где отказ не про файл: человек не должен прикладывать его снова.
 */

export type ViewerState = 'loading' | 'chunk' | 'old-browser' | FailureKind;

const TEXTS: Record<Exclude<ViewerState, 'loading'>, { head: string; text: string; icon: ReactNode; tab: boolean; place: boolean }> = {
  chunk: {
    head: 'Просмотрщик не загрузился',
    text: 'Приложение обновилось — обновите страницу. Файл приложен и цел.',
    icon: <RefreshCw size={20} />, tab: true, place: false,
  },
  'old-browser': {
    head: 'Браузер устарел для просмотра здесь',
    text: 'Нужен Chrome или Firefox 126 и новее, Safari 18 и новее. Во вкладке файл откроется.',
    icon: <Globe size={20} />, tab: true, place: false,
  },
  'not-pdf': {
    head: 'Файл не читается как PDF',
    text: 'Он повреждён. Скачайте оригинал.',
    icon: <FileX size={20} className="text-warning" />, tab: false, place: true,
  },
  password: {
    head: 'PDF защищён паролем',
    text: 'Здесь его не открыть — браузер спросит пароль во вкладке.',
    icon: <Lock size={20} />, tab: true, place: false,
  },
  'no-pages': {
    head: 'В файле нет страниц',
    text: '',
    icon: <FileQuestion size={20} />, tab: false, place: true,
  },
  worker: {
    head: 'Просмотрщик не запустился',
    text: 'Обновите страницу или откройте файл во вкладке. Файл приложен и цел.',
    icon: <Cpu size={20} />, tab: true, place: false,
  },
  broken: {
    head: 'Файл не показан',
    text: 'Просмотрщик не смог его разобрать. Во вкладке браузер попробует сам.',
    icon: <FileX size={20} className="text-warning" />, tab: true, place: true,
  },
};

export function ViewerMessage({ state, source, actions, loadingText = 'Файл загружается…' }: {
  state: ViewerState;
  /** Байты для «Открыть во вкладке»; без них кнопки нет. */
  source: PdfBytes | null;
  actions?: ReactNode;
  loadingText?: string;
}) {
  if (state === 'loading') {
    return <Centered><span className="text-sm text-on-surface-variant">{loadingText}</span></Centered>;
  }
  const t = TEXTS[state];
  return (
    <Centered>
      <div className="flex flex-col items-center gap-2 max-w-xs text-center" role="alert" data-testid="viewer-failure"
        data-state={state}>
        <span className="text-fg3">{t.icon}</span>
        <span className="text-sm font-medium text-on-surface">{t.head}</span>
        {t.text && <span className="text-xs text-on-surface-variant">{t.text}</span>}
        <div className="flex flex-wrap justify-center gap-2 mt-1">
          {(state === 'chunk' || state === 'worker') && (
            <Button size="sm" variant="tonal" icon={<RefreshCw size={13} />} onClick={() => window.location.reload()}>
              Обновить страницу
            </Button>
          )}
          {t.tab && source && (
            <Button size="sm" variant="outlined" icon={<ExternalLink size={13} />} onClick={() => openInNewTab(source)}>
              Открыть во вкладке
            </Button>
          )}
          {t.place && actions}
        </div>
      </div>
    </Centered>
  );
}

function Centered({ children }: { children: ReactNode }) {
  return <div className="h-full w-full flex items-center justify-center p-4 bg-surface-container-low">{children}</div>;
}
