import { type ReactNode } from 'react';

// Лёгкий безопасный markdown-рендерер (без внешних зависимостей и без innerHTML — строит React-узлы,
// поэтому текст экранируется React'ом). Поддерживает: заголовки решётками, маркированные и нумерованные
// списки, абзацы; инлайн — жирный, курсив, код, ссылки. Для справки типа документа (schema.help).

const INLINE = /(\*\*[^*]+\*\*|__[^_]+__|\*[^*\n]+\*|_[^_\n]+_|`[^`]+`|\[[^\]]+\]\([^)]+\))/g;

function safeHref(url: string): string | undefined {
  return /^(https?:|mailto:)/i.test(url.trim()) ? url.trim() : undefined;
}

function inline(text: string, kp: string): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  let m: RegExpExecArray | null;
  let i = 0;
  INLINE.lastIndex = 0;
  while ((m = INLINE.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    const tok = m[0];
    const k = `${kp}-${i++}`;
    if (tok.startsWith('**') || tok.startsWith('__')) out.push(<strong key={k}>{tok.slice(2, -2)}</strong>);
    else if (tok.startsWith('`')) out.push(
      <code key={k} className="font-mono bg-muted text-fg1 px-1 rounded text-[0.9em]">{tok.slice(1, -1)}</code>);
    else if (tok.startsWith('[')) {
      const mm = /^\[([^\]]+)\]\(([^)]+)\)$/.exec(tok)!;
      const href = safeHref(mm[2]);
      out.push(href
        ? <a key={k} href={href} target="_blank" rel="noreferrer" className="text-brand hover:text-brand-hover underline">{mm[1]}</a>
        : mm[1]);
    } else out.push(<em key={k}>{tok.slice(1, -1)}</em>);
    last = m.index + tok.length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

export function Markdown({ children, className = '' }: { children: string; className?: string }) {
  const blocks = children.replace(/\r\n/g, '\n').trim().split(/\n{2,}/);
  return (
    <div className={`text-sm text-fg2 space-y-2 leading-relaxed ${className}`}>
      {blocks.map((block, bi) => {
        const lines = block.split('\n');
        const h = /^(#{1,3})\s+(.*)$/.exec(lines[0]);
        if (h && lines.length === 1) {
          const lvl = h[1].length;
          const cls = lvl === 1 ? 'text-base font-semibold text-fg1'
            : lvl === 2 ? 'text-sm font-semibold text-fg1' : 'text-sm font-medium text-fg1';
          return <p key={bi} className={cls}>{inline(h[2], `h${bi}`)}</p>;
        }
        if (lines.every(l => /^[-*]\s+/.test(l))) {
          return (
            <ul key={bi} className="list-disc pl-5 space-y-0.5">
              {lines.map((l, li) => <li key={li}>{inline(l.replace(/^[-*]\s+/, ''), `u${bi}-${li}`)}</li>)}
            </ul>
          );
        }
        if (lines.every(l => /^\d+\.\s+/.test(l))) {
          return (
            <ol key={bi} className="list-decimal pl-5 space-y-0.5">
              {lines.map((l, li) => <li key={li}>{inline(l.replace(/^\d+\.\s+/, ''), `o${bi}-${li}`)}</li>)}
            </ol>
          );
        }
        return (
          <p key={bi}>
            {lines.flatMap((l, li) =>
              li === 0 ? inline(l, `p${bi}-${li}`) : [<br key={`br${li}`} />, ...inline(l, `p${bi}-${li}`)])}
          </p>
        );
      })}
    </div>
  );
}
