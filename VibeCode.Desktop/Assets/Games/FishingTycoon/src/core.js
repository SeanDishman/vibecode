// core.js — constants, maths and the balance tables. Leaf module: imports nothing.

export const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
export const lerp = (a, b, t) => a + (b - a) * t;
export const rand = (a = 1, b = 0) => b + Math.random() * (a - b);

/** Money reads better with separators once you are into the thousands. */
export function money(n) {
  n = Math.floor(n);
  if (n >= 1e9) return '$' + (n / 1e9).toFixed(2) + 'B';
  if (n >= 1e6) return '$' + (n / 1e6).toFixed(2) + 'M';
  return '$' + n.toLocaleString('en-US');
}

/* ── depth zones ──────────────────────────────────────────────────────────
   Bands of water, purely for colour and for naming where the hook is sitting.
   Nothing gates them: the Rod ladder below is the whole depth progression. */
export const ZONES = [
  { name: 'Shallows',    from: 0,    to: 900,   col: '#2e86ab', deep: '#1f6a8c' },
  { name: 'The Reef',    from: 900,  to: 2200,  col: '#217a95', deep: '#175f79' },
  { name: 'Open Ocean',  from: 2200, to: 4200,  col: '#1a5f7f', deep: '#124b66' },
  { name: 'The Trench',  from: 4200, to: 7000,  col: '#123f5c', deep: '#0c2f47' },
  { name: 'The Abyss',   from: 7000, to: 12000, col: '#0a2438', deep: '#061725' },
];

export function zoneAt(y) {
  for (const z of ZONES) if (y < z.to) return z;
  return ZONES[ZONES.length - 1];
}

/* ── fish ─────────────────────────────────────────────────────────────────
   `w` is spawn weight inside its depth band, and `len` doubles as the weight the
   line has to hold. Value climbs far faster than rod costs do, so pushing one
   zone deeper always feels like the right move. */
export const FISH = [
  { id: 'sardine',  name: 'Sardine',      value: 3,      w: 10, len: 7,  spd: 46, col: '#b9d4e0', min: 0,    max: 1400,  school: 7 },
  { id: 'mackerel', name: 'Mackerel',     value: 8,      w: 8,  len: 9,  spd: 52, col: '#8fd3c7', min: 200,  max: 2200,  school: 5 },
  { id: 'cod',      name: 'Cod',          value: 20,     w: 7,  len: 12, spd: 44, col: '#c9b98a', min: 700,  max: 2600,  school: 3 },
  { id: 'salmon',   name: 'Salmon',       value: 46,     w: 6,  len: 13, spd: 60, col: '#ff9d7a', min: 900,  max: 3200,  school: 4 },
  { id: 'tuna',     name: 'Bluefin Tuna', value: 120,    w: 5,  len: 18, spd: 76, col: '#5aa9e6', min: 2000, max: 4600,  school: 2 },
  { id: 'sword',    name: 'Swordfish',    value: 320,    w: 4,  len: 24, spd: 88, col: '#7f8fd0', min: 2600, max: 5400,  school: 1 },
  { id: 'shark',    name: 'Reef Shark',   value: 760,    w: 3,  len: 30, spd: 70, col: '#95a3ad', min: 3600, max: 6800,  school: 1 },
  { id: 'ray',      name: 'Manta Ray',    value: 1500,   w: 3,  len: 34, spd: 54, col: '#6e7fa8', min: 4200, max: 7600,  school: 1 },
  { id: 'squid',    name: 'Giant Squid',  value: 3600,   w: 2,  len: 38, spd: 64, col: '#d2708f', min: 5200, max: 9000,  school: 1 },
  { id: 'angler',   name: 'Anglerfish',   value: 9000,   w: 2,  len: 26, spd: 40, col: '#c46bd6', min: 7000, max: 12000, school: 1 },
  { id: 'lantern',  name: 'Lanternfish',  value: 22000,  w: 1,  len: 20, spd: 58, col: '#ffd166', min: 8200, max: 12000, school: 2 },
  { id: 'leviath',  name: 'Leviathan',    value: 90000,  w: 1,  len: 56, spd: 50, col: '#7ee787', min: 9800, max: 12000, school: 1 },

  /* ── weather fish ───────────────────────────────────────────────────────
     `sky` pins a species to what the weather is doing: these are only ever on
     the hook — or in the water — under that sky. They pay several times what
     the ordinary fish at the same depth are worth, because you cannot go and
     get them, you can only be ready when they show up.

     They are laddered by depth exactly like the ordinary fish. A single wide
     band does not work: an ocean sunfish worth $340 is a jackpot next to a
     sardine and an insult next to a giant squid, so a sun-break would end up
     *lowering* late-game income. Each rung instead covers two or three rod
     levels and is worth roughly 2–15x the average catch inside its own band.

     They also run heavy for their depth, so landing them takes a better line
     than the bare minimum the rod gate enforces — that is the point of the
     Line track past the safety floor. lineNeededFor() ignores them entirely,
     so ordinary weather always keeps paying regardless. */
  { id: 'stormjack', name: 'Storm Jack',    value: 45,    w: 5, len: 11, spd: 92, col: '#9ad8ff', min: 0,    max: 1400,  school: 3, sky: 'storm' },
  { id: 'thundeel',  name: 'Thunder Eel',   value: 700,   w: 4, len: 20, spd: 78, col: '#c3b0ff', min: 1400, max: 3600,  school: 1, sky: 'storm' },
  { id: 'squallsh',  name: 'Squall Shark',  value: 3400,  w: 3, len: 28, spd: 84, col: '#7fb4d8', min: 3600, max: 6200,  school: 1, sky: 'storm' },
  { id: 'tempest',   name: 'Tempest Ray',   value: 44000, w: 3, len: 42, spd: 68, col: '#8ecbff', min: 6200, max: 12000, school: 1, sky: 'storm' },

  { id: 'sunfish',   name: 'Ocean Sunfish', value: 110,   w: 4, len: 10, spd: 26, col: '#ffe08a', min: 0,    max: 2200,  school: 1, sky: 'sun' },
  { id: 'goldfin',   name: 'Gilded Sunfish', value: 3200, w: 3, len: 24, spd: 40, col: '#ffc247', min: 2200, max: 6200,  school: 1, sky: 'sun' },
  { id: 'sunwhale',  name: 'Sun Whale',     value: 46000, w: 3, len: 34, spd: 36, col: '#ffb03a', min: 6200, max: 12000, school: 1, sky: 'sun' },
];

/* ── the ladders ────────────────────────────────────────────────────────────
   No track is a curve. Every rung is a specific piece of kit with a name, and
   the number it leaves you on was written down by hand rather than evaluated,
   because "1.12x, 1.12x, 1.12x…" is a spreadsheet and "Star Drag, Levelwind,
   Twelve-Gear Head" is a boat. Some rungs are worth more than their neighbours
   and the pattern is meant to be spotted: every third reel head is geared,
   every third line is a braid, every fourth bait is a slick, every fourth
   fishmonger is a signed contract.

   `n` names the part, `v` is the stat it leaves the boat on, `p` is what the
   card says it does, and `big` marks the milestone rungs — the ones that jump
   further than the rung either side of them. Index 0 is the gear Sal already
   owns, so a ladder of N+1 rungs is a track with max N. */

const ROD = [
  { n: 'Cane Pole',           v: 120,  p: 'Sardines, and not much else' },
  { n: 'Boat Rod',            v: 210,  p: 'Mackerel move in' },
  { n: 'Surfcaster',          v: 710,  p: 'Cod move in' },
  { n: 'Downrigger',          v: 910,  p: 'Salmon — and The Reef opens up', big: true },
  { n: 'Trolling Boom',       v: 2010, p: 'Bluefin Tuna move in' },
  { n: 'Deep Drop Rig',       v: 2610, p: 'Swordfish — and the Open Ocean', big: true },
  { n: 'Broomstick Rod',      v: 3610, p: 'Reef Sharks move in' },
  { n: 'Bent-Butt Standup',   v: 4210, p: 'Manta Rays — and The Trench', big: true },
  { n: 'Electric Deep Reel',  v: 5210, p: 'Giant Squid move in' },
  { n: 'Trench Spar',         v: 7010, p: 'Anglerfish — and The Abyss', big: true },
  { n: 'Abyssal Boom',        v: 8210, p: 'Lanternfish move in' },
  { n: 'Leviathan Derrick',   v: 9810, p: 'The sea floor. The Leviathan is down there' },
];

const BAIT = [
  { n: 'Bare Hook',           v: 3.60, p: 'Something bites eventually' },
  { n: 'Bread Dough',         v: 3.24, p: '0.90x the wait' },
  { n: 'Ragworm',             v: 2.88, p: '0.89x the wait' },
  { n: 'Live Shrimp',         v: 2.54, p: '0.88x the wait' },
  { n: 'Chum Slick',          v: 2.01, p: '0.88x — and every 4th bait is a slick: another 0.90x on top', big: true },
  { n: 'Sand Eel',            v: 1.79, p: '0.89x the wait' },
  { n: 'Squid Strip',         v: 1.58, p: '0.88x the wait' },
  { n: 'Mackerel Flapper',    v: 1.39, p: '0.88x the wait' },
  { n: 'Berley Trail',        v: 1.10, p: 'Slick rung — 0.88x, then another 0.90x', big: true },
  { n: 'Glowstick Rig',       v: 0.98, p: '0.89x the wait' },
  { n: 'Electric Lure',       v: 0.86, p: '0.88x the wait' },
  { n: 'Pheromone Gel',       v: 0.75, p: '0.87x the wait' },
  { n: 'Ambergris Chum',      v: 0.59, p: 'Slick rung — 0.88x, then another 0.90x', big: true },
];

const REEL = [
  { n: 'Hand Line',           v: 420,  p: 'Hauled up arm over arm' },
  { n: 'Nylon Handline',      v: 483,  p: '1.15x wind speed' },
  { n: 'Star Drag',           v: 541,  p: '1.12x wind speed' },
  { n: 'Three-Gear Head',     v: 667,  p: '1.12x — and every 3rd head is geared: another 1.10x on top', big: true },
  { n: 'Ball-Bearing Spool',  v: 760,  p: '1.14x wind speed' },
  { n: 'Oiled Bearings',      v: 851,  p: '1.12x wind speed' },
  { n: 'Six-Gear Head',       v: 1049, p: 'Geared rung — 1.12x, then another 1.10x', big: true },
  { n: 'Carbon Drag',         v: 1206, p: '1.15x wind speed' },
  { n: 'Levelwind',           v: 1351, p: '1.12x wind speed' },
  { n: 'Nine-Gear Head',      v: 1664, p: 'Geared rung — 1.12x, then another 1.10x', big: true },
  { n: 'Titanium Spool',      v: 1930, p: '1.16x wind speed' },
  { n: 'Hydraulic Assist',    v: 2200, p: '1.14x wind speed' },
  { n: 'Twelve-Gear Head',    v: 2711, p: 'Geared rung — 1.12x, then another 1.10x', big: true },
];

const LINE = [
  { n: 'Cotton Twine',        v: 7,  p: 'Parts if you look at it wrong' },
  { n: 'Monofilament',        v: 12, p: '+5 kg' },
  { n: 'Heavy Mono',          v: 17, p: '+5 kg' },
  { n: 'Braided Dacron',      v: 24, p: '+7 kg — every 3rd line is a braid, and braids gain more', big: true },
  { n: 'Fluorocarbon',        v: 29, p: '+5 kg' },
  { n: 'Wire Trace',          v: 34, p: '+5 kg' },
  { n: '8-Strand Braid',      v: 41, p: 'Braid rung — +7 kg', big: true },
  { n: 'Kevlar Core',         v: 46, p: '+5 kg' },
  { n: 'Spectra',             v: 51, p: '+5 kg' },
  { n: '12-Strand Braid',     v: 58, p: 'Braid rung — +7 kg', big: true },
  { n: 'Titanium Weave',      v: 63, p: '+5 kg' },
  { n: 'Abyssal Cable',       v: 70, p: '+7 kg. Nothing in this ocean can snap it' },
];

const TRADER = [
  { n: 'Off the Dock',        v: 1.00, p: 'Whatever the quay will pay' },
  { n: 'Dockside Buyer',      v: 1.20, p: '+20% on every fish' },
  { n: 'Ice Truck',           v: 1.42, p: '+22% — it arrives cold now' },
  { n: 'Restaurant Deal',     v: 1.68, p: '+26% — every 3rd rung is a signed contract, worth more', big: true },
  { n: 'Fish Market Stall',   v: 1.90, p: '+22%' },
  { n: 'Wholesale Ledger',    v: 2.14, p: '+24%' },
  { n: 'Export License',      v: 2.55, p: 'Contract rung — +41%', big: true },
  { n: 'Auction House',       v: 2.82, p: '+27%' },
  { n: 'Sushi Contract',      v: 3.10, p: '+28%' },
  { n: 'Overseas Freight',    v: 3.65, p: 'Contract rung — +55%', big: true },
  { n: 'Michelin Supplier',   v: 4.20, p: '+55%. Every fish quadruples in price' },
];

/* Reel Power alternates on purpose: odd rungs cut the fight short, even rungs
   are hardware you brace against, so they buy grip. `g` is the grip multiplier
   over the line's rating. */
const POWER = [
  { n: 'Bare Hands',          v: 0.55, g: 1.00, p: 'Sal against the fish' },
  { n: 'Rod Belt',            v: 0.50, g: 1.04, p: 'Speed rung — fights end 0.05s sooner' },
  { n: 'Fighting Chair',      v: 0.48, g: 1.12, p: 'Grip rung — hold 12% over the line rating', big: true },
  { n: 'Gimbal Harness',      v: 0.43, g: 1.14, p: 'Speed rung — 0.05s off every fight' },
  { n: 'Shoulder Rig',        v: 0.41, g: 1.24, p: 'Grip rung — 24% over the rating', big: true },
  { n: 'Electric Winch',      v: 0.36, g: 1.26, p: 'Speed rung — 0.05s off every fight' },
  { n: 'Hydraulic Arm',       v: 0.34, g: 1.38, p: 'Grip rung — 38% over the rating', big: true },
  { n: 'Gaff Crew',           v: 0.29, g: 1.40, p: 'Speed rung — 0.05s off every fight' },
  { n: 'Davit & Block',       v: 0.27, g: 1.52, p: 'Grip rung — half again over the rating', big: true },
];

/* The net: passive fishing for whatever swims near the surface. It never
   reaches the rod's depth and never takes weather fish — it is the steady
   trickle that gets the whole tree started, not a replacement for rods.
   `d` is the deepest band it sweeps. */
const NET = [
  { n: 'No Net',              v: 16, d: 0,    p: 'Rods only' },
  { n: 'Hand Cast Net',       v: 16, d: 400,  p: 'A catch every 16s from the top 400 m' },
  { n: 'Seine Net',           v: 11, d: 1400, p: 'Every 11s, and it sweeps to 1,400 m' },
  { n: 'Purse Seine',         v: 8,  d: 2600, p: 'Every 8s to 2,600 m — the biggest she can tow', big: true },
];

const CREW = [
  { n: 'Just Sal',            v: 1, p: 'One rod over the stern' },
  { n: 'Marta the Mate',      v: 2, p: 'A second rod, worked off the port rail' },
  { n: 'Old Pike',            v: 3, p: 'Third rod. He does not talk much' },
  { n: 'Nils the Kid',        v: 4, p: 'Four rods over the side at once', big: true },
];

/** Every ladder by track id, so the shop can render a track's whole future. */
export const LADDERS = { net: NET, rod: ROD, bait: BAIT, trader: TRADER, line: LINE, reel: REEL, crew: CREW, power: POWER };

/** One rung of one track. Every stat curve below reads through here, so this is
 *  where they are made total: a level that is out of range reads as the end of
 *  the ladder, and one that is missing entirely reads as the starting gear. A
 *  bare `game.levels.power` on a save written before that track existed comes
 *  back undefined, and clamp() would pass that straight through to `t[undefined]`
 *  — the old arithmetic curves turned it into a silent NaN, and an indexed one
 *  would throw on the very next frame. */
export const rungOf = (id, l) => {
  const t = LADDERS[id];
  return t ? t[clamp(Math.floor(l) || 0, 0, t.length - 1)] : null;
};

/** Line levels. Named because the rod gate below has to agree with it. */
const LINE_MAX = LINE.length - 1;

/* ── the research tree ──────────────────────────────────────────────────────
   This is an idle game: nobody steers, nobody hauls. The deckhands fish on
   their own and every node here makes them do it better.

   Every track but the net has a `needs` gate onto another track, so the net is
   always the first thing bought and the rest fans out from it. `x`/`y` are the
   node's position on the tree canvas in tree units — the shop pans and zooms
   over it, so the layout is free to sprawl wider than any window. */
export const UPGRADES = [
  {
    id: 'net', name: 'Fishing Net', icon: '🥅', x: 40, y: 230,
    blurb: 'Passive fishing off the stern. It is the root of everything: the first upgrade you can afford, and the trickle that pays for the second.',
    stat: l => (l === 0 ? 'Rods only' : `every ${netEvery(l)}s · ${netDepthFor(l).toLocaleString('en-US')} m`),
    cost: l => [25, 600, 4200][l],
  },
  {
    id: 'rod', name: 'Rod', icon: '🎣', x: 350, y: 260,
    needs: { id: 'net', lvl: 1 },
    blurb: 'How deep the hook goes. Every rung clears exactly one new species\' minimum depth — and leaves the shallow ones behind, so depth upgrades which fish rather than adding more.',
    stat: l => `${depthFor(l).toLocaleString('en-US')} m`,
    cost: l => Math.round(60 * Math.pow(2.45, l)),
  },
  {
    id: 'bait', name: 'Bait', icon: '🪱', x: 350, y: 70,
    needs: { id: 'net', lvl: 1 },
    blurb: 'How long a baited hook sits before something takes it. Straight throughput: halve the wait, double the fish.',
    stat: l => `${biteDelay(l).toFixed(2)}s per bite`,
    cost: l => Math.round(45 * Math.pow(2.0, l)),
  },
  {
    id: 'trader', name: 'Fishmonger', icon: '⚖', x: 350, y: 450,
    needs: { id: 'net', lvl: 2 },
    blurb: 'Who buys the catch. Multiplies everything — net hauls, rod catches, weather fish — so it never stops being worth a rung.',
    stat: l => (l === 0 ? 'market rate' : `${traderFor(l).toFixed(2)}x value`),
    cost: l => Math.round(200 * Math.pow(2.5, l)),
  },
  {
    id: 'line', name: 'Line', icon: '🧵', x: 680, y: 270,
    needs: { id: 'rod', lvl: 2 },
    blurb: 'What the line can hold before it parts. Depth alone gets you bites you cannot land — this is the track that turns them into money.',
    stat: l => `${strengthFor(l)} kg`,
    cost: l => Math.round(55 * Math.pow(2.35, l)),
  },
  {
    id: 'reel', name: 'Reel', icon: '🌀', x: 680, y: 0,
    needs: { id: 'bait', lvl: 3 },
    blurb: 'Wind speed — and the same number drops the weighted hook, at 1.5x. In deep water this is most of the round trip.',
    stat: l => `${reelFor(l)} m/s`,
    cost: l => Math.round(55 * Math.pow(2.0, l)),
  },
  {
    id: 'crew', name: 'Deckhands', icon: '🧑‍✈️', x: 680, y: 460,
    needs: { id: 'rod', lvl: 3 },
    blurb: 'More hands, more rods over the side, all fishing at once. The bluntest multiplier in the tree and priced like it.',
    stat: l => `${linesFor(l)} rod${linesFor(l) > 1 ? 's' : ''}`,
    cost: l => Math.round(2500 * Math.pow(12, l)),
  },
  {
    id: 'power', name: 'Reel Power', icon: '💪', x: 990, y: 0,
    needs: { id: 'reel', lvl: 3 },
    blurb: 'Winning the fight faster, and holding fish a little past what the line is rated for. The rod safety gate ignores the grip on purpose — it is a bonus, not a guarantee.',
    stat: l => `${fightFor(l).toFixed(2)}s · ${gripFor(l).toFixed(2)}x grip`,
    cost: l => Math.round(400 * Math.pow(2.3, l)),
  },
];

/* A track's cap is just how long its ladder is — the table is the balance. */
for (const u of UPGRADES) u.max = LADDERS[u.id].length - 1;

/** How wide and tall the tree canvas is, from the node positions themselves. */
export const NODE_W = 216, NODE_H = 132;
export const treeBounds = () => ({
  x0: Math.min(...UPGRADES.map(u => u.x)),
  y0: Math.min(...UPGRADES.map(u => u.y)),
  x1: Math.max(...UPGRADES.map(u => u.x)) + NODE_W,
  y1: Math.max(...UPGRADES.map(u => u.y)) + NODE_H,
});

/* Stat curves — all of them now just read the rung off the ladder above. */
export const depthFor = l => rungOf('rod', l).v;
export const biteDelay = l => rungOf('bait', l).v;
export const reelFor = l => rungOf('reel', l).v;
export const sinkFor = l => reelFor(l) * 1.5;      // a weighted hook drops faster than it winds
export const strengthFor = l => rungOf('line', l).v;
export const linesFor = l => rungOf('crew', l).v;
export const traderFor = l => rungOf('trader', l).v;
export const netEvery = l => rungOf('net', l).v;
export const netDepthFor = l => rungOf('net', l).d;
export const fightFor = l => rungOf('power', l).v;
export const gripFor = l => rungOf('power', l).g;

/** Everything that is in the water whatever the sky is doing. The weather fish
 *  are excluded from every guarantee below: you cannot count on a storm. */
const ALWAYS = FISH.filter(f => !f.sky);

/** The lightest thing living at a depth — whatever the line must at minimum hold
 *  for the water down there to be worth fishing at all. */
function lightestAt(d) {
  let best = Infinity;
  for (const f of ALWAYS) if (d >= f.min && d <= f.max && f.len < best) best = f.len;
  return best === Infinity ? 0 : best;
}

/** Lowest Line level that can still land *something* at a given rod level.
 *  Going deeper leaves the shallow species behind, so a rod bought too far ahead
 *  of the line would put every remaining fish over the breaking strain — no
 *  catches, no income, and no way back. The shop refuses that sale; this is the
 *  number it checks. Never exceeds L5, and the line is always far cheaper than
 *  the rod that needs it, so it costs an ordinary player nothing. */
export function lineNeededFor(rodLevel) {
  const need = lightestAt(depthFor(rodLevel));
  let l = 0;
  while (l < LINE_MAX && strengthFor(l) < need) l++;
  return l;
}

/** Heaviest ordinary species a given breaking strain can land — the shop rail
 *  prints it next to each Line rung so a rung reads as a fish, not a number. */
export function biggestUnder(kg) {
  let best = null;
  for (const f of ALWAYS) if (f.len <= kg && (!best || f.len > best.len)) best = f;
  return best ? best.name : 'nothing';
}

/** Ordinary species in reach at a rod level, for the Rod rung rail. */
export function speciesAtRod(l) {
  const d = depthFor(l);
  return ALWAYS.filter(f => d >= f.min && d <= f.max).map(f => f.name);
}
