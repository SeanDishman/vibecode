// data.js — every balance table in the game: terrain, resources, buildings,
// units, the research tree and flavour names. Pure data, no behaviour.

import { T } from './core.js';

export const ERAS = ['Ancient', 'Classical', 'Medieval', 'Renaissance', 'Industrial', 'Modern'];

/** The tech that reveals oil on the map and unlocks everything that burns it. */
export const OIL_TECH = 'combustion';
/** Researching this wins the game. */
export const VICTORY_TECH = 'space';

/* `warTurn` is the turn rivals first mount offensives. Without it a new player
   who is still reading the build menu gets rushed off the map before they have
   placed a second city. */
export const DIFFS = [
  { name: 'Chieftain', rivals: 3, aiGold: 0.75, aiSci: 0.80, aggression: 0.45, warTurn: 90 },
  { name: 'Warlord',   rivals: 3, aiGold: 1.00, aiSci: 1.00, aggression: 0.90, warTurn: 55 },
  { name: 'Emperor',   rivals: 4, aiGold: 1.35, aiSci: 1.30, aggression: 1.30, warTurn: 32 },
];

/* ── terrain ──────────────────────────────────────────────────────────────
   `move` is the movement cost multiplier; food/gold are the per-turn yield a
   citizen pulls out of the tile. Colours are muted so empire tints stay legible. */
export const TERRAIN = [
  { id: T.DEEP,     name: 'Deep ocean', col: '#122a4d', move: 1.30, food: 0, gold: 0 },
  { id: T.OCEAN,    name: 'Ocean',      col: '#17395f', move: 1.15, food: 1, gold: 0 },
  { id: T.SHALLOW,  name: 'Coast',      col: '#22608c', move: 1.00, food: 2, gold: 1 },
  { id: T.BEACH,    name: 'Sand',       col: '#cdb684', move: 1.00, food: 0, gold: 0 },
  { id: T.GRASS,    name: 'Grassland',  col: '#4a8443', move: 1.00, food: 3, gold: 0 },
  { id: T.PLAINS,   name: 'Plains',     col: '#79974a', move: 0.95, food: 2, gold: 1 },
  { id: T.SAVANNA,  name: 'Savanna',    col: '#93974f', move: 0.95, food: 2, gold: 0 },
  { id: T.FOREST,   name: 'Forest',     col: '#2e6238', move: 1.55, food: 2, gold: 1 },
  { id: T.JUNGLE,   name: 'Jungle',     col: '#235a30', move: 1.85, food: 2, gold: 0 },
  { id: T.MARSH,    name: 'Wetland',    col: '#456347', move: 1.70, food: 2, gold: 0 },
  { id: T.HILLS,    name: 'Hills',      col: '#63713f', move: 1.70, food: 1, gold: 2 },
  { id: T.MOUNTAIN, name: 'Mountains',  col: '#6f7078', move: 2.80, food: 0, gold: 2 },
  { id: T.PEAK,     name: 'Peaks',      col: '#a9adb6', move: 4.00, food: 0, gold: 1 },
  { id: T.DESERT,   name: 'Desert',     col: '#cbb173', move: 1.15, food: 0, gold: 0 },
  { id: T.TUNDRA,   name: 'Tundra',     col: '#7f8f83', move: 1.20, food: 1, gold: 0 },
  { id: T.SNOW,     name: 'Snow',       col: '#ccd6dd', move: 1.55, food: 0, gold: 0 },
];

/** Defensive terrain bonus for whoever is standing on it. */
export const TERRAIN_DEF = {
  [T.FOREST]: 0.25, [T.JUNGLE]: 0.25, [T.HILLS]: 0.35,
  [T.MOUNTAIN]: 0.50, [T.PEAK]: 0.50, [T.MARSH]: -0.15,
};

export const RESOURCES = [
  null,
  { key: 'FISH',   name: 'Fish',   food: 3, gold: 1, sci: 0, col: '#7fe3d0' },
  { key: 'WHEAT',  name: 'Wheat',  food: 3, gold: 0, sci: 0, col: '#ffd96b' },
  { key: 'GAME',   name: 'Game',   food: 2, gold: 1, sci: 0, col: '#c98b52' },
  { key: 'ORE',    name: 'Iron',   food: 0, gold: 3, sci: 0, col: '#c2ccd8' },
  { key: 'GEMS',   name: 'Gems',   food: 0, gold: 5, sci: 1, col: '#ff8fd0' },
  { key: 'STONE',  name: 'Stone',  food: 0, gold: 2, sci: 0, col: '#9aa3ad' },
  { key: 'HORSES', name: 'Horses', food: 1, gold: 2, sci: 0, col: '#e0b184' },
  { key: 'OIL',    name: 'Oil',    food: 0, gold: 2, sci: 0, col: '#2b2b33', strategic: true },
];

/* ── buildings ────────────────────────────────────────────────────────────
   `on` limits terrain for tile buildings; `city: true` means it sits in the city
   centre instead. `coast` / `needs` are extra requirements. Yields are per turn. */
export const BUILDINGS = [
  { id: 'house',    name: 'Huts',        cost: 16,  tech: null,           era: 0,
    on: [T.GRASS, T.PLAINS, T.SAVANNA, T.BEACH, T.TUNDRA, T.FOREST, T.HILLS, T.DESERT],
    gold: 1, housing: 3, desc: 'Homes for +3 people' },
  // Housing upgrade chain. Researching the next tier does NOT need re-placing:
  // existing homes are marked for upgrade and villagers physically walk over and
  // rebuild them (see applyHousingUpgrades in turn.js). Each tier supersedes the
  // one below it in the build palette so the list never fills up with obsolete
  // versions of the same thing.
  { id: 'cottage',  name: 'Cottages',    cost: 40,  tech: 'masonry',      era: 1, upgradeFrom: 'house',
    on: [T.GRASS, T.PLAINS, T.SAVANNA, T.BEACH, T.TUNDRA, T.FOREST, T.HILLS, T.DESERT],
    gold: 2, housing: 6, desc: 'Stone-footed homes — +6 people, +2 gold' },
  { id: 'manor',    name: 'Town Houses', cost: 95,  tech: 'banking',      era: 2, upgradeFrom: 'cottage',
    on: [T.GRASS, T.PLAINS, T.SAVANNA, T.BEACH, T.TUNDRA, T.FOREST, T.HILLS, T.DESERT],
    gold: 4, housing: 10, desc: 'Two storeys — +10 people, +4 gold' },
  { id: 'apartment',name: 'Apartments',  cost: 210, tech: 'industrial',   era: 4, upgradeFrom: 'manor',
    on: [T.GRASS, T.PLAINS, T.SAVANNA, T.BEACH, T.TUNDRA, T.FOREST, T.HILLS, T.DESERT],
    gold: 6, housing: 18, desc: 'Tenement blocks — +18 people, +6 gold' },

  { id: 'farm',     name: 'Farm',        cost: 22,  tech: 'agriculture',  era: 0,
    on: [T.GRASS, T.PLAINS, T.SAVANNA], food: 3, riverBonus: 1, desc: '+3 food (+1 beside a river)' },
  { id: 'fishery',  name: 'Fishing Hut', cost: 24,  tech: 'fishing',      era: 0,
    on: [T.BEACH, T.GRASS, T.PLAINS, T.TUNDRA, T.MARSH], coast: true,
    food: 3, gold: 1, desc: '+3 food, +1 gold on the shore' },
  { id: 'lumber',   name: 'Lumber Camp', cost: 26,  tech: 'woodworking',  era: 0,
    on: [T.FOREST, T.JUNGLE], gold: 2, food: 1, desc: '+2 gold, +1 food from the woods' },
  { id: 'mine',     name: 'Mine',        cost: 34,  tech: 'mining',       era: 0,
    on: [T.HILLS, T.MOUNTAIN, T.PEAK], gold: 3, desc: '+3 gold out of the rock' },
  { id: 'quarry',   name: 'Quarry',      cost: 32,  tech: 'masonry',      era: 0,
    on: [T.HILLS, T.MOUNTAIN, T.DESERT, T.PLAINS], gold: 2, desc: '+2 gold' },
  { id: 'pasture',  name: 'Pasture',     cost: 28,  tech: 'horseback',    era: 0,
    on: [T.GRASS, T.PLAINS, T.SAVANNA, T.TUNDRA], food: 2, gold: 1, desc: '+2 food, +1 gold' },

  // Fortifies a single tile on a shared enemy frontier. Invaders crawl through
  // completed segments; defenders fighting on them dig in hard.
  { id: 'borderwall', name: 'Border Wall', cost: 18, tech: 'masonry',      era: 1,
    on: [T.GRASS, T.PLAINS, T.SAVANNA, T.BEACH, T.TUNDRA, T.FOREST, T.JUNGLE,
         T.HILLS, T.DESERT, T.SNOW, T.MARSH],
    wall: true, desc: 'On the enemy border — slows invaders, shields your troops' },

  { id: 'granary',  name: 'Granary',     cost: 46,  tech: 'pottery',      era: 1, city: true,
    foodPct: 0.25, desc: '+25% food in this city' },
  { id: 'barracks', name: 'Barracks',    cost: 60,  tech: 'bronze',       era: 1, city: true,
    unitCap: 3, vet: 0.15, desc: '+3 army cap, +15% unit strength' },
  { id: 'walls',    name: 'Walls',       cost: 52,  tech: 'masonry',      era: 1, city: true,
    def: 0.60, desc: '+60% city defence' },
  { id: 'market',   name: 'Market',      cost: 64,  tech: 'currency',     era: 1, city: true,
    gold: 4, goldPct: 0.20, desc: '+4 gold, +20% city gold' },
  { id: 'library',  name: 'Library',     cost: 68,  tech: 'writing',      era: 1, city: true,
    sci: 3, desc: '+3 science' },
  { id: 'harbor',   name: 'Harbour',     cost: 58,  tech: 'sailing',      era: 1, city: true, coast: true,
    food: 2, gold: 2, ships: true, desc: '+2 food, +2 gold — unlocks boats here' },
  { id: 'shrine',   name: 'Shrine',      cost: 44,  tech: 'mysticism',    era: 1, city: true,
    sci: 1, culture: 2, desc: '+1 science, +2 culture (borders grow)' },
  { id: 'temple',   name: 'Temple',      cost: 78,  tech: 'philosophy',   era: 1, city: true,
    culture: 4, sci: 1, desc: '+4 culture, +1 science' },
  { id: 'aqueduct', name: 'Aqueduct',    cost: 86,  tech: 'engineering',  era: 1, city: true,
    housing: 5, growPct: 0.30, desc: '+5 housing, +30% growth' },

  { id: 'smith',    name: 'Blacksmith',  cost: 92,  tech: 'ironworking',  era: 2, city: true,
    gold: 2, vet: 0.15, desc: '+2 gold, +15% unit strength' },
  { id: 'windmill', name: 'Windmill',    cost: 88,  tech: 'machinery',    era: 2,
    on: [T.HILLS, T.PLAINS, T.GRASS], food: 3, gold: 1, desc: '+3 food, +1 gold' },
  { id: 'castle',   name: 'Castle',      cost: 130, tech: 'feudalism',    era: 2, city: true,
    def: 1.00, unitCap: 4, desc: '+100% defence, +4 army cap' },
  { id: 'shipyard', name: 'Shipyard',    cost: 120, tech: 'shipbuilding', era: 2, city: true, coast: true,
    needs: 'harbor', gold: 2, ships: true, navy: true, desc: 'Builds warships, +2 gold (needs a Harbour)' },
  { id: 'univ',     name: 'University',  cost: 145, tech: 'education',    era: 2, city: true,
    needs: 'library', sci: 6, sciPct: 0.25, desc: '+6 science, +25% city science (needs a Library)' },
  { id: 'cathedral',name: 'Cathedral',   cost: 160, tech: 'theology',     era: 2, city: true,
    needs: 'temple', culture: 8, sci: 2, desc: '+8 culture, +2 science (needs a Temple)' },

  { id: 'bank',     name: 'Bank',        cost: 175, tech: 'banking',      era: 3, city: true,
    needs: 'market', gold: 7, goldPct: 0.25, desc: '+7 gold, +25% city gold (needs a Market)' },
  { id: 'armory',   name: 'Armoury',     cost: 190, tech: 'gunpowder',    era: 3, city: true,
    needs: 'barracks', unitCap: 4, vet: 0.25, desc: '+4 army cap, +25% unit strength' },
  { id: 'observ',   name: 'Observatory', cost: 210, tech: 'astronomy',    era: 3, city: true,
    needs: 'univ', sci: 9, desc: '+9 science (needs a University)' },

  // ── Industrial
  { id: 'factory',  name: 'Factory',     cost: 240, tech: 'industrial',   era: 4, city: true,
    gold: 8, goldPct: 0.20, unitCap: 3, desc: '+8 gold, +20% city gold, +3 army cap' },
  { id: 'hospital', name: 'Hospital',    cost: 200, tech: 'sanitation',   era: 4, city: true,
    housing: 8, growPct: 0.35, desc: '+8 housing, +35% growth' },
  { id: 'power',    name: 'Power Plant', cost: 280, tech: 'electricity',  era: 4, city: true,
    needs: 'factory', gold: 6, sci: 4, goldPct: 0.15, desc: '+6 gold, +4 science (needs a Factory)' },
  { id: 'railway',  name: 'Railway',     cost: 190, tech: 'railroad',     era: 4,
    on: [T.GRASS, T.PLAINS, T.SAVANNA, T.HILLS, T.DESERT, T.TUNDRA, T.BEACH],
    gold: 4, desc: '+4 gold along the line' },

  // ── Modern. The oil well is the gateway to everything with an engine.
  { id: 'oilwell',  name: 'Oil Well',    cost: 150, tech: 'combustion',   era: 5,
    on: [T.DESERT, T.MARSH, T.TUNDRA, T.HILLS, T.PLAINS, T.SNOW, T.SAVANNA, T.GRASS], res: 'OIL',
    gold: 3, oil: 4, desc: '+4 oil, +3 gold — must sit on an oil field' },
  { id: 'refinery', name: 'Refinery',    cost: 300, tech: 'massprod',     era: 5, city: true,
    needs: 'factory', gold: 5, oil: 2, desc: '+2 oil, +5 gold (needs a Factory)' },
  { id: 'airfield', name: 'Airfield',    cost: 320, tech: 'flight',       era: 5, city: true,
    air: true, gold: 2, desc: 'Lets this city build aircraft' },
  { id: 'lab',      name: 'Research Lab',cost: 380, tech: 'computers',    era: 5, city: true,
    needs: 'observ', sci: 14, sciPct: 0.30, desc: '+14 science, +30% city science (needs an Observatory)' },
];
export const BLD = Object.fromEntries(BUILDINGS.map(b => [b.id, b]));

/* ── units ────────────────────────────────────────────────────────────────
   `atk` is strength; ranged units strike from `range` tiles and take no reply.
   `pop` is how many citizens leave the city to form the unit. */
export const UNITS = [
  { id: 'settler',  name: 'Settler',     cost: 55,  pop: 2, tech: null,           atk: 0,  hp: 30, spd: 1.05, sight: 4, role: 'settler',
    desc: 'Walk somewhere new and found a city' },
  { id: 'warrior',  name: 'Warrior',     cost: 28,  pop: 1, tech: null,           atk: 6,  hp: 34, spd: 1.00, sight: 3, role: 'melee',
    desc: 'Cheap early muscle' },
  { id: 'archer',   name: 'Archer',      cost: 40,  pop: 1, tech: 'archery',      atk: 7,  hp: 26, spd: 0.95, sight: 4, range: 3, role: 'ranged',
    desc: 'Shoots from 3 tiles, takes no return blow' },
  { id: 'spearman', name: 'Spearman',    cost: 44,  pop: 1, tech: 'bronze',       atk: 9,  hp: 44, spd: 0.95, sight: 3, role: 'melee', vsCav: 1.6,
    desc: 'Tough line infantry, brutal against horses' },
  { id: 'horseman', name: 'Horseman',    cost: 58,  pop: 1, tech: 'horseback',    atk: 11, hp: 40, spd: 1.85, sight: 5, role: 'melee', cav: true,
    desc: 'Fast raider — run down settlers and siege' },
  { id: 'sword',    name: 'Swordsman',   cost: 70,  pop: 1, tech: 'ironworking',  atk: 15, hp: 55, spd: 1.00, sight: 3, role: 'melee',
    desc: 'Heavy infantry' },
  { id: 'catapult', name: 'Catapult',    cost: 90,  pop: 1, tech: 'mathematics',  atk: 12, hp: 30, spd: 0.60, sight: 3, range: 4, role: 'siege', vsCity: 3.0,
    desc: 'Slow, fragile, and murder on city walls' },
  { id: 'knight',   name: 'Knight',      cost: 108, pop: 1, tech: 'chivalry',     atk: 22, hp: 68, spd: 1.75, sight: 5, role: 'melee', cav: true,
    desc: 'Armoured shock cavalry' },
  { id: 'crossbow', name: 'Crossbowman', cost: 96,  pop: 1, tech: 'machinery',    atk: 18, hp: 40, spd: 0.95, sight: 4, range: 4, role: 'ranged',
    desc: 'Long-ranged and hits hard' },
  { id: 'musket',   name: 'Musketeer',   cost: 130, pop: 1, tech: 'gunpowder',    atk: 28, hp: 82, spd: 1.00, sight: 4, range: 2, role: 'ranged',
    desc: 'Gunpowder line infantry' },
  { id: 'cannon',   name: 'Cannon',      cost: 165, pop: 1, tech: 'metallurgy',   atk: 26, hp: 45, spd: 0.65, sight: 3, range: 5, role: 'siege', vsCity: 3.5,
    desc: 'Flattens walls from five tiles away' },

  // ── ships. Built only in a coastal city; warships also need a Shipyard.
  { id: 'trireme',  name: 'Trireme',     cost: 78,  pop: 1, tech: 'shipbuilding', atk: 12, hp: 48, spd: 2.00, sight: 6, range: 2, role: 'ship', sea: true, needBld: 'shipyard',
    desc: 'Patrols the coast and sinks loaded transports' },
  { id: 'frigate',  name: 'Frigate',     cost: 155, pop: 1, tech: 'navigation',   atk: 26, hp: 78, spd: 2.30, sight: 7, range: 4, role: 'ship', sea: true, needBld: 'shipyard', vsCity: 1.8,
    desc: 'Ocean-going gun platform, bombards coastal cities' },

  // ── Industrial
  { id: 'rifleman', name: 'Rifleman',    cost: 175, pop: 1, tech: 'rifling',      atk: 38, hp: 100, spd: 1.05, sight: 4, range: 2, role: 'ranged',
    desc: 'Breech-loading line infantry' },
  { id: 'artillery',name: 'Artillery',   cost: 230, pop: 1, tech: 'ballistics',   atk: 40, hp: 55,  spd: 0.70, sight: 4, range: 6, role: 'siege', vsCity: 3.2,
    desc: 'Shells cities from six tiles away' },
  { id: 'ironclad', name: 'Ironclad',    cost: 250, pop: 1, tech: 'steam',        atk: 40, hp: 130, spd: 2.20, sight: 7, range: 4, role: 'ship', sea: true, needBld: 'shipyard', vsCity: 2.0,
    desc: 'Steam-driven armoured warship' },

  // ── Modern. Everything below burns oil: it costs oil to field and 1 oil a
  //    turn to keep running, and a dry treasury means it fights at 55%.
  { id: 'infantry', name: 'Infantry',    cost: 210, pop: 1, tech: 'massprod',     atk: 46, hp: 125, spd: 1.10, sight: 4, range: 2, role: 'ranged',
    desc: 'Modern conscript infantry' },
  { id: 'tank',     name: 'Tank',        cost: 320, pop: 1, tech: 'armor',        atk: 72, hp: 165, spd: 2.10, sight: 5, role: 'melee', cav: true, oil: 12, oilUp: 1, vsCity: 1.5,
    desc: 'Fast armour that rolls straight over a border' },
  { id: 'destroyer',name: 'Destroyer',   cost: 300, pop: 1, tech: 'combustion',   atk: 48, hp: 140, spd: 3.00, sight: 8, range: 4, role: 'ship', sea: true, needBld: 'shipyard', oil: 10, oilUp: 1,
    desc: 'Fast oil-fired warship' },
  { id: 'battleship',name:'Battleship',  cost: 480, pop: 2, tech: 'steel',        atk: 90, hp: 260, spd: 2.10, sight: 9, range: 7, role: 'ship', sea: true, needBld: 'shipyard', oil: 20, oilUp: 2, vsCity: 2.4,
    desc: 'Floating fortress — flattens coastal cities from range' },
  { id: 'fighter',  name: 'Fighter',     cost: 340, pop: 1, tech: 'flight',       atk: 60, hp: 90,  spd: 4.20, sight: 9, range: 3, role: 'air', air: true, needBld: 'airfield', oil: 12, oilUp: 1,
    desc: 'Flies over anything; cannot hold ground' },
  { id: 'bomber',   name: 'Bomber',      cost: 460, pop: 1, tech: 'radio',        atk: 85, hp: 110, spd: 3.20, sight: 8, range: 4, role: 'air', air: true, needBld: 'airfield', oil: 18, oilUp: 2, vsCity: 3.0,
    desc: 'Levels cities from the air, but can never take one' },
];
export const UNI = Object.fromEntries(UNITS.map(u => [u.id, u]));

/* ── research tree ────────────────────────────────────────────────────────── */
export const TECHS = [
  { id: 'agriculture', name: 'Agriculture',       era: 0, cost: 28,  req: [],                          eff: 'Farms' },
  { id: 'fishing',     name: 'Fishing',           era: 0, cost: 30,  req: [],                          eff: 'Fishing huts' },
  { id: 'mining',      name: 'Mining',            era: 0, cost: 46,  req: [],                          eff: 'Mines' },
  { id: 'woodworking', name: 'Woodworking',       era: 0, cost: 40,  req: ['agriculture'],             eff: 'Lumber camps' },
  { id: 'pottery',     name: 'Pottery',           era: 0, cost: 54,  req: ['agriculture'],             eff: 'Granaries' },
  { id: 'archery',     name: 'Archery',           era: 0, cost: 58,  req: ['woodworking'],             eff: 'Archers' },
  { id: 'masonry',     name: 'Masonry',           era: 0, cost: 66,  req: ['mining'],                  eff: 'Quarries, city walls, border walls' },
  { id: 'horseback',   name: 'Horseback Riding',  era: 0, cost: 74,  req: ['agriculture'],             eff: 'Pastures, horsemen' },
  { id: 'bronze',      name: 'Bronze Working',    era: 0, cost: 80,  req: ['mining'],                  eff: 'Spearmen, barracks' },
  { id: 'sailing',     name: 'Sailing',           era: 0, cost: 82,  req: ['fishing', 'woodworking'],  eff: 'Harbours — troops can cross coastal water' },
  { id: 'writing',     name: 'Writing',           era: 0, cost: 90,  req: ['pottery'],                 eff: 'Libraries' },

  { id: 'mysticism',   name: 'Mysticism',         era: 1, cost: 110, req: ['writing'],                 eff: 'Shrines' },
  { id: 'currency',    name: 'Currency',          era: 1, cost: 125, req: ['bronze', 'pottery'],       eff: 'Markets' },
  { id: 'mathematics', name: 'Mathematics',       era: 1, cost: 145, req: ['masonry', 'writing'],      eff: 'Catapults' },
  { id: 'ironworking', name: 'Iron Working',      era: 1, cost: 160, req: ['bronze'],                  eff: 'Swordsmen, blacksmiths' },
  { id: 'shipbuilding',name: 'Shipbuilding',      era: 1, cost: 175, req: ['sailing'],                 eff: 'Triremes, shipyards, open sea' },
  { id: 'philosophy',  name: 'Philosophy',        era: 1, cost: 190, req: ['mysticism'],               eff: 'Temples' },
  { id: 'engineering', name: 'Engineering',       era: 1, cost: 210, req: ['mathematics'],             eff: 'Aqueducts' },

  { id: 'feudalism',   name: 'Feudalism',         era: 2, cost: 250, req: ['ironworking'],             eff: 'Castles' },
  { id: 'machinery',   name: 'Machinery',         era: 2, cost: 280, req: ['engineering'],             eff: 'Crossbowmen, windmills' },
  { id: 'education',   name: 'Education',         era: 2, cost: 300, req: ['philosophy'],              eff: 'Universities' },
  { id: 'chivalry',    name: 'Chivalry',          era: 2, cost: 330, req: ['feudalism', 'horseback'],  eff: 'Knights' },
  { id: 'theology',    name: 'Theology',          era: 2, cost: 350, req: ['philosophy'],              eff: 'Cathedrals' },
  { id: 'navigation',  name: 'Navigation',        era: 2, cost: 370, req: ['shipbuilding', 'mathematics'], eff: 'Frigates, deep ocean' },
  { id: 'banking',     name: 'Banking',           era: 2, cost: 400, req: ['currency', 'education'],   eff: 'Banks' },

  { id: 'gunpowder',   name: 'Gunpowder',         era: 3, cost: 520, req: ['machinery', 'ironworking'],eff: 'Musketeers, armouries' },
  { id: 'astronomy',   name: 'Astronomy',         era: 3, cost: 560, req: ['education', 'navigation'], eff: 'Observatories' },
  { id: 'metallurgy',  name: 'Metallurgy',        era: 3, cost: 640, req: ['gunpowder'],               eff: 'Cannon' },
  { id: 'printing',    name: 'Printing Press',    era: 3, cost: 700, req: ['astronomy', 'banking'],    eff: '+35% science in every city' },
  { id: 'enlighten',   name: 'The Enlightenment', era: 3, cost: 900, req: ['printing', 'theology'],    eff: '+20% gold and science empire-wide' },

  // Costs tuned against a measured run: at the old prices the first tank landed
  // around turn 1040 (nearly an hour at 1×), so most games ended before anyone
  // saw the modern era at all. These bring the milestones in by about a third.
  { id: 'steam',       name: 'Steam Power',       era: 4, cost: 650,  req: ['metallurgy', 'printing'],   eff: 'Ironclads' },
  { id: 'rifling',     name: 'Rifling',           era: 4, cost: 700,  req: ['gunpowder', 'machinery'],   eff: 'Riflemen' },
  { id: 'industrial',  name: 'Industrialisation', era: 4, cost: 800,  req: ['steam', 'banking'],         eff: 'Factories' },
  { id: 'railroad',    name: 'Railroad',          era: 4, cost: 850,  req: ['steam', 'industrial'],      eff: 'Railways' },
  { id: 'sanitation',  name: 'Sanitation',        era: 4, cost: 880,  req: ['industrial'],               eff: 'Hospitals' },
  { id: 'ballistics',  name: 'Ballistics',        era: 4, cost: 950,  req: ['rifling', 'metallurgy'],    eff: 'Artillery' },
  { id: 'electricity', name: 'Electricity',       era: 4, cost: 1050, req: ['industrial', 'enlighten'],  eff: 'Power plants' },

  { id: 'combustion',  name: 'Combustion',        era: 5, cost: 1250, req: ['electricity', 'ballistics'],eff: 'Reveals OIL · oil wells, destroyers' },
  { id: 'steel',       name: 'Steel',             era: 5, cost: 1350, req: ['railroad', 'combustion'],   eff: 'Battleships' },
  { id: 'massprod',    name: 'Mass Production',   era: 5, cost: 1450, req: ['combustion', 'sanitation'], eff: 'Refineries, modern infantry' },
  { id: 'armor',       name: 'Armoured Warfare',  era: 5, cost: 1600, req: ['steel', 'massprod'],        eff: 'Tanks' },
  { id: 'flight',      name: 'Flight',            era: 5, cost: 1650, req: ['combustion', 'steel'],      eff: 'Airfields, fighters' },
  { id: 'radio',       name: 'Radio',             era: 5, cost: 1750, req: ['flight', 'electricity'],    eff: 'Bombers' },
  { id: 'computers',   name: 'Computers',         era: 5, cost: 1950, req: ['radio', 'massprod'],        eff: 'Research labs' },
  // Deliberately far beyond its neighbours: measurement showed a runaway empire
  // otherwise blew through the whole modern tree and won inside ~20 turns, so
  // nobody ever got to actually use the tanks and aircraft they had just unlocked.
  { id: 'space',       name: 'The Space Race',    era: 5, cost: 5200, req: ['computers', 'armor'],       eff: 'Wins the game' },
];
export const TECH = Object.fromEntries(TECHS.map(t => [t.id, t]));

/* ── flavour ──────────────────────────────────────────────────────────────── */
/** Enough banners for a crowded world. The player always takes the first. */
export const EMPIRE_NAMES = [
  { name: 'Aurelia',    adj: 'Aurelian',    col: '#45e6d2' },
  { name: 'Karnath',    adj: 'Karnathi',    col: '#ff6da9' },
  { name: 'Veldros',    adj: 'Veldrosi',    col: '#ffd166' },
  { name: 'Solmara',    adj: 'Solmaran',    col: '#9d7cff' },
  { name: 'Tzakan',     adj: 'Tzakani',     col: '#ff9d5c' },
  { name: 'Bryndal',    adj: 'Bryndali',    col: '#7ee787' },
  { name: 'Ostrava',    adj: 'Ostravan',    col: '#5aa9ff' },
  { name: 'Malduun',    adj: 'Malduuni',    col: '#ff5f5f' },
  { name: 'Ysolde',     adj: 'Ysoldi',      col: '#d67cff' },
  { name: 'Ravengar',   adj: 'Ravengari',   col: '#b8e04a' },
  { name: 'Cirenne',    adj: 'Cirennian',   col: '#ffb3c7' },
  { name: 'Hollowmark', adj: 'Hollowmarch', col: '#4ad6a0' },
  { name: 'Zarrun',     adj: 'Zarruni',     col: '#e8734a' },
  { name: 'Nyxheim',    adj: 'Nyxheimer',   col: '#a0b4ff' },
];

/** How many rivals the start menu offers. */
export const RIVAL_COUNTS = [3, 6, 9, 12];

/* ── national character ───────────────────────────────────────────────────
   Every rival rolls a personality and a power tier. These multiply straight into
   its economy and its war appetite, so the world contains genuine soft targets
   and genuine threats instead of N copies of the same opponent. `threat` is only
   used to describe a nation to the player. */
export const PERSONALITIES = [
  { id: 'warlike', name: 'Warlike',    aggr: 1.70, gold: 1.00, sci: 0.85, expand: 1.05, army: 1.40,
    blurb: 'Raises armies early and uses them' },
  { id: 'builder', name: 'Builders',   aggr: 0.60, gold: 1.15, sci: 1.15, expand: 1.20, army: 0.80,
    blurb: 'Grows tall, fights late' },
  { id: 'trader',  name: 'Merchants',  aggr: 0.70, gold: 1.40, sci: 1.00, expand: 1.00, army: 0.80,
    blurb: 'Wealthy, and lightly defended' },
  { id: 'scholar', name: 'Scholars',   aggr: 0.55, gold: 0.90, sci: 1.50, expand: 0.90, army: 0.70,
    blurb: 'Races up the tech tree, weak on the ground' },
  { id: 'nomad',   name: 'Nomads',     aggr: 1.20, gold: 0.85, sci: 0.80, expand: 1.55, army: 1.10,
    blurb: 'Sprawls fast, spread thin' },
  { id: 'zealot',  name: 'Zealots',    aggr: 1.35, gold: 0.95, sci: 0.95, expand: 1.10, army: 1.20,
    blurb: 'Expands by the sword' },
  { id: 'fading',  name: 'Fading',     aggr: 0.30, gold: 0.60, sci: 0.65, expand: 0.55, army: 0.45,
    blurb: 'A crumbling realm, ripe for the taking' },
];

/** Power tiers, rolled per nation. `weight` biases how often each shows up. */
export const POWERS = [
  { id: 'minor',    name: 'Minor realm',  mult: 0.55, weight: 3, units: -1 },
  { id: 'regional', name: 'Regional power', mult: 0.85, weight: 4, units: 0 },
  { id: 'major',    name: 'Major power',  mult: 1.15, weight: 3, units: 0 },
  { id: 'great',    name: 'Great power',  mult: 1.55, weight: 2, units: 1 },
];

export const CITY_NAMES = [
  'Emberhold', 'Silverreach', 'Corvath', 'Duskvale', 'Rhoswen', 'Ashmoor', 'Thorngate',
  'Var Kesh', 'Wyndmere', 'Highfen', 'Sarnath', 'Oldwater', 'Ironhollow', 'Palewick',
  'Brackenford', 'Mirefall', 'Stonebay', 'Larkspur', 'Greyharbour', 'Nimwood', 'Calderis',
  'Tarnholm', 'Whitecliff', 'Ravenmere', 'Sunderfell', 'Kestrel', 'Amber Ford', 'Redmarsh',
  'Elder Vale', 'Northwatch', 'Gullhaven', 'Saltspire', 'Windrow', 'Farhaven', 'Copperdeep',
  'Blackreed', 'Hollowmere', 'Stagfell', 'Bell Harbour', 'Umbercross', 'Fen Marrow',
];
