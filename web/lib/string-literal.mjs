// The strings of a song as the properties show them: in the source a string is in "double quotes" (with the escapes \" \\ \n \t; any other \x is x: docs/language/02-lexical.md),
// but a property field shows the text itself, without quotes or escapes, and puts the quotes back when it saves. Pure, tested without a DOM. @ample:string-literal

/** True when the stored value is one string in double quotes. */
export const isQuoted = (raw) => /^"[\s\S]*"$/.test(String(raw ?? '').trim()) && String(raw).trim().length >= 2 && !endsWithEscapedQuote(String(raw).trim());

// "abc\" is not a closed string: the last quote is escaped
function endsWithEscapedQuote(text) {
  let backslashes = 0;
  for (let i = text.length - 2; i >= 1 && text[i] === '\\'; i--) backslashes++;
  return backslashes % 2 === 1;
}

/** The text inside a quoted string, with its escapes resolved; anything that is not a quoted string comes back as it is. */
export function unquoteLiteral(raw) {
  const text = String(raw ?? '').trim();
  if (!isQuoted(text)) return String(raw ?? '');
  return text.slice(1, -1).replace(/\\([\s\S])/g, (_, c) => (c === 'n' ? '\n' : c === 't' ? '\t' : c));
}

/** Text as a quoted string: backslash and quote escaped, line breaks as \n and tabs as \t (a carriage return is dropped). */
export function quoteLiteral(text) {
  return '"' + String(text ?? '').replace(/\r/g, '').replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/\n/g, '\\n').replace(/\t/g, '\\t') + '"';
}
