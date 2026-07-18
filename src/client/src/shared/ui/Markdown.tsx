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
  const lines = children.replace(/\r\n/g, '\n').split('\n');
  const out: ReactNode[] = [];
  let para: string[] = [];
  let list: { ordered: boolean; items: string[] } | null = null;
  let k = 0;

  const flushPara = () => {
    if (!para.length) return;
    const p = para; const key = `p${k++}`;
    out.push(<p key={key}>{p.flatMap((l, i) =>
      i === 0 ? inline(l, `${key}-${i}`) : [<br key={`br${i}`} />, ...inline(l, `${key}-${i}`)])}</p>);
    para = [];
  };
  const flushList = () => {
    if (!list) return;
    const { ordered, items } = list; const key = `l${k++}`;
    out.push(ordered
      ? <ol key={key} className="list-decimal pl-5 space-y-0.5">{items.map((t, i) => <li key={i}>{inline(t, `${key}-${i}`)}</li>)}</ol>
      : <ul key={key} className="list-disc pl-5 space-y-0.5">{items.map((t, i) => <li key={i}>{inline(t, `${key}-${i}`)}</li>)}</ul>);
    list = null;
  };

  for (const line of lines) {
    if (line.trim() === '') { flushPara(); flushList(); continue; }
    const h = /^(#{1,3})\s+(.*)$/.exec(line);
    const ul = /^[-*]\s+(.*)$/.exec(line);
    const ol = /^\d+\.\s+(.*)$/.exec(line);
    if (h) {
      flushPara(); flushList();
      const lvl = h[1].length;
      const cls = lvl === 1 ? 'text-base font-semibold text-fg1'
        : lvl === 2 ? 'text-sm font-semibold text-fg1' : 'text-sm font-medium text-fg1';
      out.push(<p key={`h${k++}`} className={cls}>{inline(h[2], `h${k}`)}</p>);
    } else if (ul) {
      flushPara();
      if (list && !list.ordered) list.items.push(ul[1]);
      else { flushList(); list = { ordered: false, items: [ul[1]] }; }
    } else if (ol) {
      flushPara();
      if (list && list.ordered) list.items.push(ol[1]);
      else { flushList(); list = { ordered: true, items: [ol[1]] }; }
    } else {
      flushList(); para.push(line);
    }
  }
  flushPara(); flushList();

  return <div className={`text-sm text-fg2 space-y-2 leading-relaxed ${className}`}>{out}</div>;
}
