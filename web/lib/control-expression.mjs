// The value of a parameter written with controls (`cutoff 600 + 3000 * traffic.level`), as the server reads it (ControlExpression.cs in Ample.Language): numbers with up to three
// decimals and a unit, + - *, brackets, and block.output. Everything is integer fixed point with three decimals (1000 is 1), so the preview gives the same numbers as the build:
// a general control is 0 to 1000, a typed control its value in its unit times 1000. Pure, tested in node (web/lib/test/control-expression.test.mjs).

/** The units that may follow a number (ControlTypes.Units). */
export const EXPRESSION_UNITS = new Set(['Hz', 'dB', 'ms', 's', '%', 'st', 'cents', 'oct', 'deg', 'bars', 'dBFS']);

const FUNCTION_START = /^[A-Za-z_][A-Za-z0-9_-]*\s*\(/;
const REFERENCE_TOKEN = /(?<![\w.])[A-Za-z_][A-Za-z0-9_]*(?:-[A-Za-z0-9_]+)*\.[A-Za-z_][A-Za-z0-9_]*(?![\w(])/;
const isDigit = (c) => c >= '0' && c <= '9';
const isLetter = (c) => /\p{L}/u.test(c);
const isLetterOrDigit = (c) => /[\p{L}\p{Nd}]/u.test(c);
const isSpace = (c) => /\s/.test(c);

/** Whether a parameter value is a control expression: it names a block.output and is not one of the automation functions (auto, lfo, env, fadein ...). */
export function looksLikeExpression(text) {
  const t = String(text ?? '').trim();
  return t.length > 0 && !FUNCTION_START.test(t) && REFERENCE_TOKEN.test(t);
}

/** Rounds a fixed-point value to a whole number, half away from zero (ControlExpression.ToInt). */
export const expressionToInt = (fixedPoint) => Math.trunc((fixedPoint + (fixedPoint >= 0 ? 500 : -500)) / 1000);

const multiply = (a, b) => { const product = a * b; return Math.trunc((product + (product >= 0 ? 500 : -500)) / 1000); };

class Parser {
  constructor(text) { this.text = text; this.position = 0; this.references = []; this.units = []; }
  get atEnd() { return this.position >= this.text.length; }
  skipSpace() { while (this.position < this.text.length && isSpace(this.text[this.position])) this.position++; }

  parseExpression() {
    let left = this.parseTerm();
    for (;;) {
      this.skipSpace();
      if (this.atEnd || (this.text[this.position] !== '+' && this.text[this.position] !== '-')) return left;
      const op = this.text[this.position++];
      left = { op, left, right: this.parseTerm() };
    }
  }

  parseTerm() {
    let left = this.parseFactor();
    for (;;) {
      this.skipSpace();
      if (this.atEnd || this.text[this.position] !== '*') return left;
      this.position++;
      left = { op: '*', left, right: this.parseFactor() };
    }
  }

  parseFactor() {
    this.skipSpace();
    if (this.atEnd) throw new SyntaxError('the expression ends too soon');
    const c = this.text[this.position];
    if (c === '-') { this.position++; return { neg: this.parseFactor() }; }
    if (c === '(') {
      this.position++;
      const inner = this.parseExpression();
      this.skipSpace();
      if (this.atEnd || this.text[this.position] !== ')') throw new SyntaxError('a bracket is not closed');
      this.position++;
      return inner;
    }
    if (isDigit(c)) return this.parseNumber();
    if (isLetter(c) || c === '_') return this.parseReference();
    throw new SyntaxError(`unexpected "${c}"`);
  }

  parseNumber() {
    const text = this.text, start = this.position;
    while (this.position < text.length && isDigit(text[this.position])) this.position++;
    const whole = Number(text.slice(start, this.position));
    let fraction = 0;
    if (this.position + 1 < text.length && text[this.position] === '.' && isDigit(text[this.position + 1])) {
      this.position++;
      let digits = 0;
      while (this.position < text.length && isDigit(text[this.position])) {
        if (digits < 3) { fraction = fraction * 10 + (text.charCodeAt(this.position) - 48); digits++; }
        this.position++;
      }
      while (digits++ < 3) fraction *= 10;
    }
    // a unit right after the number (600 Hz, -6 dB, 30%)
    const save = this.position;
    this.skipSpace();
    const unitStart = this.position;
    while (this.position < text.length && (isLetter(text[this.position]) || text[this.position] === '%')) this.position++;
    const unit = text.slice(unitStart, this.position), next = text[this.position];
    if (unit.length > 0 && EXPRESSION_UNITS.has(unit) && !(next !== undefined && (next === '.' || next === '(' || isLetterOrDigit(next)))) this.units.push(unit);
    else this.position = save;
    return { number: whole * 1000 + fraction };
  }

  identifier() {
    const text = this.text, start = this.position;
    while (this.position < text.length && (isLetterOrDigit(text[this.position]) || text[this.position] === '_'
      || (text[this.position] === '-' && this.position + 1 < text.length && isLetterOrDigit(text[this.position + 1]) && this.position > start))) this.position++;
    return text.slice(start, this.position);
  }

  parseReference() {
    const text = this.text, start = this.position, block = this.identifier();
    if (this.position >= text.length || text[this.position] !== '.') throw new SyntaxError(`"${block}" is not a control: write the block and its output, for example ${block}.level`);
    this.position++;
    if (this.position >= text.length || !(isLetter(text[this.position]) || text[this.position] === '_')) throw new SyntaxError(`"${block}." needs the name of an output`);
    const outputStart = this.position;
    while (this.position < text.length && (isLetterOrDigit(text[this.position]) || text[this.position] === '_')) this.position++;
    this.references.push({ block, output: text.slice(outputStart, this.position), start, length: this.position - start });
    return { ref: this.references.length - 1 };
  }
}

const evaluateNode = (node, valueOf) => {
  if ('number' in node) return node.number;
  if ('ref' in node) return valueOf(node.ref);
  if ('neg' in node) return -evaluateNode(node.neg, valueOf);
  if (node.op === '+') return evaluateNode(node.left, valueOf) + evaluateNode(node.right, valueOf);
  if (node.op === '-') return evaluateNode(node.left, valueOf) - evaluateNode(node.right, valueOf);
  return multiply(evaluateNode(node.left, valueOf), evaluateNode(node.right, valueOf));
};

/**
 * Reads an expression.
 * @param {string} text the parameter value
 * @returns {{ references: { block: string, output: string, start: number, length: number }[], units: string[], evaluate: (valueOf: (index: number) => number) => number } | null}
 *   null when the text is not an expression; evaluate gives the fixed-point value (1000 is 1) from the fixed-point value of every reference
 */
export function parseExpression(text) {
  const source = String(text ?? '');
  const parser = new Parser(source);
  try {
    const root = parser.parseExpression();
    parser.skipSpace();
    if (!parser.atEnd) return null;
    return { references: parser.references, units: parser.units, evaluate: (valueOf) => evaluateNode(root, valueOf) };
  } catch (error) {
    if (error instanceof SyntaxError) return null;
    throw error;
  }
}
