// Board geometry, the road, and every turret / circle definition.
// Pure data + pure functions: this module never touches the DOM, so the
// simulation can be unit-tested headlessly.

export const CELL = 40, COLS = 42, ROWS = 24;
export const W = COLS * CELL, H = ROWS * CELL;
export const MAX_LEVEL = 5;

// The road: a long serpentine so a wave stays on the board for a while and one
// well-placed turret can cover two lanes. Authored in cell coordinates.
export const ROUTE = [
  [-2, 2], [4, 2], [4, 21], [10, 21], [10, 2], [16, 2], [16, 21], [22, 21],
  [22, 2], [28, 2], [28, 21], [34, 21], [34, 2], [44, 2],
];

/* `upgrades` is one entry per rank past L1 (so 4 entries → L2..L5).
   Each has a unique `name` + authored `desc` for the hover tip.
   Numeric bumps still come from the shared level formulas in state.js. */
export const TURRETS = [
  { id: 'pulse', name: 'Pulse', key: '1', cost: 40, color: '#45e6d2', kind: 'bullet',
    range: 118, rate: 0.28, dmg: 8, speed: 560,
    role: 'Basic damage',
    blurb: 'Fires fast single bullets at one circle at a time. Cheap starter tower — spam a few of these early.',
    upgrades: [
      { name: 'Overcharge', desc: 'Hot-loads the first barrel. Every shot hits harder and the turret reaches a little farther down the lane — still your cheap bread-and-butter gun, just less wimpy.' },
      { name: 'Rapid cycle', desc: 'Shortens the chamber time so Pulse spits rounds faster. Great when a pack is already in range and you need more lead on target, not a bigger boom.' },
      { name: 'Heavy cores', desc: 'Swaps in denser cores. Damage jumps hard and range keeps climbing — this is when a row of Pulses starts deleting medium circles instead of tickling them.' },
      { name: 'Pulse storm', desc: 'Max overclock. Highest damage, fastest cadence, longest reach this chassis gets. A fully stacked Pulse line can carry mid-game trash while your big guns hunt bosses.' },
    ] },
  { id: 'flak', name: 'Flak', key: '2', cost: 55, color: '#ffd166', kind: 'shotgun',
    range: 94, rate: 0.85, dmg: 8, speed: 460, pellets: 5, spread: 0.36,
    role: 'Crowd control',
    blurb: 'Shotgun spray of pellets at short range. Great against packs of small swarm circles walking together.',
    upgrades: [
      { name: 'Extra pellets', desc: 'Loads one more pellet per blast. Same choke, denser cloud — swarm packs walking the near lane take more chips every boom.' },
      { name: 'Dense pack', desc: 'Tighter powder charge: each pellet hits harder and you fire a bit quicker. Still short-range; plant it on a bend where packs clump.' },
      { name: 'Mag dump', desc: 'Bigger magazine dump. More pellets, higher damage, faster reloads — turns the choke point into a shredder for anything that walks in as a group.' },
      { name: 'Wall of lead', desc: 'Full mag-dump mode. Maximum pellets and punch for this frame. Anything that bunches up in its face melts; lone tanks still want a sniper or rail nearby.' },
    ] },
  { id: 'cryo', name: 'Cryo', key: '3', cost: 70, color: '#7ec8ff', kind: 'aura',
    range: 106, slow: 0.42,
    role: 'Slow field · no damage',
    blurb: 'Does not deal damage. Slows every enemy inside its circle so your damage towers have more time to shoot them.',
    upgrades: [
      { name: 'Deep freeze', desc: 'Colder field. Circles inside crawl harder (stronger slow %) so your DPS towers get more shots before they leave. Still zero damage from Cryo itself.' },
      { name: 'Wide field', desc: 'Pushes the freeze radius outward so more of the road sits in the chill. Cover two bends or a long straight with one well-placed dish.' },
      { name: 'Permafrost', desc: 'Slow bites deeper and the aura grows again. Late packs and bosses spend more time under fire — pair with splash or chain towers in the same pocket.' },
      { name: 'Absolute zero', desc: 'Max freeze strength and coverage. Near-park-the-pack utility; still does not kill anything alone, but everything in the ring is stuck in molasses.' },
    ] },
  { id: 'cannon', name: 'Cannon', key: '4', cost: 85, color: '#ff9f6d', kind: 'shell',
    range: 134, rate: 1.15, dmg: 24, speed: 360, splash: 46,
    role: 'Splash damage',
    blurb: 'Lobs explosive shells that hit the target and anything near it. Good vs armour and clumps.',
    upgrades: [
      { name: 'HE shells', desc: 'High-explosive warheads. Direct hits and the blast around them both hurt more — first real step from “support splash” to “clump killer.”' },
      { name: 'Blast radius', desc: 'Bigger boom radius and a bit more range. Catches the runners trailing the main pack that used to walk out of the old blast unscathed.' },
      { name: 'Quick reload', desc: 'Crew reloads faster so shells land more often, on top of the usual damage/range climb. Keeps pressure on armored blobs instead of waiting forever between booms.' },
      { name: 'Siege rounds', desc: 'Heaviest shells this barrel takes. Huge damage, fat splash, long reach — your go-to for thick clumps and armored trains on the mid-to-late road.' },
    ] },
  { id: 'venom', name: 'Venom', key: '5', cost: 95, color: '#9ff05f', kind: 'bullet',
    // Hit is weak on purpose — the poison is the payload. Poison scales hard with level
    // (see statPoison) and ignores armour, so it stays relevant on tanks late game.
    range: 126, rate: 1.0, dmg: 6, speed: 420, poison: { dps: 28, dur: 5 },
    role: 'Poison DoT',
    blurb: 'Soft hit, hard poison. Venom keeps melting HP after the shot and ignores armour — upgrade it to stack serious toxin DPS.',
    upgrades: [
      { name: 'Concentrated toxin', desc: 'Richer venom per dart. Poison DPS jumps hard (and still ignores armour) while the soft impact shot also ticks up. Tag tanks and let them melt walking away.' },
      { name: 'Lingering venom', desc: 'The toxin sticks longer and burns harder. Refreshing poison on a boss mid-walk keeps the DoT from falling off before your other guns finish the job.' },
      { name: 'Viral strain', desc: 'Aggressive strain: big poison DPS bump, longer burn, better range/rate. One or two Venoms can now carry armor-heavy waves that shrug pure bullets.' },
      { name: 'Neurotoxin', desc: 'Peak toxin load. Maximum poison DPS and duration plus full gun scaling — paint every tank and elite; armour means nothing while they cook from the inside.' },
    ] },
  { id: 'tesla', name: 'Tesla', key: '6', cost: 115, color: '#c9a2ff', kind: 'chain',
    range: 118, rate: 0.75, dmg: 11, chains: 4,
    role: 'Chain lightning',
    blurb: 'Zaps one circle, then jumps the bolt to nearby ones. Perfect when enemies walk in a line on the road.',
    upgrades: [
      { name: 'Extra jump', desc: 'The bolt chains one more hop. Lines of circles on the road all take a hit from a single zap — perfect on the long serpentine stretches.' },
      { name: 'Fat arc', desc: 'Fatter arc, harder zap. Each link in the chain deals more damage and the coil fires a bit quicker so multi-target lines stay under pressure.' },
      { name: 'Storm coil', desc: 'More jumps, more damage, more range. A single Tesla can rake a whole convoy walking single-file without needing perfect aim on each one.' },
      { name: 'Chain reaction', desc: 'Max hops and power. Storm-tier chaining that shreds parade lines; still weaker on lone bosses than a rail or sniper, but unmatched for traffic jams.' },
    ] },
  { id: 'laser', name: 'Laser', key: '7', cost: 140, color: '#ff6da9', kind: 'beam',
    range: 152, dps: 26, ramp: 2.2,
    role: 'Beam DPS',
    blurb: 'Holds a continuous beam on one target. Damage ramps up the longer it stays locked — melt single strong foes.',
    upgrades: [
      { name: 'Focus lens', desc: 'Cleaner focus: higher beam DPS and a longer lock range. The ramp still doubles damage while you stay on target — better at sticking to mid-HP elites.' },
      { name: 'Hot core', desc: 'Core runs hotter. Sustained DPS climbs hard so the beam chews through armored singles faster once the ramp kicks in.' },
      { name: 'Sustained beam', desc: 'Stabilizers keep the beam on longer targets without losing power. Big DPS and range bump — this is the “melt that tank” rank.' },
      { name: 'Meltdown', desc: 'Full thermal dump. Maximum beam DPS and reach. Park it on strong targeting and let it cook bosses while splash towers clear the trash around them.' },
    ] },
  { id: 'missile', name: 'Missile', key: '8', cost: 170, color: '#f9f871', kind: 'missile',
    range: 210, rate: 1.7, dmg: 30, speed: 250, splash: 40,
    role: 'Homing rockets',
    blurb: 'Long-range rockets that chase their target and explode for splash. Hard to miss runners.',
    upgrades: [
      { name: 'Fast lock', desc: 'Seeker heads lock and fire more often, with a damage bump. Runners that used to juke the old reload get tagged before they slip past the bend.' },
      { name: 'Dual launch', desc: 'Heavier warheads and a wider kill zone on impact. Homing still does the aiming — you just delete more of the pack around the lock.' },
      { name: 'Cluster warhead', desc: 'Cluster boom: more splash radius, more damage, better range. Ideal for late mixed packs where the primary target is fat and the escorts are close.' },
      { name: 'Barrage', desc: 'Full rocket battery. Fastest fire, hardest hits, widest blast this pod can field — long-range insurance against anything that tries to sprint the exit.' },
    ] },
  { id: 'rail', name: 'Railgun', key: '9', cost: 190, color: '#8fe3ff', kind: 'rail',
    range: 340, rate: 2.0, dmg: 80, pierce: 3, targeting: 'strong',
    role: 'Pierce · boss killer',
    blurb: 'Huge sniper line across the map. Shot punches through several enemies and prefers the strongest.',
    upgrades: [
      { name: 'Long rails', desc: 'Longer rails mean more velocity on exit. Damage and map-crossing range both climb — still prefers the strongest target in sight.' },
      { name: 'Deep pierce', desc: 'The slug punches through one more body. Lines of enemies on the same shot path all eat a hit; bosses in the back still get the “strong” priority.' },
      { name: 'Capacitor bank', desc: 'Bigger capacitors: harder hits, faster recharge between rails. Boss melting gets serious without giving up the pierce line.' },
      { name: 'Hypervelocity', desc: 'Peak rail. Maximum damage, pierce, and range. Your map-long boss eraser — expensive, slowish, and worth every coin when elites show up.' },
    ] },
  { id: 'mortar', name: 'Mortar', key: '0', cost: 230, color: '#ffb0d0', kind: 'mortar',
    range: 400, minRange: 130, rate: 2.8, dmg: 44, splash: 80,
    role: 'Long-range artillery',
    blurb: 'Lobs massive bombs very far with a huge blast. Cannot shoot anything standing too close to it.',
    upgrades: [
      { name: 'Heavy shells', desc: 'Heavier bombs, bigger craters. Damage and splash both go up — still cannot hit anything inside its minimum range, so keep it off the road edge.' },
      { name: 'Fast crew', desc: 'Loader crew speeds up. Shells land more often on top of the usual damage/range growth — less dead air between salvos on long approaches.' },
      { name: 'Cluster bombs', desc: 'Cluster payloads: much wider blast and harder hits across the far road. Ideal for packing the exit lane where you cannot place towers dense enough.' },
      { name: 'Artillery park', desc: 'Full battery. Max damage, splash, and range for this tube. Your strategic “delete that half of the map” piece — baby-sit the dead zone up close with other guns.' },
    ] },
  { id: 'amp', name: 'Amplifier', key: 'A', cost: 130, color: '#ffe08a', kind: 'buff',
    range: 98, dmgMul: 0.30, rateMul: 0.20,
    role: 'Buff nearby towers',
    blurb: 'Does not shoot. Powers up every other turret in range — more damage and faster fire. Place in the middle of a cluster.',
    upgrades: [
      { name: 'Strong field', desc: 'Hotter buff field. Nearby towers gain a larger damage and fire-rate bonus. Still does nothing alone — it only exists to juice the guns around it.' },
      { name: 'Wide aura', desc: 'Aura radius grows so more of your cluster sits inside the buff. Re-check placement after big rebuilds so edge towers are not left outside.' },
      { name: 'Overdrive', desc: 'Overdrive coils: much stronger damage and rate multipliers for everything in range. One Amp can turn a mediocre pocket into a delete zone.' },
      { name: 'Command node', desc: 'Max command aura. Highest buff strength and coverage. Drop it in the heart of your best battery and every gun around it plays a tier higher.' },
    ] },

  // ── new battery ──────────────────────────────────────────────────────────
  { id: 'flame', name: 'Flame', key: 'Q', cost: 100, color: '#ff7a3d', kind: 'flame',
    range: 88, dps: 22, cone: 0.62,
    role: 'Cone fire · napalm',
    blurb: 'Short cone of fire that shreds packs. From level 3 (Napalm) enemies keep burning after they walk out of the jet.',
    upgrades: [
      { name: 'Hotter jet', desc: 'Hotter fuel mix. Cone DPS and reach climb, and the spray fans a bit wider — better at roasting packs that only clip the edge of the jet.' },
      { name: 'Napalm', desc: 'UNLOCK: Napalm. Enemies that walk through the cone keep burning after they leave — real linger DPS on top of the jet itself. This is the rank that makes Flame a pack eraser.' },
      { name: 'Sticky fuel', desc: 'Stickier napalm: stronger burn DPS and longer burn time, plus a hotter cone. Circles leave the jet but the fire does not leave them.' },
      { name: 'Inferno', desc: 'Full inferno kit. Max cone DPS, widest spray, strongest longest napalm burn. Anything that walks the near path leaves as charcoal.' },
    ] },
  { id: 'sniper', name: 'Sniper', key: 'W', cost: 160, color: '#a8ffce', kind: 'sniper',
    range: 300, rate: 1.65, dmg: 55, targeting: 'strong',
    role: 'Single hard hits',
    blurb: 'Slow, heavy shots at long range. Prioritises big targets. From level 3 it ignores armour — boss hunter.',
    upgrades: [
      { name: 'High caliber', desc: 'Heavier slug. Big damage spike and a bit more range — still prioritises the strongest circle in view. First step from “support poke” to “elite deleter.”' },
      { name: 'AP rounds', desc: 'Armor-piercing tips in the magazine (full ignore unlocks next rank). Damage and cadence both improve so fat targets drop faster while you wait for true AP.' },
      { name: 'Armor pierce', desc: 'UNLOCK: shots ignore enemy armour. Bosses and armored trains stop shrugging the caliber — this is the rank sniper is built for.' },
      { name: 'One shot', desc: 'Match-grade load. Maximum damage, rate, and range with full armour ignore. Your dedicated boss hunter; leave trash to flak and flame.' },
    ] },
  { id: 'nova', name: 'Nova', key: 'E', cost: 145, color: '#d4a0ff', kind: 'nova',
    range: 100, rate: 1.9, dmg: 18, splash: 100,
    role: 'Omni pulse',
    blurb: 'Every few seconds, damages everything in a ring around itself. No aiming — plant it on a busy bend.',
    upgrades: [
      { name: 'Fast pulse', desc: 'Shorter cooldown between pulses. The ring hits more often, so dense bends stay under constant chip instead of long quiet gaps.' },
      { name: 'Wide nova', desc: 'Pulse radius and damage both grow. Covers more of the bend and hurts everything inside harder — still no aiming, pure plant-and-forget.' },
      { name: 'Shockwave', desc: 'Harder shockwaves, faster cadence, bigger ring. A single Nova on a busy corner can hold a lane while you micro the rest of the map.' },
      { name: 'Supernova', desc: 'Max omni pulse. Biggest ring, hardest hit, fastest beat. Drop it on the nastiest intersection and everything that walks through pays a tax.' },
    ] },
  { id: 'gatling', name: 'Gatling', key: 'R', cost: 125, color: '#c4d0e0', kind: 'gatling',
    range: 128, rate: 0.12, dmg: 3.5, speed: 640, spinUp: 1.4,
    role: 'Spin-up machine gun',
    blurb: 'Starts slow, then ramps into a wall of bullets while it has a target. Weak if enemies zip past too fast.',
    upgrades: [
      { name: 'Quick spin', desc: 'Bearings run smoother so full stream damage and rate climb sooner — less of a peashooter phase when packs first enter range.' },
      { name: 'Heavy rounds', desc: 'Heavier bullets. Each round in the stream hits much harder once spun up; range improves so it starts chewing earlier on approaching circles.' },
      { name: 'Stabilizer', desc: 'Stabilized mount: higher damage and faster full-auto once hot. Keeps the wall of lead on target through longer streams.' },
      { name: 'Minigun', desc: 'Full minigun conversion. Maximum stream damage and rate. Hold a target long enough and almost nothing walks out of the cone of fire alive.' },
    ] },

  // ── Late-game supers (BTD black hole / death ray / storm tropes) ────────
  // upgradePremium makes each rank cost more than a normal tower of the same place price.
  { id: 'singularity', name: 'Singularity', key: 'S', cost: 420, color: '#b388ff', kind: 'singularity',
    range: 132, dps: 38, slow: 0.55, upgradePremium: 1.22,
    role: 'Black hole · pull + melt',
    blurb: 'Classic black-hole tower: every circle in range is crushed and slowed hard. Stupid good on bends full of tanks — costs a fortune to place and to upgrade.',
    upgrades: [
      { name: 'Event horizon', desc: 'Wider well + hotter melt DPS. More of the bend sits inside the crush so packs that used to wade through start dying on the way in.' },
      { name: 'Tidal pull', desc: 'UNLOCK pull: enemies inside the well are dragged backward along the path (slower progress) on top of the hard slow. Tanks crawl; runners get yanked.' },
      { name: 'Gravity well', desc: 'UNLOCK armour-ignore on melt. Range expands, slow clamps harder, and shielded/armoured packs finally melt like trash.' },
      { name: 'Collapse', desc: 'UNLOCK core detonation: every few seconds the hole pulses a huge spike of damage in its inner half. Max DPS, reach, and slow — pure late-game lane lock.' },
    ] },
  { id: 'oblivion', name: 'Oblivion', key: 'O', cost: 480, color: '#ff4d6d', kind: 'oblivion',
    range: 360, rate: 2.4, dmg: 220, targeting: 'strong', upgradePremium: 1.28,
    role: 'Death ray · execute',
    blurb: 'End-game death ray. Brutal hit on the strongest target, always ignores armour. Upgrades are as expensive as the place cost — this is a boss-delete investment.',
    upgrades: [
      { name: 'Focus array', desc: 'Tighter focus: more damage and range on every ray. Still always ignores armour — your long-range anti-boss sniper.' },
      { name: 'Execute protocol', desc: 'UNLOCK execute: targets under ~18% HP die instantly to the ray. Soften with splash/Tempest, then Oblivion finishes the job.' },
      { name: 'Split beam', desc: 'UNLOCK secondary ray: after the main hit, a weaker beam (55% damage) tags the next-strongest target in range. Two elites take a hit per shot.' },
      { name: 'Annihilation', desc: 'Max death ray. Highest damage, range, execute threshold, and split-beam power. If something survives two of these, bank more gold.' },
    ] },
  { id: 'tempest', name: 'Tempest', key: 'Y', cost: 390, color: '#5ce1e6', kind: 'tempest',
    range: 280, rate: 1.35, dmg: 55, strikes: 5, upgradePremium: 1.18,
    role: 'Storm strikes',
    blurb: 'Calls lightning on several random enemies in a huge radius every pulse — no aiming. Place cost is high; ranks stay expensive so you build one or two, not a forest.',
    upgrades: [
      { name: 'More bolts', desc: 'Extra bolts per pulse and more damage each. Random strikes cover more of the packed road without you picking targets.' },
      { name: 'Forked sky', desc: 'UNLOCK fork: each bolt has a chance to jump to a nearby enemy for 60% damage. Dense packs take chain tax automatically.' },
      { name: 'Chain sky', desc: 'UNLOCK armour-ignore on every bolt. Armoured/tank packs finally pay the storm tax like everyone else; more bolts and range too.' },
      { name: 'Cataclysm', desc: 'UNLOCK shock: struck enemies are briefly stunned (progress freeze). Max bolts, damage, and range — delete parades of late-game trash.' },
    ] },
];

export const TDEF = Object.fromEntries(TURRETS.map(t => [t.id, t]));

export const ENEMIES = {
  // Small trash — leave these zippy and light.
  grunt:  { name: 'Grunt',    r: 5,   hp: 28,   spd: 104, gold: 4,   color: '#63e8ff' },
  runner: { name: 'Runner',   r: 4,   hp: 20,   spd: 190, gold: 4,   color: '#ffe066' },
  swarm:  { name: 'Swarm',    r: 3.4, hp: 12,   spd: 134, gold: 1,   color: '#ff8fd0' },
  // Medium packs (armor / shield ring / medic / split / phase). These used to
  // stack with hpScale into brick walls when ~a dozen+ walked the road at once.
  // Cut hard so a mid-game battery clears them without a nap; boss/tank still soak.
  armor:  { name: 'Armored',  r: 7,   hp: 48,   spd: 82,  gold: 8,   color: '#9aa7c7', armor: 3 },
  shield: { name: 'Shielded', r: 6,   hp: 34,   spd: 96,  gold: 8,   color: '#8ea2ff', shield: 32 },
  medic:  { name: 'Medic',    r: 6,   hp: 40,   spd: 88,  gold: 10,  color: '#8affc1', heals: true },
  split:  { name: 'Splitter', r: 6.5, hp: 38,   spd: 96,  gold: 7,   color: '#c89bff', splits: 3 },
  mini:   { name: 'Splinter', r: 4,   hp: 16,   spd: 144, gold: 1,   color: '#d9bcff' },
  phase:  { name: 'Phase',    r: 5.5, hp: 42,   spd: 112, gold: 9,   color: '#ff7a7a', phases: true },
  // Big singles — still chunky, not pack spam.
  tank:   { name: 'Tank',     r: 9,   hp: 240,  spd: 62,  gold: 22,  color: '#5f7cff', armor: 5, lives: 2 },
  // Bosses used to soak entire batteries; they should still feel heavy, not immortal.
  boss:   { name: 'Boss',     r: 15,  hp: 1200, spd: 56,  gold: 140, color: '#ff5470', armor: 5, lives: 3, boss: true },
};

// Difficulty curve. Early is gentle; after ~wave 15 HP accelerates hard so
// level-5 supers and 5k+ gold can't AFK the rest of the run.
// wave 10 ≈ 2.6× · wave 20 ≈ 6.3× · wave 30 ≈ 14× · wave 40 ≈ 28×
export const hpScale = w => {
  const t = Math.max(0, w - 1);
  const early = 1 + 0.15 * t + 0.006 * t * t;
  // Extra late-game term kicks in after wave 15.
  const late = w > 15 ? Math.pow(1.085, w - 15) : 1;
  return early * late;
};

// Speed also keeps rising later — old cap at 1.4 made late packs feel sluggish.
export const spdScale = w => Math.min(1.85, 1 + 0.008 * (w - 1) + (w > 20 ? (w - 20) * 0.012 : 0));

/** Flat armour bonus that grows slowly so late armoured packs don't face free ignore. */
export const armorScale = w => Math.floor(Math.max(0, w - 8) / 6);

export const START_LIVES = 20;
export const START_GOLD = 260;
