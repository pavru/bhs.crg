import type { Story } from './story-model';
import { TOOLTIP_DELAY } from '@/shared/ui/tooltipTrigger';

/** Reuse the browser's own generated CSS for static hover/pressed examples. */
function enablePseudoStates() {
  if (document.querySelector('[data-catalog-states]')) return;
  const style = document.createElement('style');
  style.dataset.catalogStates = '';
  style.textContent = [...document.styleSheets].flatMap(sheet => {
    try { return [...sheet.cssRules].map(rule => rule.cssText).filter(text => /:hover|:active/.test(text)); }
    catch { return []; }
  }).join('\n').replaceAll(':hover', ':is(:hover,[data-catalog-hover])').replaceAll(':active', ':is(:active,[data-catalog-pressed])');
  document.head.append(style);
}
const frame = () => new Promise<void>(resolve => {
  let completed = false;
  const finish = () => { if (!completed) { completed = true; resolve(); } };
  // Offscreen iframes and background tabs can pause animation frames.
  const timer = setTimeout(finish, 48);
  requestAnimationFrame(() => requestAnimationFrame(() => { clearTimeout(timer); finish(); }));
});
async function waitFor(check: () => boolean, message: string, timeout = 8000) {
  const start = performance.now();
  while (!check()) {
    if (performance.now() - start > timeout) throw new Error(message);
    await frame();
  }
}
export async function activateStory(story: Story) {
  await Promise.race([document.fonts.ready, new Promise(resolve => setTimeout(resolve, 2000))]);
  await frame();
  const control = document.querySelector<HTMLElement>('#specimen button:not([disabled]), #specimen input:not([disabled]), #specimen textarea, #specimen [tabindex="0"]');
  if (control && story.state === 'Focus') control.focus();
  if (control && ['Hover', 'Pressed'].includes(story.state ?? '')) {
    enablePseudoStates();
    const attr = story.state === 'Hover' ? 'data-catalog-hover' : 'data-catalog-pressed';
    let node: HTMLElement | null = control;
    while (node && node.id !== 'specimen') { node.setAttribute(attr, ''); node = node.parentElement; }
  }
  if (control && story.component === 'RichTooltip') {
    control.dispatchEvent(new PointerEvent('pointerover', { pointerType: 'mouse', bubbles: true }));
    await new Promise(resolve => setTimeout(resolve, TOOLTIP_DELAY + 40));
  }
  if (control && story.state === 'Open' && ['Select', 'SelectItem', 'SelectGroup', 'SiteSelectItems', 'RevealItem', 'SiteRevealScope', 'SplitButton', 'TypePickerField', 'RowActionsMenu', 'AccountMenu', 'WorkspaceSwitcher', 'SectionMenu'].includes(story.component)) control.click();
  if (story.state === 'Open' && ['SuggestField', 'PlainField'].includes(story.component) && control) {
    control.focus(); control.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', altKey: true, bubbles: true }));
  }
  if (story.state === 'Expanded') [...document.querySelectorAll<HTMLButtonElement>('button')].find(b => /подроб|Техничес|Детал/i.test(b.textContent ?? ''))?.click();
  if (story.state === 'Confirm') [...document.querySelectorAll<HTMLButtonElement>('button')].find(b => /Перераспознать источник/.test(b.textContent ?? ''))?.click();
  if (story.state === 'Submenu') {
    const trigger = document.querySelector<HTMLElement>('[role="menuitem"][aria-haspopup="menu"]');
    trigger?.focus(); trigger?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
  }
  if (['Ready', 'Over', 'Rejected'].includes(story.state ?? '')) {
    const transfer = new DataTransfer(); transfer.items.add(new File(['example'], 'Счёт.pdf', { type: 'application/pdf' }));
    if (story.state === 'Rejected') transfer.items.add(new File(['example'], 'Акт.pdf', { type: 'application/pdf' }));
    const zone = story.state === 'Ready' ? document.body : document.querySelector('#specimen .border-dashed');
    zone?.dispatchEvent(new DragEvent(story.state === 'Rejected' ? 'drop' : 'dragenter', { bubbles: true, dataTransfer: transfer }));
  }
  if (['CodeEditor', 'MonacoEditor'].includes(story.component)) await waitFor(() => !!document.querySelector('.monaco-editor'), 'Редактор Monaco не загрузился');
  if (['PdfViewer', 'PdfPages', 'PdfViewerImpl'].includes(story.component) && story.variant.includes('Loaded')) await waitFor(() => !!document.querySelector('canvas'), 'Страница PDF не отрисовалась');
  if (story.state === 'Magnifier') {
    await waitFor(() => !!document.querySelector<HTMLImageElement>('#specimen img')?.naturalWidth, 'Изображение не загрузилось');
    [...document.querySelectorAll<HTMLButtonElement>('button')].find(b => b.getAttribute('aria-label') === 'Лупа')?.click();
    const image = document.querySelector('#specimen img')!;
    const box = image.getBoundingClientRect();
    image.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, pointerType: 'mouse', clientX: box.x + box.width / 2, clientY: box.y + Math.min(200, box.height / 2) }));
  }
  await frame();
}
