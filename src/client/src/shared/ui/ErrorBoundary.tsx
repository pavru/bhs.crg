import { Component, type ErrorInfo, type ReactNode } from 'react';
import { AlertTriangle, RotateCcw, RefreshCw, ChevronDown, ChevronRight } from 'lucide-react';

/**
 * React error boundary (issue #305). В приложении их не было вовсе — а в React 19 любой throw при
 * рендере, не пойманный границей, РАЗМОНТИРУЕТ всё дерево → немой белый экран без следа в UI
 * (симптом «пустой экран при открытии документа»). Граница ловит throw, показывает читаемую панель
 * с текстом ошибки + сворачиваемым стеком и даёт восстановиться, не перезагружая страницу.
 *
 * `resetKeys` — при их изменении граница сбрасывается (напр. сменили документ/маршрут → пробуем
 * рендер заново). `variant="inline"` — компактная панель для контента модалки (не полноэкранная).
 */
export class ErrorBoundary extends Component<
  {
    children: ReactNode;
    /** Заголовок панели (по умолчанию — общий). */
    title?: string;
    /** Меняется — граница сбрасывает пойманную ошибку и пробует отрисовать детей снова. */
    resetKeys?: unknown[];
    /** inline — компактная панель (для модалок/секций); page — полноэкранная (корень app). */
    variant?: 'inline' | 'page';
    /** Показать кнопку «Перезагрузить страницу» (для корневой границы). */
    allowReload?: boolean;
  },
  { error: Error | null; expanded: boolean }
> {
  state = { error: null as Error | null, expanded: false };

  static getDerivedStateFromError(error: Error) {
    return { error, expanded: false };
  }

  componentDidUpdate(prev: Readonly<{ resetKeys?: unknown[] }>) {
    // Смена resetKeys при активной ошибке → сброс (даём поддереву шанс отрисоваться на новых данных).
    if (this.state.error && !shallowEqualArr(prev.resetKeys, this.props.resetKeys)) {
      this.setState({ error: null, expanded: false });
    }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // Лог в консоль — чтобы стек попал в devtools даже когда панель свёрнута.
    console.error('[ErrorBoundary] пойман сбой рендера:', error, info.componentStack);
  }

  reset = () => this.setState({ error: null, expanded: false });

  render() {
    const { error, expanded } = this.state;
    if (!error) return this.props.children;

    const { title = 'Что-то пошло не так', variant = 'inline', allowReload } = this.props;
    const isPage = variant === 'page';

    return (
      <div className={isPage
        ? 'min-h-screen flex items-center justify-center p-6 bg-base'
        : 'flex-1 min-h-0 flex items-center justify-center p-6'}>
        <div className="max-w-lg w-full rounded-xl border border-danger/30 bg-danger-subtle/40 p-5">
          <div className="flex items-start gap-3">
            <AlertTriangle size={20} className="text-danger shrink-0 mt-0.5" />
            <div className="min-w-0 flex-1">
              <h2 className="text-base font-medium text-fg1">{title}</h2>
              <p className="text-sm text-fg3 mt-1">
                Произошла ошибка при отображении. Данные не потеряны — можно попробовать снова или
                перезагрузить страницу.
              </p>

              <div className="flex flex-wrap items-center gap-2 mt-3">
                <button type="button" onClick={this.reset}
                  className="inline-flex items-center gap-1.5 h-8 px-3 rounded-lg text-sm font-medium bg-brand text-white hover:bg-brand-hover transition-colors">
                  <RotateCcw size={14} /> Попробовать снова
                </button>
                {allowReload && (
                  <button type="button" onClick={() => window.location.reload()}
                    className="inline-flex items-center gap-1.5 h-8 px-3 rounded-lg text-sm font-medium border border-stroke text-fg2 hover:bg-base transition-colors">
                    <RefreshCw size={14} /> Перезагрузить страницу
                  </button>
                )}
              </div>

              <button type="button" onClick={() => this.setState({ expanded: !expanded })}
                className="inline-flex items-center gap-1 mt-3 text-xs text-fg4 hover:text-fg2 transition-colors">
                {expanded ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
                Подробности ошибки
              </button>
              {expanded && (
                <pre className="mt-2 max-h-60 overflow-auto rounded-lg bg-black/5 dark:bg-white/5 p-3 text-[11px] leading-relaxed text-fg3 whitespace-pre-wrap break-words">
                  {error.message}{error.stack ? `\n\n${error.stack}` : ''}
                </pre>
              )}
            </div>
          </div>
        </div>
      </div>
    );
  }
}

function shallowEqualArr(a?: unknown[], b?: unknown[]): boolean {
  if (a === b) return true;
  if (!a || !b || a.length !== b.length) return false;
  return a.every((v, i) => Object.is(v, b[i]));
}
