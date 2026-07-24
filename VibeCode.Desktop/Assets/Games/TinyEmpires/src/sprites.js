// sprites.js — all the pixel art. Each sprite is an array of strings, one
// character per pixel. '1'/'2'/'3' are recoloured per empire at bake time;
// every other character is a fixed palette entry. Baked results are cached per
// (name, colour, scale) so drawing a unit is just a blit.
//
// Buildings are 16x16, cities 24x18, foot units 12x14. That resolution exists
// because the map zooms to 64 px per tile: at the old 9x9 a house was five
// visible pixels of roof and read as a bare triangle once magnified. Every
// sprite here is drawn with an outline, a mid tone and a highlight so it still
// has form when it is blown up.

import { clamp } from './core.js';

const PAL = {
  k: '#14161d',  // outline
  n: '#232733',  // soft shadow
  d: '#3b2a1c',  // dark wood / door
  w: '#6b4a2c',  // wood
  W: '#8f6438',  // wood light
  h: '#7a5334',  // horse hide
  H: '#a3714a',  // horse hide, lit
  j: '#b2854e',  // plank highlight
  s: '#6c737e',  // stone dark
  S: '#9aa2ad',  // stone
  u: '#c9d0d8',  // stone light
  r: '#7e3f31',  // roof dark
  R: '#a3543f',  // roof
  E: '#c87355',  // roof highlight
  t: '#8f6f3c',  // thatch dark
  T: '#c4a05e',  // thatch
  Y: '#e6c98d',  // thatch highlight
  g: '#2c5c33',  // foliage dark
  G: '#3f7c45',  // foliage
  L: '#5fa858',  // foliage light
  y: '#ffd166',  // gold
  q: '#ffe6a3',  // lit window
  f: '#e8c39a',  // skin
  F: '#c29268',  // skin shadow
  m: '#b9c4d1',  // metal
  M: '#eaf1f8',  // metal bright
  c: '#e6eaf1',  // cloth / plaster
  C: '#b9c0cc',  // cloth shade
  b: '#2f79ad',  // water
  B: '#57a3d4',  // water light
  o: '#ff9d5c',  // fire
  p: '#9d7cff',  // arcane
  e: '#78e0cf',  // teal accent
  x: 'rgba(0,0,0,.30)',   // ground shadow
};

/* ══════════════ buildings — 16 x 16 ══════════════ */
export const SPR_BLD = {
  house: [
    '................', '................', '.......tt.......', '......tTTt......',
    '.....tTTTTt.....', '....tTTYYTTt....', '...tTTYYYYTTt...', '..tTTTTTTTTTTt..',
    '.tttttttttttttt.', '..kwwwwwwwwwwk..', '..kwWqqwwqqWwk..', '..kwWqqwwqqWwk..',
    '..kwwwwwwwwwwk..', '..kwwwdddwwwwk..', '..kwwwdddwwwwk..', '..xxxxxxxxxxxx..'],
  cottage: [
    '................', '.....s..........', '.....s..tt......', '....ttttTTt.....',
    '...tTTTTTTTTt...', '..tTTYYYYYYTTt..', '.tTTTTTTTTTTTTt.', 'tttttttttttttttt',
    '.kSSSSSSSSSSSSk.', '.kSuqqSSSSqqu Sk', '.kSuqqSSSSqquSk.', '.kSSSSSSSSSSSSk.',
    '.kSSSwwdddwSSSk.', '.kSSSwwdddwSSSk.', '.kSSSwwdddwSSSk.', '.xxxxxxxxxxxxxx.'],
  manor: [
    '................', '......r.........', '.....rRr........', '...rrRRRrr......',
    '..rRRRRRRRRrr...', '.rRREEEEEERRRr..', 'rrrrrrrrrrrrrrr.', '.kjjjjjjjjjjjjk.',
    '.kjqqjjqqjjqqjk.', '.kjqqjjqqjjqqjk.', '.kjjjjjjjjjjjjk.', '.kWWWWWWWWWWWWk.',
    '.kWqqWWWWWWqqWk.', '.kWqqWWdddWqqWk.', '.kWWWWWdddWWWWk.', '.xxxxxxxxxxxxxx.'],
  apartment: [
    '................', '.kSSSSSSSSSSSSk.', '.kSuuuuuuuuuuSk.', '.kSqqSSqqSSqqSk.',
    '.kSqqSSqqSSqqSk.', '.kSSSSSSSSSSSSk.', '.kSqqSSqqSSqqSk.', '.kSqqSSqqSSqqSk.',
    '.kSSSSSSSSSSSSk.', '.kSqqSSqqSSqqSk.', '.kSqqSSqqSSqqSk.', '.kSSSSSSSSSSSSk.',
    '.kSuuSSuuSSuuSk.', '.kSSSSSdddSSSSk.', '.kSSSSSdddSSSSk.', '.xxxxxxxxxxxxxx.'],

  farm: [
    '................', '..y..y..y..y..y.', '.yYyyYyyYyyYyyY.', '.GGGGGGGGGGGGGG.',
    '.gggggggggggggg.', '.GGGGGGGGGGGGGG.', '.wwwwwwwwwwwwww.', '.GGGGGGGGGGGGGG.',
    '.gggggggggggggg.', '.GGGGGGGGGGGGGG.', '.wwwwwwwwwwwwww.', '.GGGGGGGGGGGGGG.',
    '.gggggggggggggg.', '.GGGGGGGGGGGGGG.', '.wwwwwwwwwwwwww.', '.xxxxxxxxxxxxxx.'],
  fishery: [
    '................', '.....tttt.......', '....tTTTTt......', '...tTTYYTTt.....',
    '..tttttttttt....', '..kwwwwwwwwk....', '..kwqqwwddwk....', '..kwwwwwddwk....',
    '.wwwwwwwwwwwww..', '.w..w..w..w..w..', 'bBbbBbbBbbBbbBbb', 'bbbBbbbbBbbbbBbb',
    'bbbbbbBbbbbbbbbb', 'bBbbbbbbbbBbbbbb', 'bbbbbbbbBbbbbbbb', 'bbbbbbbbbbbbbbbb'],
  lumber: [
    '................', '.......mm.......', '......mMMm......', '......mMMm......',
    '.......ww.......', '.......ww.......', '....g..ww..g....', '...gGg.ww.gGg...',
    '..gGLGg...gGLGg.', '...wWw.....wWw..', '.wwwwwwwwwwwwww.', '.wjwjwjwjwjwjww.',
    '.wwwwwwwwwwwwww.', '.wjwjwjwjwjwjww.', '.wwwwwwwwwwwwww.', '.xxxxxxxxxxxxxx.'],
  mine: [
    '................', '.....ssssss.....', '...sSSSSSSSSs...', '..sSSuuuuuuSSs..',
    '.sSSuuuuuuuuSSs.', '.sSuuwkkkkwuuSs.', '.sSuuwkkkkwuuSs.', '.sSuuwkkkkwuuSs.',
    '.sSSSSkkkkSSSSs.', '..sSSSkkkkSSSs..', '...ssskkkksss...', '......kkkk......',
    '...mm.kkkk.mm...', '..mMMm....mMMm..', '..mmmm....mmmm..', '.xxxxxxxxxxxxxx.'],
  quarry: [
    '................', '................', '.......uu.......', '......uSSu......',
    '.....uSSSSu.....', '....uSSuuSSu....', '...uSSuSSuSSu...', '..uSSSSSSSSSSu..',
    '.uSSuuSSuuSSuuS.', '.SSSSSSSSSSSSSS.', '.sSSsSSsSSsSSsS.', '.ssssssssssssss.',
    '.sSSsSSsSSsSSsS.', '.ssssssssssssss.', '.ssssssssssssss.', '.xxxxxxxxxxxxxx.'],
  pasture: [
    '................', '................', '......cccc......', '.....cCcccc.....',
    '....ccccccccc...', '...cckcccccck...', '....c......c....', '.....k....k.....',
    '.w...........w..', '.w...cccc....w..', '.wwwwCcccwwwwww.', '.w...c..c....w..',
    '.w....k.k....w..', '.wwwwwwwwwwwwww.', '.w...w...w...w..', '.xxxxxxxxxxxxxx.'],
  borderwall: [
    '................', '................', '................', '................',
    '.u.u.u.u.u.u.u.u', '.SuSuSuSuSuSuSuS', '.SSSSSSSSSSSSSSS', '.sSSsSSsSSsSSsSS',
    '.SSSSSSSSSSSSSSS', '.SSsSSsSSsSSsSSS', '.SSSSSSSSSSSSSSS', '.sSSsSSsSSsSSsSS',
    '.SSSSSSSSSSSSSSS', '.sssssssssssssss', '................', '.xxxxxxxxxxxxxx.'],

  granary: [
    '................', '......tttt......', '.....tTTTTt.....', '....tTTYYTTt....',
    '...tTTTTTTTTt...', '..tttttttttttt..', '...kWWWWWWWWk...', '...kWjjjjjjWk...',
    '...kWjqqqqjWk...', '...kWjqqqqjWk...', '...kWjjjjjjWk...', '...kWWWWWWWWk...',
    '...kWWdddWWWk...', '...kWWdddWWWk...', '...kWWWWWWWWk...', '...xxxxxxxxxx...'],
  barracks: [
    '.....1..........', '.....11.........', '.....111........', '.....1..........',
    '.....1..........', '..rrrrrrrrrrrr..', '.rRRRRRRRRRRRRr.', 'rrrrrrrrrrrrrrrr',
    '.kwwwwwwwwwwwwk.', '.kwqqwwmmwwqqwk.', '.kwqqwwmmwwqqwk.', '.kwwwwwmmwwwwwk.',
    '.kwwwwwdddwwwwk.', '.kwwwwwdddwwwwk.', '.kwwwwwdddwwwwk.', '.xxxxxxxxxxxxxx.'],
  walls: [
    '................', '................', '.u.u.u.u.u.u.u..', '.SuSuSuSuSuSuS..',
    '.SSSSSSSSSSSSS..', '.sSSsSSsSSsSSs..', '.SSSSSSSSSSSSS..', '.SSsSSsSSsSSsS..',
    '.SSSSSSSSSSSSS..', '.sSSsSSsSSsSSs..', '.SSSSSSSSSSSSS..', '.SSkkkkkkkkSSS..',
    '.SSkddddddkSSS..', '.SSkddddddkSSS..', '.ssssssssssss...', '.xxxxxxxxxxxx...'],
  market: [
    '................', '................', '..RcRcRcRcRcRc..', '.RcRcRcRcRcRcRc.',
    'cRcRcRcRcRcRcRcR', '..w..........w..', '..w..yyyyyy..w..', '..w.yyyyyyyy.w..',
    '..w..yyyyyy..w..', '..w.WWWWWWWW.w..', '..w.WjjjjjjW.w..', '..w.WWWWWWWW.w..',
    '..w..........w..', '..w..........w..', '..w..........w..', '.xxxxxxxxxxxxxx.'],
  library: [
    '................', '................', '.....pppppp.....', '....pcccccp.....',
    '....pcqqqcp.....', '.....pppppp.....', '..uuuuuuuuuuuu..', '.uSSSSSSSSSSSSu.',
    'uSSSSSSSSSSSSSSu', '.kSSSSSSSSSSSSk.', '.kSuSSuSSuSSuSk.', '.kSuSSuSSuSSuSk.',
    '.kSuSSuSSuSSuSk.', '.kSSSSSdddSSSSk.', '.kSSSSSdddSSSSk.', '.xxxxxxxxxxxxxx.'],
  harbor: [
    '.......1........', '.......11.......', '.....11111......', '....1111111.....',
    '...111111111....', '.......w........', '.......w........', '....WWWWWWW.....',
    '...wwwwwwwww....', '..wwwwwwwwwww...', '.w....w....w....', '.w....w....w....',
    'bBbbbbbbbbBbbbbb', 'bbbbBbbbbbbbbBbb', 'bbbbbbbbBbbbbbbb', 'bbbbbbbbbbbbbbbb'],
  shrine: [
    '................', '................', '..uuuuuuuuuuuu..', '..uSSSSSSSSSSu..',
    '..sSSSSSSSSSSs..', '..sS........Ss..', '..sS........Ss..', '..sS...pp...Ss..',
    '..sS..pppp..Ss..', '..sS...pp...Ss..', '..sS........Ss..', '..sS........Ss..',
    '..ssSSSSSSSSss..', '..ssssssssssss..', '................', '..xxxxxxxxxxxx..'],
  temple: [
    '................', '.......yy.......', '......uuuu......', '.....uucuuu.....',
    '....uuccccuu....', '...uucccccccu...', '..uuccccccccuu..', '.uuuuuuuuuuuuuu.',
    'uuuuuuuuuuuuuuuu', '.kcSScSScSScSck.', '.kcSScSScSScSck.', '.kcSScSScSScSck.',
    '.kcSScSScSScSck.', '.kccccccccccck..', '.uuuuuuuuuuuuuu.', '.xxxxxxxxxxxxxx.'],
  aqueduct: [
    '................', '.SSSSSSSSSSSSSS.', '.SuuuuuuuuuuuuS.', '.SbBbbBbbBbbBbS.',
    '.SbbbbbbbbbbbbS.', '.SSSSSSSSSSSSSS.', '.SsSSSSSSSSSSsS.', '.Ss..SS..SS..sS.',
    '.Ss..SS..SS..sS.', '.S...SS..SS...S.', '.S...SS..SS...S.', '.S...SS..SS...S.',
    '.S...SS..SS...S.', '.SSSSSSSSSSSSSS.', '.ssssssssssssss.', '.xxxxxxxxxxxxxx.'],
  smith: [
    '.....o..........', '....ooo.........', '.....o..........', '.....s..........',
    '.....s..........', '..ssssssssssss..', '.sSSSSSSSSSSSSs.', 'ssssssssssssssss',
    '.kwwwwwwwwwwwwk.', '.kwooooooooowwk.', '.kwoooooooooWwk.', '.kwwwwwwwwwwwwk.',
    '.kwwmmwwwwmmwwk.', '.kwwmmwwddmmwwk.', '.kwwwwwwddwwwwk.', '.xxxxxxxxxxxxxx.'],
  windmill: [
    'm.............m.', '.mm.........mm..', '..mm.......mm...', '...mm.....mm....',
    '....mm...mm.....', '.....mmmmm......', '......mmm.......', '.....WWWWW......',
    '....WWjjjWW.....', '....WWjjjWW.....', '...WWWjjjWWW....', '...WWqqqqqWW....',
    '...WWWWWWWWW....', '...WWWdddWWW....', '...WWWdddWWW....', '...xxxxxxxxx....'],
  castle: [
    '.1..............', '.1....u.u.u.....', '.1...uSuSuSu....', 'u.u..SSSSSSS.u.u',
    'SuS..SSSSSSS.SuS', 'SSS..SSSSSSS.SSS', 'SSS..SSSSSSS.SSS', 'SSSSSSSSSSSSSSSS',
    'SSSSSSSSSSSSSSSS', 'SsSSsSSsSSsSSsSS', 'SSSSSSSSSSSSSSSS', 'SSSSSkkkkkkSSSSS',
    'SSSSSkddddkSSSSS', 'SSSSSkddddkSSSSS', 'ssssssssssssssss', '.xxxxxxxxxxxxxx.'],
  shipyard: [
    '................', '....w.......w...', '...wwwwwwwwwww..', '..wwwwwwwwwwwww.',
    '.ww.ww.ww.ww.ww.', '.ww.ww.ww.ww.ww.', '.wwwwwwwwwwwwww.', '..WWWWWWWWWWWW..',
    '...wwwwwwwwww...', '.wwwwwwwwwwwwww.', '.w..w..w..w..w..', 'bBbbbbbbbbBbbbbb',
    'bbbbBbbbbbbbbBbb', 'bbbbbbbbBbbbbbbb', 'bbbbbbbbbbbbbbbb', 'bbbbbbbbbbbbbbbb'],
  univ: [
    '.......y........', '......ycy.......', '.....uccccu.....', '....uccccccu....',
    '...ucccccccccu..', '..uuuuuuuuuuuu..', '.uSSSSSSSSSSSSu.', 'uSSSSSSSSSSSSSSu',
    '.kSSSSSSSSSSSSk.', '.kuSSuSSuSSuSSk.', '.kuSSuSSuSSuSSk.', '.kuSSuSSuSSuSSk.',
    '.kuSSuSSuSSuSSk.', '.kSSSSSdddSSSSk.', '.kSSSSSdddSSSSk.', '.xxxxxxxxxxxxxx.'],
  cathedral: [
    '.......y........', '.......c........', '......ccc.......', '.....ccccc......',
    '....ccccccc.....', '...cccpppccc....', '..cccppppccccc..', '..cccpppcccccc..',
    '.cccccccccccccc.', 'cccccccccccccccc', '.kcCcCcCcCcCcck.', '.kcCcCcCcCcCcck.',
    '.kcCcCcCcCcCcck.', '.kccckddddkcccc.', '.kccckddddkcccc.', '.xxxxxxxxxxxxxx.'],
  bank: [
    '................', '................', '......yyyy......', '.....ycccy......',
    '....uuuuuuuu....', '...uuuuuuuuuu...', '..uuuuuuuuuuuu..', '.uSSSSSSSSSSSSu.',
    'uSSSSSSSSSSSSSSu', '.kSSSSSSSSSSSSk.', '.kuSSuSyySuSSuk.', '.kuSSuSyySuSSuk.',
    '.kuSSuSSSSuSSuk.', '.kSSSSSdddSSSSk.', '.kSSSSSdddSSSSk.', '.xxxxxxxxxxxxxx.'],
  armory: [
    '................', '....mm....mm....', '...mMMm..mMMm...', '....mm....mm....',
    '.....mm..mm.....', '......mmmm......', '..ssssssssssss..', '.sSSSSSSSSSSSSs.',
    'ssssssssssssssss', '.kwwwwwwwwwwwwk.', '.kwmmwwwwwwmmwk.', '.kwmmwwddwwmmwk.',
    '.kwwwwwddwwwwwk.', '.kwwwwwddwwwwwk.', '.kwwwwwwwwwwwwk.', '.xxxxxxxxxxxxxx.'],
  observ: [
    '..........mm....', '.........mm.....', '........mm......', '.....cccc.......',
    '....cccccc......', '...ccmmcccc.....', '..cccccccccc....', '..uuuuuuuuuu....',
    '.uSSSSSSSSSSu...', '.kSSSSSSSSSSk...', '.kSuSSuSSuSSk...', '.kSuSSuSSuSSk...',
    '.kSSSSSSSSSSk...', '.kSSSSdddSSSk...', '.kSSSSdddSSSk...', '.xxxxxxxxxxx....'],

  factory: [
    '..o.............', '..s....s........', '..s....s........', '..s....s........',
    '..ssssssssssss..', '.sSSSSSSSSSSSSs.', 'ssssssssssssssss', '.kwwwwwwwwwwwwk.',
    '.kwqqwwqqwwqqwk.', '.kwqqwwqqwwqqwk.', '.kwwwwwwwwwwwwk.', '.kwmmwwmmwwmmwk.',
    '.kwwwwwwwwwwwwk.', '.kwwwwwdddwwwwk.', '.kwwwwwdddwwwwk.', '.xxxxxxxxxxxxxx.'],
  hospital: [
    '................', '......cccc......', '.....cccccc.....', '....cccccccc....',
    '...cccrrrrccc...', '..ccccrrrrcccc..', '.cccccrrrrccccc.', 'cccccccccccccccc',
    '.kccccccccccck..', '.kcqqccqqccqqck.', '.kcqqccqqccqqck.', '.kccccccccccck..',
    '.kcqqccqqccqqck.', '.kcccccdddccccc.', '.kcccccdddccccc.', '.xxxxxxxxxxxxxx.'],
  power: [
    '..o.........o...', '..s.........s...', '..s.........s...', '..sss.....sss...',
    '..ssssssssssss..', '.sSSSSSSSSSSSSs.', 'ssssssssssssssss', '.kSSSSSSSSSSSSk.',
    '.kSyyyyyyyyyySk.', '.kSySSSSSSSSySk.', '.kSySSyySSSSySk.', '.kSySSyySSSSySk.',
    '.kSyyyyyyyyyySk.', '.kSSSSSdddSSSSk.', '.kSSSSSdddSSSSk.', '.xxxxxxxxxxxxxx.'],
  railway: [
    '................', '................', '................', '................',
    '.m............m.', '.mmmmmmmmmmmmmm.', '.mwmmwmmwmmwmmw.', '.mmmmmmmmmmmmmm.',
    '.m............m.', '.mmmmmmmmmmmmmm.', '.mwmmwmmwmmwmmw.', '.mmmmmmmmmmmmmm.',
    '.m............m.', '................', '................', '.xxxxxxxxxxxxxx.'],
  oilwell: [
    '.......mm.......', '......mMMm......', '.....mm..mm.....', '....mm....mm....',
    '...mm..mm..mm...', '...m..mMMm..m...', '..mm..mMMm..mm..', '..m...mMMm...m..',
    '.mm...mMMm...mm.', '.m....mMMm....m.', 'mmmmmmmMMmmmmmmm', '.kkkkkkkkkkkkkk.',
    '.kdddddddddddkk.', '.kdnnnnnnnnndkk.', '.kdddddddddddkk.', '.xxxxxxxxxxxxxx.'],
  refinery: [
    '..o.......o.....', '..s.......s.....', '..s.......s.....', '..sssssssss.....',
    '..mmmmmmmmmmm...', '.mMMMMMMMMMMMm..', '.mmmmmmmmmmmmm..', '.mkmmkmmkmmkmm..',
    '.mmmmmmmmmmmmm..', '.mMMMMMMMMMMMm..', '.mmmmmmmmmmmmm..', '.kSSSSSSSSSSSk..',
    '.kSSSSSSSSSSSk..', '.kSSSSdddSSSSk..', '.kSSSSdddSSSSk..', '.xxxxxxxxxxxxx..'],
  airfield: [
    '................', '................', '.......1........', '......111.......',
    '.....11111......', '..111111111111..', '.....11111......', '......111.......',
    '.......1........', '................', 'kkkkkkkkkkkkkkkk', 'kMkkMkkMkkMkkMkk',
    'kkkkkkkkkkkkkkkk', 'kMkkMkkMkkMkkMkk', 'kkkkkkkkkkkkkkkk', '.xxxxxxxxxxxxxx.'],
  lab: [
    '.......e........', '......eee.......', '.....eeeee......', '......ccc.......',
    '.....ccccc......', '....ccpppcc.....', '...cccccccccc...', '..uuuuuuuuuuuu..',
    '.uSSSSSSSSSSSSu.', '.kSSSSSSSSSSSSk.', '.kSqqSSqqSSqqSk.', '.kSqqSSqqSSqqSk.',
    '.kSSSSSSSSSSSSk.', '.kSSSSSdddSSSSk.', '.kSSSSSdddSSSSk.', '.xxxxxxxxxxxxxx.'],
};

/* ══════════════ cities — 24 x 18, four growth stages ══════════════
   These are settlements, not monuments: the sprite is a little cluster of
   roofs so a city reads as a place people live rather than one big object. */
export const SPR_CITY = [
  // hamlet — a few huts round a fire
  ['........................', '........................', '........................',
   '.......tt......tt.......', '......tTTt....tTTt......', '.....tTTTTt..tTTTTt.....',
   '....tttttttttttttttt....', '....kwwwwk....kwwwwk....', '....kwqqwk....kwqqwk....',
   '....kwddwk....kwddwk....', '.........o..............', '........ooo....tt.......',
   '.......w.o.w..tTTTTt....', '..............tttttttt..', '..............kwwwwwwk..',
   '..............kwddwwwk..', '........................', '..xxxxxxxxxxxxxxxxxxx...'],
  // town — a hall, a palisade and smoke
  ['........................', '...........1............', '...........1............',
   '.......rrrrrrrrrr.......', '......rRRRRRRRRRRr......', '.....rrrrrrrrrrrrrr.....',
   '.....kwwwwwwwwwwwwk.....', '.....kwqqwwwwwwqqwk.....', '.....kwwwwwddwwwwwk.....',
   '.w...kwwwwwddwwwwwk...w.', '.w..tt...........tt...w.', '.w.tTTt.........tTTt..w.',
   '.w.wwww.........wwww..w.', '.wwwwwwwwwwwwwwwwwwwwww.', '.w.w.w.w.w.w.w.w.w.w.ww.',
   '........................', '........................', '..xxxxxxxxxxxxxxxxxxxx..'],
  // city — stone curtain wall, towers, gatehouse
  ['...........1............', '...........1............', '..u.u...........u.u.....',
   '..SuS..rrrrrrr..SuS.....', '..SSS.rRRRRRRRr.SSS.....', '..SSS.rrrrrrrrr.SSS.....',
   '..SSS.kwwwwwwwk.SSS.....', '..SSS.kwqqwwqwk.SSS.....', '..SSSSkwwwwwwwkSSSS.....',
   'u.u.SSSSSSSSSSSSSSS.u.u.', 'SuS.SSSSSSSSSSSSSSS.SuS.', 'SSSSSSSSSSSSSSSSSSSSSSSS',
   'SSsSSsSSsSSsSSsSSsSSsSSS', 'SSSSSSSSSSSSSSSSSSSSSSSS', 'SSSSSSSSSkkkkSSSSSSSSSSS',
   'SSSSSSSSSkddkSSSSSSSSSSS', 'ssssssssskddksssssssssss', '.xxxxxxxxxxxxxxxxxxxxxx.'],
  // metropolis — cathedral spire over tiled roofs behind a great wall
  ['...1.......y.......1....', '...1.......c.......1....', '..u.u.....ccc.....u.u...',
   '..SuS....ccccc....SuS...', '..SSS...cccpccc...SSS...', '..SSS..ccccccccc..SSS...',
   '..SSS.rrrrrrrrrrr.SSS...', '..SSSrRRRRRRRRRRRrSSS...', '..SSSrrrrrrrrrrrrrSSS...',
   'u.u.SkwqqwwwwwwqqwkS.u.u', 'SuS.SkwwwwwwwwwwwwkS.SuS', 'SSSSSSSSSSSSSSSSSSSSSSSS',
   'SSsSSsSSsSSsSSsSSsSSsSSS', 'SSSSSSSSSSSSSSSSSSSSSSSS', 'SSSSSSSSSkkkkSSSSSSSSSSS',
   'SSSSSSSSSkddkSSSSSSSSSSS', 'ssssssssskddksssssssssss', '.xxxxxxxxxxxxxxxxxxxxxx.'],
];

/* ══════════════ units ══════════════
   Foot soldiers share a 12x14 body so they read as one army, and differ by the
   shape their weapon cuts out of the silhouette — the only thing that survives
   when they are two tiles tall on screen. */

const FOOT = {
  head: ['....kkkk....', '...kmmmmk...', '...kfFffk...', '....kffk....'],
};

export const SPR_UNIT = {
  settler: [
    '....kkkk....', '...kffffk...', '...kfFffk...', '....kffk....',
    '..k11111k...', '.kW11111Wk..', '.kW11111Wk..', '.kW11111Wk..',
    '..k11111k...', '...k111k....', '...k1.1k....', '...k1.1k....',
    '...kk.kk....', '....x..x....'],
  warrior: [
    '....kkkk....', '...kmmmmk...', '...kfFffk...', '....kffk....',
    '..c1111k.m..', '.ccc1111km..', '.ccc1111km..', '.ccc1111km..',
    '..c1111k.m..', '...k111k....', '...k1.1k....', '...k1.1k....',
    '...kk.kk....', '....x..x....'],
  spearman: [
    '.........M..', '....kkkkkm..', '...kmmmmkm..', '...kfFffkm..',
    '....kffk.m..', '..c1111k.m..', '.ccc1111km..', '.ccc1111km..',
    '..c1111k.m..', '...k111k....', '...k1.1k....', '...k1.1k....',
    '...kk.kk....', '....x..x....'],
  archer: [
    '....kkkk..w.', '...kmmmmk.ww', '...kfFffk..w', '....kffk...w',
    '...k1111k..w', '..k111111k.w', '..k111111kw.', '..k111111w..',
    '...k1111k...', '...k111k....', '...k1.1k....', '...k1.1k....',
    '...kk.kk....', '....x..x....'],
  sword: [
    '....kkkk.M..', '...kmmmmkM..', '...kfFffkM..', '....kffk.M..',
    '..cm111k.M..', '.cccm111kM..', '.cccm111kM..', '.cccm111k...',
    '..cm111k....', '...k111k....', '...k1.1k....', '...k1.1k....',
    '...kk.kk....', '....x..x....'],
  crossbow: [
    '....kkkk....', '...kmmmmk...', '...kfFffk...', '....kffk....',
    '..mmmmmmmm..', '..mk1111km..', '...k1111k...', '..k111111k..',
    '...k1111k...', '...k111k....', '...k1.1k....', '...k1.1k....',
    '...kk.kk....', '....x..x....'],
  musket: [
    '..........c.', '....kkkk.m..', '...kccmck.m.', '...kfFffkm..',
    '....kffkm...', '..k1111km...', '.kc111111k..', '.kc111111k..',
    '..k11111k...', '...k111k....', '...k1.1k....', '...k1.1k....',
    '...kk.kk....', '....x..x....'],
  rifleman: [
    '.........m..', '....kkkk.m..', '...kmmmmkm..', '...kfFffkm..',
    '....kffk.m..', '..k11111km..', '.kc111111k..', '.kc111111k..',
    '..k11111k...', '...k111k....', '...k1.1k....', '...k1.1k....',
    '...kk.kk....', '....x..x....'],
  infantry: [
    '....kkkk....', '...kGGGGk...', '...kfFffk...', '....kffk....',
    '..m11111m...', '.km1111 1mk.', '.km111111mk.', '.km111111mk.',
    '..m11111m...', '...k111k....', '...k1.1k....', '...k1.1k....',
    '...kk.kk....', '....x..x....'],

  // ── mounted: horse in profile with a rider, 16x14
  // Horse in profile facing left, rider seated on top: a flat brown slab reads
  // as nothing at all, so the hide is shaded and the legs are separated.
  horseman: [
    '................', '.........kkk....', '........kmmmk...', '........kfFfk...',
    '.........kfk....', '.......k11111k..', '......k1111111k.', 'kkk...k11111k...',
    'kHHk..kkkkkkk...', 'kHhkhhhhhhhhk...', '.khhhhhhhhhhhk..', '.khhhhhhhhhhhk..',
    '..kh.kh..kh.hk..', '..xx.xx..xx.xx..'],
  knight: [
    '................', '.........1k.....', '........kmMmk...', '........kmmmk...',
    '.........kmk....', 'mmmmmmmk11111k..', '......k1111111k.', 'kkk...k11111k...',
    'kHHk..kkkkkkk...', 'kHhkhhhhhhhhk...', '.khhhhhhhhhhhk..', '.kmhhhhhhhhhmk..',
    '..kh.kh..kh.hk..', '..xx.xx..xx.xx..'],

  // ── siege and armour, 16x14
  catapult: [
    '................', '.............ww.', '............ww..', '...........ww...',
    '..........ww....', '.........ww.....', '..wwwwwwww......', '..w......w......',
    '..wwwwwwww......', '..w.wwww.w......', '..k......k......', '.kkk....kkk.....',
    '.kkk....kkk.....', '.xxx....xxx.....'],
  artillery: [
    '................', '................', '......mmmmmmmmmm', '.....mMMMMMMMMMm',
    '.....mmmmmmmmmmm', '....mmmm........', '...ww..ww.......', '..wwwwwwww......',
    '..w......w......', '..k......k......', '.kkk....kkk.....', '.kkk....kkk.....',
    '..k......k......', '.xxx....xxx.....'],
  cannon: [
    '................', '................', '................', '....mmmmmmmmmmm.',
    '...mMMMMMMMMMMm.', '...mmmmmmmmmmmm.', '..wwwwww........', '.wwwwwwww.......',
    '.w......w.......', '.k......k.......', 'kkk....kkk......', 'kkk....kkk......',
    '.k......k.......', 'xxx....xxx......'],
  tank: [
    '................', '................', '.........mmmmmmm', '........mMMMMMMM',
    '....11111mmmmmmm', '...1111111......', '..111111111.....', '..111111111.....',
    'kkkkkkkkkkkk....', 'kMkkMkkMkkMk....', 'kkkkkkkkkkkk....', 'kMkkMkkMkkMk....',
    'kkkkkkkkkkkk....', '.xxxxxxxxxx.....'],

  // ── ships, 20x12: real hulls with masts
  trireme: [
    '.........1..........', '.........11.........', '.......1111.........', '......111111........',
    '.....11111111.......', '.........w..........', '.........w..........', '..wwwwwwwwwwwwww....',
    '.wWWWWWWWWWWWWWWw...', '..wwwwwwwwwwwwwww...', '...bbbbbbbbbbbbb....', '....xxxxxxxxxxx.....'],
  frigate: [
    '....1....1....1.....', '...11...11...11.....', '..111..111..111.....', '..111..111..111.....',
    '....w....w....w.....', '....w....w....w.....', '.wwwwwwwwwwwwwwww...', 'wWWWWWWWWWWWWWWWWw..',
    'wWkWkWkWkWkWkWkWWw..', '.wwwwwwwwwwwwwwww...', '..bbbbbbbbbbbbbb....', '...xxxxxxxxxxxx.....'],
  ironclad: [
    '....................', '.........s..........', '.........s..........', '......mmmmmmm.......',
    '.....mMMMMMMMm......', '....mmmmmmmmmmm.....', '...mmmmmmmmmmmmm....', '..mmmmmmmmmmmmmmm...',
    '.mkmmkmmkmmkmmkmmk..', '..mmmmmmmmmmmmmmm...', '...bbbbbbbbbbbbb....', '....xxxxxxxxxxx.....'],
  destroyer: [
    '....................', '........1...........', '........1...........', '......mmmmm.........',
    '.....mMMMMMm........', '...mmmmmmmmmmmm.....', '..mmmmmmmmmmmmmmm...', '.mmmmmmmmmmmmmmmmm..',
    'mkmmkmmkmmkmmkmmkmm.', '.mmmmmmmmmmmmmmmmm..', '..bbbbbbbbbbbbbbb...', '...xxxxxxxxxxxxx....'],
  battleship: [
    '.......m....m.......', '.......m....m.......', '....mmmmmmmmmmm.....', '...mMMMMMMMMMMMm....',
    '..mmmmmmmmmmmmmmm...', '.mmmmmmmmmmmmmmmmm..', 'mmmmmmmmmmmmmmmmmmm.', 'mMmmMmmMmmMmmMmmMm..',
    'mkmmkmmkmmkmmkmmkmm.', 'mmmmmmmmmmmmmmmmmmm.', '.bbbbbbbbbbbbbbbbb..', '..xxxxxxxxxxxxxxx...'],

  // ── aircraft, 20x10 — swept wings, nothing else on the map looks like this
  fighter: [
    '.........11.........', '.........11.........', '........1111........', 'mmmmmmmm1111mmmmmmmm',
    'mMMMMMMM1111MMMMMMMm', '........1111........', '.......111111.......', '......mm1111mm......',
    '........mmmm........', '.........mm.........'],
  bomber: [
    '.........11.........', '........1111........', '........1111........', 'mmmmmmmm1111mmmmmmmm',
    'mMMMMMMM1111MMMMMMMm', 'mmmmmmmm1111mmmmmmmm', '.......1111111......', '.....mmm1111mmm.....',
    '....mm..mmmm..mm....', '.........mm.........'],

  // a land unit crossing water rides a raft
  boat: [
    '....................', '.........1..........', '.........1..........', '.........1..........',
    '........111.........', '..wwwwwwwwwwwww.....', '.wWWWWWWWWWWWWWw....', '..wwwwwwwwwwwww.....',
    '...bbbbbbbbbbb......', '....xxxxxxxxx.......'],

  // ambient townsfolk — small, but an actual person
  villager: [
    '.kk.', 'kffk', '.11.', '111.', '.11.', 'k..k'],
};

/* ══════════════ baking ══════════════ */

const cache = new Map();

/** Lighten/darken a #rrggbb by a flat amount per channel. */
export function shade(hex, amt) {
  const n = parseInt(hex.slice(1), 16);
  const r = clamp(((n >> 16) & 255) + amt, 0, 255) | 0;
  const g = clamp(((n >> 8) & 255) + amt, 0, 255) | 0;
  const b = clamp((n & 255) + amt, 0, 255) | 0;
  return `rgb(${r},${g},${b})`;
}

/** Rasterise a sprite at `scale` device pixels per art pixel, tinted for one empire. */
export function bake(name, rows, color, scale) {
  const key = `${name}|${color}|${scale}`;
  const hit = cache.get(key);
  if (hit) return hit;

  const w = Math.max(...rows.map(r => r.length)), h = rows.length;
  const cv = document.createElement('canvas');
  cv.width = w * scale; cv.height = h * scale;
  const g = cv.getContext('2d');
  const c1 = color, c2 = shade(color, -62), c3 = shade(color, 56);

  for (let y = 0; y < h; y++) {
    const row = rows[y];
    for (let x = 0; x < row.length; x++) {
      const ch = row[x];
      if (ch === '.' || ch === ' ') continue;
      g.fillStyle = ch === '1' ? c1 : ch === '2' ? c2 : ch === '3' ? c3 : (PAL[ch] || '#ff00ff');
      g.fillRect(x * scale, y * scale, scale, scale);
    }
  }
  cache.set(key, cv);
  return cv;
}

export const bakeBld = (id, color, scale) => bake('b_' + id, SPR_BLD[id] || SPR_BLD.house, color, scale);
export const bakeUnit = (id, color, scale) => bake('u_' + id, SPR_UNIT[id] || SPR_UNIT.warrior, color, scale);
export const bakeCity = (stage, color, scale) => bake('c_' + stage, SPR_CITY[stage], color, scale);
export const clearSpriteCache = () => cache.clear();
