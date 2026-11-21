import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '@fontsource/roboto/700.css';
import '@fontsource/roboto-mono/400.css';
import '@/index.css';
import { setFormatLocale } from '@/shared/format/format';
import { installStoryApi } from './story-api';
import { connectMockupControls } from './mockup-bridge';
import { applyMockupTheme } from './mockup-theme';
import { installArchiveIcons, replaceArchiveIcons } from './mockup-icons';
import { restoreDcTemplates } from './mockup-dc';
import manifest from './mockups/manifest.json';
import type { MockupState } from './mockup-model';

const params = new URLSearchParams(location.search);
const requested = params.get('project');
const project = manifest.projects.find(p => p.id === requested || p.aliases.includes(requested ?? ''));
const page = project?.pages.find(p => p.path === params.get('page')) ?? project?.pages[0];
const theme = params.get('theme') === 'dark' ? 'dark' : 'light';
const original = params.get('original') === '1';
const state: MockupState = { ready: false, project: project?.id ?? '', page: page?.path ?? '', controls: {} };
Object.assign(window, { __mockupState: state });
let cleanup: (() => void) | undefined;
const report = () => parent.postMessage({ type: 'mockup-state', ...state }, location.origin);

function scriptContentAllowed(content: string) {
  // Claude's hosting SDK is not part of a mockup. Exported screen scripts remain intact.
  return !/capabilities\s*=|artifact-client|claude\.site|__ARTIFACT|postMessage\(.+artifact/i.test(content);
}
async function main() {
  if (!project || !page) throw Error('Макет или страница не найдены в восстановленном архиве');
  setFormatLocale('ru-RU'); installStoryApi(); installArchiveIcons();
  const base = new URL(`./mockups/${project.id}/`, location.href);
  const response = await fetch(new URL(page.path, base));
  if (!response.ok) throw Error(`Не удалось прочитать страницу: ${response.status}`);
  const parsed = new DOMParser().parseFromString(await response.text(), 'text/html');
  const scripts = [...parsed.querySelectorAll('script')].map(script => ({ src: script.getAttribute('src'), code: script.textContent ?? '', type: script.type }));
  const dcTemplate = parsed.cloneNode(true) as Document;
  parsed.querySelectorAll('script,base,link[rel=preconnect],link[href^="http"],meta[http-equiv],link[href$="fonts.css"]').forEach(node => node.remove());
  const baseElement = document.createElement('base'); baseElement.href = base.href; document.head.append(baseElement);
  const styles = [...parsed.querySelectorAll('style,link[rel=stylesheet]')];
  const loaded: Promise<unknown>[] = [];
  for (const source of styles) {
    const element = source.cloneNode(true) as HTMLLinkElement;
    if (element.tagName === 'LINK') loaded.push(new Promise(resolve => { element.onload = resolve; element.onerror = resolve; }));
    document.head.append(element); source.remove();
  }
  document.body.className = parsed.body.className;
  const hasDcLogic = !!dcTemplate.querySelector('script[type="text/x-dc"],script[data-dc-script]');
  if (hasDcLogic) document.body.innerHTML = '<x-dc></x-dc>';
  else { replaceArchiveIcons(parsed.body); document.body.innerHTML = parsed.body.innerHTML; }
  document.title = `${project.title} — ${page.title}`;
  if (project.kind === 'design') {
    for (const item of scripts) {
      if (item.type && !['text/javascript', 'application/javascript', 'module'].includes(item.type)) continue;
      if (item.src) {
        if (/^https?:|^\/\/|support\.js|lucide(?:\.min)?\.js/.test(item.src)) continue;
        const script = document.createElement('script'); script.src = new URL(item.src, new URL(page.path, base)).href;
        await new Promise<void>((resolve, reject) => { script.onload = () => resolve(); script.onerror = () => reject(Error('Не найден сценарий ' + item.src)); document.body.append(script); });
      } else if (scriptContentAllowed(item.code)) {
        const script = document.createElement('script'); script.textContent = item.code; document.body.append(script);
      }
    }
  }
  await Promise.all(loaded);
  if (hasDcLogic) await restoreDcTemplates(dcTemplate, new URL(page.path, base));
  replaceArchiveIcons(document.body);
  if (!original && project.kind === 'design') {
    applyMockupTheme(theme);
    const note = document.createElement('div'); note.className = 'mockup-live-note'; note.dataset.mockupNote = '';
    note.textContent = 'Архивная компоновка · Компоненты и тема из текущего кода BHS.CRG'; document.body.prepend(note);
    cleanup = connectMockupControls(theme, counts => { state.controls = counts; report(); });
  } else if (!original) applyMockupTheme(theme);
  await Promise.race([document.fonts.ready, new Promise(resolve => setTimeout(resolve, 2000))]);
  await new Promise(resolve => setTimeout(resolve, 150));
  state.ready = true; report();
}
window.addEventListener('error', event => { state.error = event.message; report(); });
main().catch(error => {
  state.error = String(error); state.ready = true; report();
  const message = document.createElement('p'); message.textContent = 'Макет не загрузился: ' + String(error); document.body.append(message);
});
if (import.meta.hot) import.meta.hot.dispose(() => cleanup?.());
