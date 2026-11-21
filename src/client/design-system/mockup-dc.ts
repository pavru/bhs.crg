import { mockupIcon } from './mockup-icons';

type Values = Record<string, unknown>;
type Logic = { props: Values; state: Values; renderVals?: () => Values; setState: (value: Values | ((state: Values) => Values)) => void };
function lookup(expression: string, scope: Values): unknown {
  if (['true', 'yes'].includes(expression)) return true;
  if (['false', 'no'].includes(expression)) return false;
  let value: unknown = scope;
  for (const key of expression.split('.')) value = value && typeof value === 'object' ? (value as Values)[key] : undefined;
  if (value === undefined && /^(ic|i)\./.test(expression)) return mockupIcon(expression.split('.')[1])?.d ?? '';
  return value;
}
function interpolate(value: string, scope: Values): unknown {
  const exact = value.match(/^\{\{([\w.]+)\}\}$/);
  if (exact) return lookup(exact[1], scope);
  return value.replace(/\{\{([\w.]+)\}\}/g, (_, key) => String(lookup(key, scope) ?? ''));
}

/** Minimal compatibility renderer for the exported Claude Design template syntax.
 * It executes the archived renderVals, loops and bindings; it does not invent screens.
 */
export async function restoreDcTemplates(documentSource: Document, sourceUrl: URL) {
  const cache = new Map<string, Promise<Document>>();
  const load = (url: URL) => {
    let pending = cache.get(url.href);
    if (!pending) { pending = fetch(url).then(async response => {
      if (!response.ok) throw Error(`Не найден компонент макета: ${url.pathname}`);
      return new DOMParser().parseFromString(await response.text(), 'text/html');
    }); cache.set(url.href, pending); }
    return pending;
  };
  async function mount(template: Document, url: URL, container: Element, properties: Values, depth = 0) {
    if (depth > 12) throw Error('Циклический импорт компонентов макета');
    const script = template.querySelector<HTMLScriptElement>('script[type="text/x-dc"],script[data-dc-script]');
    const schema = JSON.parse(script?.getAttribute('data-props') ?? '{}') as Record<string, { default?: unknown }>;
    const defaults = Object.fromEntries(Object.entries(schema).map(([key, value]) => [key, value.default]));
    const props = { ...defaults, ...properties };
    let render: () => void = () => {};
    class DCLogic {
      props: Values; state: Values = {};
      constructor(value: Values) { this.props = value; }
      setState(value: Values | ((state: Values) => Values)) { this.state = { ...this.state, ...(typeof value === 'function' ? value(this.state) : value) }; render(); }
    }
    let logic: Logic | undefined;
    if (script?.textContent?.includes('class Component')) {
      const make = new Function('DCLogic', 'props', `${script.textContent}\nreturn new Component(props);`);
      logic = make(DCLogic, props) as Logic;
    }
    const source = template.querySelector('x-dc') ?? template.body;
    const blueprint = [...source.childNodes].filter(node => !(node instanceof Element && node.matches('helmet,script'))).map(node => node.cloneNode(true));
    for (const style of template.querySelectorAll('helmet style')) document.head.append(style.cloneNode(true));
    let pendingImports: Promise<unknown>[] = [];
    const walk = (node: Node, scope: Values) => {
      if (node.nodeType === Node.TEXT_NODE) { node.textContent = String(interpolate(node.textContent ?? '', scope) ?? ''); return; }
      if (!(node instanceof Element)) return;
      if (node.tagName === 'SC-FOR') {
        const list = interpolate(node.getAttribute('list') ?? '', scope);
        const alias = node.getAttribute('as') ?? 'item';
        const fragment = document.createDocumentFragment();
        if (Array.isArray(list)) for (const [index, item] of list.entries()) for (const child of node.childNodes) {
          const copy = child.cloneNode(true); fragment.append(copy); walk(copy, { ...scope, [alias]: item, $index: index });
        }
        node.replaceWith(fragment); return;
      }
      if (node.tagName === 'SC-IF') {
        if (!interpolate(node.getAttribute('value') ?? '', scope)) { node.remove(); return; }
        for (const child of [...node.childNodes]) walk(child, scope);
        node.replaceWith(...node.childNodes); return;
      }
      if (node.tagName === 'DC-IMPORT') {
        const name = node.getAttribute('name');
        const childProps = Object.fromEntries([...node.attributes].filter(attr => !['name', 'hint-size'].includes(attr.name)).map(attr => [attr.name, interpolate(attr.value, scope)]));
        const childUrl = new URL(`${name}.dc.html`, url);
        pendingImports.push(load(childUrl).then(child => mount(child, childUrl, node, childProps, depth + 1)));
        return;
      }
      for (const attribute of [...node.attributes]) {
        if (!attribute.value.includes('{{')) continue;
        const value = interpolate(attribute.value, scope);
        const name = attribute.name.toLowerCase();
        if (typeof value === 'function') {
          node.removeAttribute(attribute.name);
          if (name === 'ref') value(node);
          else if (name.startsWith('on')) node.addEventListener(name.slice(2) === 'change' ? 'input' : name.slice(2), event => value(event));
        } else if (['checked', 'disabled', 'selected', 'hidden'].includes(name)) {
          node.toggleAttribute(name, !!value);
        } else node.setAttribute(attribute.name, String(value ?? ''));
      }
      for (const child of [...node.childNodes]) walk(child, scope);
    };
    render = () => {
      const scope = { ...props, ...logic?.state, ...logic?.renderVals?.() };
      pendingImports = [];
      const fragment = document.createDocumentFragment();
      for (const child of blueprint) { const copy = child.cloneNode(true); fragment.append(copy); walk(copy, scope); }
      container.replaceChildren(fragment);
    };
    render(); await Promise.all(pendingImports);
  }
  if (documentSource.querySelector('script[type="text/x-dc"],script[data-dc-script]')) {
    const container = document.querySelector('x-dc') ?? document.body;
    await mount(documentSource, sourceUrl, container, {});
  }
}
