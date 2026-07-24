// Colour helpers. Every visual in the game is derived from a handful of base
// hexes, so mixing and alpha-ing them cheaply matters more than being general.

export function rgb(hex) {
  const h = hex.replace('#', '');
  return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
}

export function hexA(hex, a) {
  const [r, g, b] = rgb(hex);
  return `rgba(${r},${g},${b},${a})`;
}

/** Blend two hex colours. Returns hex, NOT rgb(): the result is routinely fed
 *  back through hexA()/mix(), and only hex survives that round trip. */
export function mix(a, b, t) {
  const A = rgb(a), B = rgb(b);
  return '#' + A.map((v, i) => {
    const c = Math.max(0, Math.min(255, Math.round(v + (B[i] - v) * t)));
    return c.toString(16).padStart(2, '0');
  }).join('');
}
