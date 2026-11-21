import tokens from '@/theme/md3-tokens.css?raw';

const aliases: Record<string, string> = {
  scl0: 'surface-container-lowest', scl: 'surface-container-low', sc: 'surface-container', sch: 'surface-container-high', schh: 'surface-container-highest',
  'surface-low': 'surface-container-low', 'surface-high': 'surface-container-high',
  'text': 'on-surface', muted: 'on-surface-variant', bg: 'surface', paper: 'surface',
};
export function applyMockupTheme(theme: 'light' | 'dark') {
  const style = document.createElement('style'); style.dataset.liveTheme = '';
  const roles = [...tokens.matchAll(/--md-(?:sys|ext)-color-([\w-]+):/g)].map(match => match[1]);
  const system = new Set([...tokens.matchAll(/--md-sys-color-([\w-]+):/g)].map(match => match[1]));
  const aliasesCss = [...new Set(roles)].map(role => `--${role}:var(--md-${system.has(role) ? 'sys' : 'ext'}-color-${role})!important;`).join('') + Object.entries(aliases).map(([alias, role]) => `--${alias}:var(--md-sys-color-${role})!important;`).join('');
  const light = tokens.match(/:root\s*\{([^}]+)\}/)?.[1] ?? '';
  const dark = tokens.match(/:root\[data-theme="dark"\]\s*\{([^}]+)\}/)?.[1] ?? '';
  style.textContent = `${tokens}\n:root{${aliasesCss};--font-plain:var(--md-ref-typeface-plain);font-family:var(--md-ref-typeface-plain)}
    .light,[data-theme="light"],.md3.light{${light}${aliasesCss}}
    .dark,[data-theme="dark"],.md3.dark{${dark}${aliasesCss}}
    .md3,.app,.screen,.frame{${aliasesCss}}
    body{margin:0;background:var(--md-sys-color-surface);color:var(--md-sys-color-on-surface);font-family:var(--md-ref-typeface-plain)}
    .mockup-live-note{position:relative;padding:10px 20px;background:var(--md-sys-color-surface-container-low);color:var(--md-sys-color-on-surface-variant);font:400 12px/18px var(--md-ref-typeface-plain);border-bottom:1px solid var(--md-sys-color-outline-variant)}
    [data-live-control]{box-sizing:border-box}[data-live-control=Checkbox]{min-width:18px;min-height:18px}
    [data-live-control=StatusText]{vertical-align:baseline}`;
  document.head.append(style); document.documentElement.dataset.theme = theme;
}
