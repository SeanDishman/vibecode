import fs from 'fs';

const path = new URL('../src/portraits.js', import.meta.url);
const src = fs.readFileSync(path, 'utf8');

const baseStart = src.indexOf('const BASE = {');
const extraComment = src.indexOf('/** Extra pixels');
const extraStart = src.indexOf('const EXTRA = {');
const formatStart = src.indexOf('/** Format big');

const BASE = new Function(src.slice(baseStart, extraComment) + '; return BASE;')();
const EXTRA = new Function(src.slice(extraStart, formatStart) + '; return EXTRA;')();

const EMPTY = '....................';
const SHIFT = 2;

function shiftDown(rows, n) {
  let trail = 0;
  for (let i = rows.length - 1; i >= 0 && rows[i] === EMPTY; i--) trail++;
  const shift = Math.min(n, trail);
  if (!shift) return rows;
  return [...Array(shift).fill(EMPTY), ...rows.slice(0, rows.length - shift)];
}

const newBASE = {};
for (const id of Object.keys(BASE)) newBASE[id] = shiftDown(BASE[id], SHIFT);

const newEXTRA = {};
for (const id of Object.keys(EXTRA)) {
  newEXTRA[id] = EXTRA[id].map((st) => ({
    tier: st.tier,
    px: st.px.map(([x, y, ch]) => [x, Math.min(19, y + SHIFT), ch]),
  }));
}

function emitBase() {
  let out = 'const BASE = {\n';
  for (const id of Object.keys(newBASE)) {
    out += `  ${id}: [\n`;
    for (const r of newBASE[id]) out += `    '${r}',\n`;
    out += '  ],\n';
  }
  return out + '};\n\n';
}

function emitExtra() {
  let out = 'const EXTRA = {\n';
  for (const id of Object.keys(newEXTRA)) {
    out += `  ${id}: [\n`;
    for (const st of newEXTRA[id]) {
      const px = st.px.map(([x, y, ch]) => `[${x}, ${y}, '${ch}']`).join(', ');
      out += `    { tier: ${st.tier}, px: [${px}] },\n`;
    }
    out += '  ],\n';
  }
  return out + '};\n\n';
}

const out =
  src.slice(0, baseStart) +
  emitBase() +
  src.slice(extraComment, extraStart) +
  emitExtra() +
  src.slice(formatStart);

fs.writeFileSync(path, out);

// validate
const B = new Function(out.slice(out.indexOf('const BASE'), out.indexOf('/** Extra')) + '; return BASE;')();
for (const id of Object.keys(B)) {
  if (B[id].length !== 20) throw new Error(`${id} rows ${B[id].length}`);
  for (const r of B[id]) if (r.length !== 20) throw new Error(`${id} len ${r.length}`);
}
console.log('shifted +2, validated', Object.keys(B).length);
