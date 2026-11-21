import { useState, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { createPortal } from 'react-dom';
import { Button, IconButton, type ButtonVariant } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { DateField } from '@/shared/ui/DateField';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Switch } from '@/shared/ui/Switch';
import { Chip } from '@/shared/ui/Chip';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText, type StatusTone } from '@/shared/ui/StatusText';
import { Icon } from '@/shared/ui/Icon';
import { MoreVert } from '@/shared/ui/icons';
import { StoryProviders } from './story-providers';
import { mockupIcon, replaceArchiveIcons } from './mockup-icons';
import sharedCss from './catalog.css?inline';

interface Mount { id: number; source: HTMLElement; host: HTMLElement; container: HTMLElement; kind: string; label: string }
const iconSelector = '.sym, .msym, .symbol, .material-symbols-outlined, [data-lucide], [data-restored-icon], svg';
function textOf(element: HTMLElement) {
  const copy = element.cloneNode(true) as HTMLElement;
  copy.querySelectorAll(iconSelector + ', input, textarea, select').forEach(node => node.remove());
  copy.querySelectorAll('span').forEach(node => node.append(document.createTextNode(' ')));
  return copy.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}
function labelOf(element: HTMLElement) {
  return element.getAttribute('aria-label') ?? element.getAttribute('title') ?? textOf(element) ?? '';
}
function glyphOf(element: HTMLElement) {
  const node = element.querySelector<HTMLElement>(iconSelector);
  const name = node?.dataset.restoredIcon ?? node?.dataset.lucide ?? node?.textContent?.trim();
  return name ? mockupIcon(name) : undefined;
}
function clicked(element: HTMLElement, event?: { stopPropagation(): void }) {
  event?.stopPropagation();
  if (element instanceof HTMLButtonElement || element instanceof HTMLInputElement) element.click();
  else element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
}
function setValue(element: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement, value: string) {
  element.value = value;
  element.dispatchEvent(new Event('input', { bubbles: true }));
  element.dispatchEvent(new Event('change', { bubbles: true }));
}
export function LiveControl({ mount }: { mount: Mount }) {
  const element = mount.source;
  const input = element.matches('input,textarea,select') ? element as HTMLInputElement : element.querySelector<HTMLInputElement>('input,textarea,select');
  const [value, updateValue] = useState(input?.value ?? element.querySelector('.val,.value,.fv,.f-val')?.textContent?.trim() ?? '');
  const [selected, setSelected] = useState(input instanceof HTMLInputElement ? input.checked : element.matches('.sel,.on,.selected,.checked,[aria-pressed="true"],[aria-checked="true"]'));
  const label = mount.label || 'Значение';
  const disabled = element.matches(':disabled,.disabled,.off,[aria-disabled="true"]') || !!input?.disabled;
  const readOnly = element.matches('.ro,.readonly,[aria-readonly="true"]') || !!input?.readOnly;
  const fromScan = element.matches('.unconf,.from-scan');
  const icon = glyphOf(element);
  const change = (next: string) => { updateValue(next); if (input) setValue(input, next); };
  if (mount.kind === 'Checkbox') return <Checkbox label={textOf(element) || undefined} aria-label={label} disabled={disabled} checked={selected} onChange={next => { setSelected(next); if (input instanceof HTMLInputElement) { input.checked = next; input.dispatchEvent(new Event('change', { bubbles: true })); } else clicked(element); }} />;
  if (mount.kind === 'Switch') return <Switch label={label} disabled={disabled} checked={selected} onChange={next => { setSelected(next); clicked(element); }} />;
  if (mount.kind === 'DateField') {
    const raw = value.match(/^(\d{1,2})[./](\d{1,2})[./](\d{4})$/);
    const iso = raw ? `${raw[3]}-${raw[2].padStart(2, '0')}-${raw[1].padStart(2, '0')}` : /^\d{4}-\d{2}-\d{2}$/.test(value) ? value : '';
    return <DateField label={label} value={iso} onChange={change} disabled={disabled} readOnly={readOnly} fromScan={fromScan} density={element.matches('.dense,.compact') ? 'dense' : 'form'} />;
  }
  if (mount.kind === 'TextAreaField') return <TextAreaField label={label} value={value} disabled={disabled} readOnly={readOnly} fromScan={fromScan} onChange={event => change(event.target.value)} />;
  if (mount.kind === 'TextField') return <TextField label={label} value={value} placeholder={input?.placeholder} disabled={disabled} readOnly={readOnly} fromScan={fromScan} required={input?.required || !!element.querySelector('.req')} density={element.matches('.dense,.compact') || !element.matches('.field,.fld,.m-field,.f') ? 'dense' : 'form'} onChange={event => change(event.target.value)} />;
  if (mount.kind === 'Select' && input instanceof HTMLSelectElement) return <Select label={label} density="dense" value={value} onValueChange={change} disabled={disabled}>{[...input.options].filter(option => option.value).map((option, index) => <SelectItem key={index} value={option.value}>{option.text}</SelectItem>)}</Select>;
  if (mount.kind === 'Chip') return <Chip selected={selected} menu={element.matches('[aria-haspopup],.dd,.menu') || !!element.querySelector('[data-restored-icon="arrow_drop_down"]')} icon={icon} disabled={disabled} onClick={event => { setSelected(!selected); clicked(element, event); }}>{textOf(element) || label}</Chip>;
  if (mount.kind === 'StatusText') {
    const tone: StatusTone = element.matches('.err,.error,.bad') ? 'error' : element.matches('.warn,.warning') ? 'warning' : element.matches('.ok,.success') ? 'success' : 'neutral';
    return <StatusText tone={tone} icon={icon}>{textOf(element)}</StatusText>;
  }
  if (mount.kind === 'IconButton') return <IconButton icon={icon ?? MoreVert} label={label} disabled={disabled} onClick={event => clicked(element, event)} />;
  const variant: ButtonVariant = element.matches('.filled,.primary,.pri,.btn-primary,.bg-primary') ? 'filled' : element.matches('.tonal,.secondary,.bg-secondary-container') ? 'tonal' : element.matches('.outlined,.outline,.out,.border-outline') ? 'outlined' : 'text';
  return <Button variant={variant} disabled={disabled} icon={icon ? <Icon icon={icon} size={18} /> : undefined} onClick={event => clicked(element, event)}>{textOf(element) || label}</Button>;
}

function kindOf(element: HTMLElement) {
  if (element.matches('.field,.fld,.m-field,.f') && element.querySelector('input, textarea, .val, .value, .fv, .f-val, .ph, .placeholder')) {
    if (/дат[аыу]|период/i.test(element.querySelector('label,.label,.fl,.lbl,.lab,.f-label,.f-lab')?.textContent ?? '')) return 'DateField';
    return element.querySelector('textarea') ? 'TextAreaField' : 'TextField';
  }
  if (element.matches('textarea')) return 'TextAreaField';
  if (element.matches('select')) return 'Select';
  if (element.matches('input[type=checkbox],.cb,.check-box,.checkbox,[role=checkbox]')) return 'Checkbox';
  if (element.matches('input[type=date]')) return 'DateField';
  if (element.matches('input:not([type=hidden]):not([type=radio]):not([type=file]):not([type=range]):not([type=button]):not([type=submit])')) return 'TextField';
  if (element.matches('.switch,.sw,[role=switch]')) return 'Switch';
  if (element.matches('.chip,.m-chip,.filter-chip,button[aria-haspopup=listbox]')) return 'Chip';
  if (element.matches('.st,.stx,.m-status,.status-text')) return 'StatusText';
  if (element.matches('.ib,.ibtn,.icon-btn,.icon-button,.t48') || element.matches('button') && !textOf(element) && element.querySelector(iconSelector)) return 'IconButton';
  if (element.matches('button,.btn')) return 'Button';
  return '';
}

/** Preserve original screen composition and event handlers; render controls from shared/ui. */
// This imperative adapter creates the single React root for an archived document.
// eslint-disable-next-line react-refresh/only-export-components
export function connectMockupControls(theme: 'light' | 'dark', report: (counts: Record<string, number>) => void) {
  const rootElement = document.createElement('div'); rootElement.id = 'mockup-react-root'; document.body.append(rootElement);
  const root = createRoot(rootElement);
  const sheet = new CSSStyleSheet();
  sheet.replaceSync(sharedCss.replace(/@import\s+[^;]+;/g, '') + '\n:host{font-family:var(--md-ref-typeface-plain);color:var(--md-sys-color-on-surface)}');
  const mounts = new Map<HTMLElement, Mount>(); let nextId = 0;
  const selector = '.field,.fld,.m-field,.f,textarea,select,input,.cb,.check-box,.checkbox,[role=checkbox],.switch,.sw,[role=switch],.chip,.m-chip,.filter-chip,.st,.stx,.m-status,.status-text,.ib,.ibtn,.icon-btn,.icon-button,.t48,button,.btn';
  const scan = () => {
    replaceArchiveIcons(document.body);
    for (const [source, mount] of mounts) if (!source.isConnected || !mount.host.isConnected) { mount.host.remove(); mounts.delete(source); }
    for (const source of document.querySelectorAll<HTMLElement>(selector)) {
      if (mounts.has(source) || source.closest('[data-live-control],#mockup-react-root') || source.matches('[type=hidden]') || source.closest('header.toolbar,.toolbar[role=navigation]')) continue;
      if (source.parentElement?.closest(selector) && kindOf(source.parentElement.closest<HTMLElement>(selector)!)) continue;
      const kind = kindOf(source); if (!kind) continue;
      const fieldLabel = source.querySelector('label,.label,.fl,.lbl,.lab,.f-label,.f-lab')?.textContent?.replace(/[*•]/g, '').trim();
      const input = source.matches('input,textarea,select') ? source as HTMLInputElement : source.querySelector<HTMLInputElement>('input,textarea,select');
      const label = fieldLabel || input?.getAttribute('aria-label') || input?.placeholder || labelOf(source);
      const host = document.createElement('span'); host.dataset.liveControl = kind;
      host.style.cssText = 'display:inline-flex;vertical-align:middle;position:relative;max-width:100%;flex-shrink:0';
      if (kind.endsWith('Field') || kind === 'Select') host.style.cssText += ';display:block;width:100%;min-width:0;flex-shrink:1';
      const shadow = host.attachShadow({ mode: 'open' }); shadow.adoptedStyleSheets = [sheet];
      const container = document.createElement('span'); container.style.cssText = 'display:contents'; shadow.append(container);
      source.before(host); host.append(source); source.style.setProperty('display', 'none', 'important'); source.setAttribute('aria-hidden', 'true'); source.tabIndex = -1;
      mounts.set(source, { id: nextId++, source, host, container, kind, label });
    }
    const portals: ReactNode[] = [];
    const counts: Record<string, number> = {};
    for (const mount of mounts.values()) { counts[mount.kind] = (counts[mount.kind] ?? 0) + 1; portals.push(createPortal(<LiveControl mount={mount} />, mount.container, String(mount.id))); }
    root.render(<StoryProviders theme={theme} component="RestoredMockup">{portals}</StoryProviders>);
    report(counts);
  };
  let timer: ReturnType<typeof setTimeout>;
  const observer = new MutationObserver(records => {
    if (!records.some(record => !(record.target instanceof Element) || !record.target.closest('#mockup-react-root'))) return;
    clearTimeout(timer); timer = setTimeout(scan, 80);
  });
  scan(); observer.observe(document.body, { childList: true, subtree: true });
  return () => { observer.disconnect(); clearTimeout(timer); root.unmount(); };
}
