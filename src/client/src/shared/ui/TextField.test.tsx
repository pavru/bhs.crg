// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { TextField } from './TextField';
import { TextAreaField } from './TextAreaField';

/** Неверное поле — `aria-invalid`, а не одна красная рамка: рамку диктор не прочтёт (ревью PR #1760). */

afterEach(cleanup);

describe('TextField и TextAreaField: aria-invalid', () => {
  it('invalid и error ставят aria-invalid, верное поле — без него', () => {
    render(<>
      <TextField label="Отсрочка" density="dense" invalid />
      <TextField label="Дата" error="Нет такой даты" />
      <TextField label="Имя" />
      <TextAreaField label="Причина" invalid />
    </>);
    expect(screen.getByLabelText('Отсрочка').getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByLabelText('Дата').getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByLabelText('Имя').hasAttribute('aria-invalid')).toBe(false);
    expect(screen.getByLabelText('Причина').getAttribute('aria-invalid')).toBe('true');
  });
});
