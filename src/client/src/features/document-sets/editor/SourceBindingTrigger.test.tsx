// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { SourceBindingTrigger } from './SourceBindingTrigger';

/**
 * Кнопка привязки в хвосте поля только для чтения слита со знаком происхождения (D7 sharedui-md3-19, [UI-86]):
 * знак один, его форма — происхождение, цвет — состояние, а пояснение диктор слышит вместе с полем.
 */
afterEach(cleanup);

const svgOf = (name: string) => screen.getByRole('button', { name }).querySelector('svg')!;

describe('SourceBindingTrigger в хвосте поля', () => {
  it('распознанное со скана — другой значок, чем подставленное из источника', () => {
    render(<>
      <SourceBindingTrigger locked={false} isBound onOpen={() => {}}
        origin={{ recognized: true, stale: false, describeId: 'a' }} />
      <SourceBindingTrigger locked isBound onOpen={() => {}}
        origin={{ recognized: false, stale: false, describeId: 'b' }} />
    </>);
    expect(svgOf('Привязка к источнику').innerHTML).not.toBe(svgOf('Снять привязку к источнику').innerHTML);
  });

  it('устарело — цвет warning, норма — primary', () => {
    render(<>
      <SourceBindingTrigger locked={false} isBound onOpen={() => {}}
        origin={{ recognized: false, stale: true, staleReason: null, describeId: 'stale' }} />
    </>);
    expect(svgOf('Привязка к источнику').getAttribute('class')).toContain('text-warning');
    cleanup();
    render(<SourceBindingTrigger locked={false} isBound onOpen={() => {}}
      origin={{ recognized: false, stale: false, describeId: 'ok' }} />);
    expect(svgOf('Привязка к источнику').getAttribute('class')).toContain('text-primary');
  });

  it('пояснение происхождения лежит под ид, на который сошлётся поле', () => {
    render(<SourceBindingTrigger locked={false} isBound onOpen={() => {}}
      origin={{ recognized: true, stale: false, describeId: 'origin-1' }} />);
    expect(document.getElementById('origin-1')!.textContent).toBe('Значение распознано со скана — возможны ошибки чтения');
  });
});
