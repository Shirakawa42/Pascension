// Node smoke test for the designer's pure core:  node Tools/CardDesigner/smoke-test.mjs
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
globalThis.window = {}; // baseline.js writes here; app.js UI layer is skipped (no document)
require(join(here, 'baseline.js'));
const { Core, CARD_FIELDS } = require(join(here, 'app.js'));
const B = globalThis.window.SOI_BASELINE;

let failures = 0;
const test = (name, fn) => {
  try { fn(); console.log('  ok  ' + name); }
  catch (e) { failures++; console.error('FAIL  ' + name + ' — ' + e.message); }
};
const assert = (cond, msg) => { if (!cond) throw new Error(msg || 'assertion failed'); };
const eq = (a, b, msg) => assert(JSON.stringify(a) === JSON.stringify(b), `${msg || 'eq'}: ${JSON.stringify(a)} !== ${JSON.stringify(b)}`);

const baseMap = new Map(B.cards.map(c => [c.id, c]));
const baseKwMap = new Map(B.keywords.map(k => [k.id, k]));
const freshState = () => ({ cards: B.cards.map(Core.clone), keywords: B.keywords.map(Core.clone), sessionName: 't', notes: 'n' });

test('baseline shape', () => {
  assert(B.cards.length === 194, 'expected 194 cards (189 defs + 5 characters), got ' + B.cards.length);
  assert(B.keywords.length === 11, 'expected 11 keywords');
  assert(B.cards.every(c => c.id && c.name && c.set && c.faction && Array.isArray(c.types) && c.types.length), 'every card has identity fields incl. a types array');
});

test('untouched state produces an empty session', () => {
  const s = Core.buildSession(freshState(), B, baseMap, baseKwMap);
  eq(s.cards.added.length + s.cards.modified.length + s.cards.removed.length, 0, 'card changes');
  eq(s.keywords.added.length + s.keywords.modified.length + s.keywords.removed.length, 0, 'kw changes');
});

test('add + modify + remove round-trips through save/open', () => {
  const st = freshState();
  st.cards.push({ id: 'test_dragon', name: 'Test Dragon', set: 'new_dlc', faction: 'Aion', types: ['Champion'], cost: 5, qty: 2, defense: 6, shield: null, text: 'Exhaust: gain 3 power. M10: gain 5 instead.', art: '', artPrompt: 'a dragon', notes: 'hello' });
  const kiln = st.cards.find(c => c.id === 'kiln_drone');
  kiln.cost = 2; kiln.text = kiln.text + ' Warp 1.';
  st.cards.find(c => c.id === 'blaster').removed = true;
  st.keywords.push({ id: 'surge', name: 'Surge', faction: 'Aion', kind: 'text', pattern: '', flags: '', meaning: 'Does surge things.' });
  st.keywords.find(k => k.id === 'echo').meaning = 'Changed meaning.';
  st.keywords.find(k => k.id === 'inspire').removed = true;

  const saved = JSON.parse(JSON.stringify(Core.buildSession(st, B, baseMap, baseKwMap)));
  eq(saved.cards.added.length, 1, 'one added');
  eq(saved.cards.modified.length, 1, 'one modified');
  eq(saved.cards.removed.length, 1, 'one removed');
  eq(Object.keys(saved.cards.modified[0].fields).sort(), ['cost', 'text'], 'modified delta fields');

  const { state: re, warnings } = Core.applySession(B, saved);
  eq(warnings.length, 0, 'no warnings');
  const dragon = re.cards.find(c => c.id === 'test_dragon');
  assert(dragon && dragon.cost === 5 && dragon.artPrompt === 'a dragon', 'added card restored');
  const kiln2 = re.cards.find(c => c.id === 'kiln_drone');
  assert(kiln2.cost === 2 && kiln2.text.endsWith('Warp 1.'), 'modified card restored');
  assert(re.cards.find(c => c.id === 'blaster').removed === true, 'removed flag restored');
  assert(re.keywords.find(k => k.id === 'surge'), 'new keyword restored');
  eq(re.keywords.find(k => k.id === 'echo').meaning, 'Changed meaning.', 'kw modify restored');
  assert(re.keywords.find(k => k.id === 'inspire').removed === true, 'kw removed restored');
  // and the re-opened state serializes to the same deltas (idempotent round-trip)
  const saved2 = Core.buildSession(re, B, baseMap, baseKwMap);
  eq(saved2.cards, saved.cards, 'second save identical (cards)');
  eq(saved2.keywords, saved.keywords, 'second save identical (keywords)');
});

test('applySession flags unknown ids instead of crashing', () => {
  const sess = Core.buildSession(freshState(), B, baseMap, baseKwMap);
  sess.cards.removed.push({ id: 'ghost_card', name: 'Ghost' });
  sess.cards.modified.push({ id: 'ghost2', name: 'Ghost2', fields: { cost: { was: 1, now: 2 } } });
  const { warnings } = Core.applySession(B, sess);
  eq(warnings.length, 2, 'two warnings');
});

test('applySession rejects foreign files', () => {
  let threw = false;
  try { Core.applySession(B, { hello: 'world' }); } catch { threw = true; }
  assert(threw, 'should throw on missing _format');
});

test('status detection', () => {
  const st = freshState();
  eq(Core.cardStatus(st.cards[0], baseMap), '', 'untouched');
  st.cards[0].qty += 1;
  eq(Core.cardStatus(st.cards[0], baseMap), 'modified', 'qty change → modified');
  st.cards[0].qty -= 1;
  eq(Core.cardStatus(st.cards[0], baseMap), '', 'revert → untouched');
  st.cards[0].removed = true;
  eq(Core.cardStatus(st.cards[0], baseMap), 'removed', 'removed flag wins');
});

test('keyword detection matches the census', () => {
  const kws = Object.fromEntries(B.keywords.map(k => [k.id, k]));
  const cards = B.cards.filter(c => !c.types.includes('Character')); // census ran on the 189 defs (incl. Duel of Doom)
  const live = cards.filter(c => c.id !== 'cloud_oracles');  // errata-superseded
  eq(Core.keywordUsage(kws['unify'], live), 12, 'Unify count');
  eq(Core.keywordUsage(kws['warp'], live), 6, 'Warp count');
  eq(Core.keywordUsage(kws['dominion'], live), 7, 'Dominion count');
  eq(Core.keywordUsage(kws['mastery-threshold'], live), 50, 'threshold count');
  assert(Core.cardHasKeyword(cards.find(c => c.id === 'ingeminex_agony'), kws['ingeminex-attack']), 'ingeminex type keyword');
  assert(Core.cardHasKeyword(cards.find(c => c.id === 'nil_assassin'), kws['mercenary']), 'mercenary type keyword');
});

test('filters: faction + cost + keyword + query compose', () => {
  const f = { q: '', status: 'all', sets: new Set(), factions: new Set(['Aion']), types: new Set(), costMin: 3, costMax: 6, keyword: 'warp' };
  const out = Core.applyFilters(B.cards, f, B.keywords, baseMap);
  assert(out.length >= 2, 'found some'); // Brute(3), Lucky(4), J-Chord(3), Breaker(6)
  assert(out.every(c => c.faction === 'Aion' && (c.cost == null || (c.cost >= 3 && c.cost <= 6))), 'all match');
  const f2 = { ...f, q: 'shield' };
  const out2 = Core.applyFilters(B.cards, f2, B.keywords, baseMap);
  assert(out2.every(c => (c.name + c.id + c.text + c.notes).toLowerCase().includes('shield')), 'text query works');
});

test('grouping: keyword groups can repeat cards, cost groups sort numerically', () => {
  const groups = Core.groupCards(B.cards, 'cost', B.keywords, baseMap);
  const titles = groups.map(g => g.title);
  eq(titles[0], 'Cost 0', 'cost 0 first');
  assert(titles.indexOf('Cost 2') < titles.indexOf('Cost 10' in titles ? 'Cost 10' : 'Cost 7'), 'numeric order');
  const total = groups.reduce((s, g) => s + g.cards.length, 0);
  eq(total, B.cards.length, 'cost grouping partitions all cards');
});

test('brief contains every change and the repo checklist', () => {
  const st = freshState();
  st.cards.push({ id: 'brief_card', name: 'Brief Card', set: 'new_dlc', faction: 'None', types: ['Ally'], cost: 1, qty: 3, defense: null, shield: null, text: 'Gain 1 gem | test pipe.', art: '', artPrompt: '', notes: '' });
  const kiln = st.cards.find(c => c.id === 'kiln_drone');
  kiln.qty = 5; kiln.text = kiln.text + ' Also | a pipe.';
  st.cards.find(c => c.id === 'crystal').removed = true;
  const md = Core.buildBrief(st, B, baseMap, baseKwMap);
  for (const needle of ['Brief Card', '`brief_card`', 'New cards (1)', 'Modified cards (1)', 'kiln_drone', 'Removed cards (1)', '`crystal`', 'ShardsEffects.cs', 'SoiFrenchCards.cs', 'ExportShardsCardTable', 'Changelog.cs'])
    assert(md.includes(needle), 'brief mentions ' + needle);
  assert(md.includes('\\|'), 'pipes escaped in tables');
});

test('slugify + escaping', () => {
  eq(Core.slugify("Zara Ra, Soulflayer!"), 'zara_ra_soulflayer', 'slug');
  eq(Core.slugify('Éclair Noir'), 'eclair_noir', 'accents stripped');
  assert(!Core.esc('<img onerror=x>').includes('<'), 'esc strips angle brackets');
});

// ---- regression guards for the review fixes ----

test('#4 applySession coerces untrusted values and ignores unknown fields', () => {
  const sess = Core.buildSession(freshState(), B, baseMap, baseKwMap);
  sess.cards.added.push({ id: 'evil', name: '<img src=x onerror=alert(1)>', set: 's', faction: 'None', types: ['Ally'], cost: '<b>3</b>', qty: '2', defense: null, shield: null, text: 'x', art: '', artPrompt: '', notes: '' });
  sess.cards.modified.push({ id: 'kiln_drone', name: 'Kiln', fields: { qty: { now: '9' }, evilField: { now: 'ha' }, cost: { now: 'NaNish' } } });
  const { state: re } = Core.applySession(B, sess);
  const evil = re.cards.find(c => c.id === 'evil');
  eq(evil.cost, null, 'non-numeric html cost sanitized to null');
  eq(evil.qty, 2, 'qty coerced to number');
  assert(typeof evil.name === 'string', 'name kept as string (esc happens at render)');
  const kiln = re.cards.find(c => c.id === 'kiln_drone');
  eq(kiln.qty, 9, 'modified qty coerced');
  eq(kiln.cost, null, 'un-numeric cost -> null');
  assert(!('evilField' in kiln), 'unknown field name ignored');
});

test('#4b null name in added card does not crash, defaults to id', () => {
  const sess = Core.buildSession(freshState(), B, baseMap, baseKwMap);
  sess.cards.added.push({ id: 'nameless', set: 's', faction: 'None', types: ['Ally'], cost: 1, qty: 1 });
  const { state: re } = Core.applySession(B, sess);
  eq(re.cards.find(c => c.id === 'nameless').name, 'nameless', 'name defaults to id');
});

test('#6/#11 modified-then-removed card keeps its edits across round-trip', () => {
  const st = freshState();
  const kiln = st.cards.find(c => c.id === 'kiln_drone');
  kiln.cost = 2; kiln.removed = true;
  const sess = JSON.parse(JSON.stringify(Core.buildSession(st, B, baseMap, baseKwMap)));
  const rem = sess.cards.removed.find(r => r.id === 'kiln_drone');
  assert(rem.fields && rem.fields.cost, 'removed record carries the edit');
  const { state: re } = Core.applySession(B, sess);
  const k2 = re.cards.find(c => c.id === 'kiln_drone');
  assert(k2.removed && k2.cost === 2, 'restored card is removed AND still cost 2 (restore is lossless)');
});

test('#6b new-then-removed card keeps its full spec', () => {
  const st = freshState();
  st.cards.push({ id: 'tmp', name: 'Temp', set: 'new_dlc', faction: 'None', types: ['Ally'], cost: 3, qty: 1, defense: null, shield: null, text: 'x', art: '', artPrompt: '', notes: '', removed: true });
  const sess = JSON.parse(JSON.stringify(Core.buildSession(st, B, baseMap, baseKwMap)));
  assert(sess.cards.removed.find(r => r.id === 'tmp')?.card, 'removed new card serializes full spec');
  const { state: re } = Core.applySession(B, sess);
  const t = re.cards.find(c => c.id === 'tmp');
  assert(t && t.removed && t.cost === 3, 'new-then-removed card recreated with spec + removed flag');
});

test('#7 keyword rename-then-back is not permanently "modified"', () => {
  const st = freshState();
  const unify = st.keywords.find(k => k.id === 'unify');
  // rename away then back, mimicking the UI heuristic
  const rename = (k, newName) => {
    const base = baseKwMap.get(k.id);
    const autos = [Core.autoPattern(k.name)];
    if (base) autos.push(Core.autoPattern(base.name), base.pattern || Core.autoPattern(base.name));
    if (!k.pattern || autos.includes(k.pattern)) k.pattern = Core.autoPattern(newName) || '';
    k.name = newName;
  };
  rename(unify, 'Unite');
  eq(Core.kwStatus(unify, baseKwMap), 'modified', 'renamed away → modified');
  rename(unify, 'Unify');
  eq(Core.kwStatus(unify, baseKwMap), '', 'renamed back → clean (no phantom modified)');
});

test('#8 digit-matching keyword cannot corrupt Ingeminex markers (Core detection unaffected)', () => {
  // The rendering fix is UI-only, but confirm keywordRegex accepts a \\d+ pattern without throwing
  const kw = { id: 'nums', name: 'nums', kind: 'text', pattern: '\\d+', flags: 'g', meaning: 'x' };
  const re = Core.keywordRegex(kw);
  assert(re && re.test('gain 3 power'), 'digit pattern compiles and matches');
});

test('#13 cost filter excludes null-cost characters', () => {
  const f = { costMin: 5, costMax: 5 };
  const out = Core.applyFilters(B.cards, f, B.keywords, baseMap);
  assert(out.length > 0 && out.every(c => c.cost === 5), 'only cost-5 cards, no characters');
  assert(!out.some(c => c.types.includes('Character')), 'characters excluded when a cost bound is set');
  // with no bound, characters are visible
  const all = Core.applyFilters(B.cards, {}, B.keywords, baseMap);
  assert(all.some(c => c.types.includes('Character')), 'characters visible with no cost filter');
});

test('brief annotates Relic Champions and plain champions (derived IsChampion)', () => {
  const st = freshState();
  st.cards.push({ id: 'relic_champ', name: 'Test Bastion', set: 'new_dlc', faction: 'Homodeus', types: ['Relic'], cost: 0, qty: 1, defense: 8, shield: null, text: 'Guard.', art: '', artPrompt: '', notes: '' });
  st.cards.push({ id: 'relic_ally', name: 'Test Charm', set: 'new_dlc', faction: 'Order', types: ['Relic'], cost: 0, qty: 1, defense: null, shield: null, text: 'Gain 1 mastery.', art: '', artPrompt: '', notes: '' });
  const md = Core.buildBrief(st, B, baseMap, baseKwMap);
  assert(md.includes('Relic is a **Champion**'), 'relic champion annotated');
  assert(md.includes('(Relic Ally)'), 'relic ally annotated');
});

test('multi-type card: round-trips, canonical order, filters, grouping, brief', () => {
  const st = freshState();
  // check the boxes in a weird order — canonTypes should normalise to TYPE_ORDER
  st.cards.push({ id: 'hybrid', name: 'Hybrid Relic Champ', set: 'new_dlc', faction: 'Homodeus', types: ['Relic', 'Champion', 'Mercenary'], cost: 4, qty: 1, defense: 5, shield: null, text: 'x', art: '', artPrompt: '', notes: '' });
  const sess = JSON.parse(JSON.stringify(Core.buildSession(st, B, baseMap, baseKwMap)));
  eq(sess.cards.added.find(c => c.id === 'hybrid').types, ['Champion', 'Mercenary', 'Relic'], 'types normalised to canonical order');
  const { state: re } = Core.applySession(B, sess);
  eq(re.cards.find(c => c.id === 'hybrid').types, ['Champion', 'Mercenary', 'Relic'], 'round-trips');
  // filter by ANY of its types
  for (const t of ['Champion', 'Mercenary', 'Relic'])
    assert(Core.applyFilters(re.cards, { types: new Set([t]) }, B.keywords, baseMap).some(c => c.id === 'hybrid'), 'matches filter ' + t);
  // grouping fans it out under each type
  const groups = Core.groupCards(re.cards, 'type', B.keywords, baseMap);
  const inGroups = groups.filter(g => g.cards.some(c => c.id === 'hybrid')).map(g => g.title);
  eq(inGroups.sort(), ['Champion', 'Mercenary', 'Relic'], 'listed under each of its type groups');
  // brief flags the multi-type combination
  const md = Core.buildBrief(st, B, baseMap, baseKwMap);
  assert(md.includes('Multi-type card'), 'brief warns about the multi-type combination');
  assert(md.includes('Champion, Mercenary, Relic'), 'brief lists all types');
});

test('legacy single-`type` records migrate to types on load', () => {
  const sess = Core.buildSession(freshState(), B, baseMap, baseKwMap);
  sess.cards.added.push({ id: 'legacy', name: 'Legacy', set: 's', faction: 'None', type: 'Champion', cost: 2, qty: 1 });
  sess.cards.modified.push({ id: 'kiln_drone', name: 'Kiln', fields: { type: { now: 'Mercenary' } } });
  const { state: re } = Core.applySession(B, sess);
  eq(re.cards.find(c => c.id === 'legacy').types, ['Champion'], 'legacy added type -> types');
  eq(re.cards.find(c => c.id === 'kiln_drone').types, ['Mercenary'], 'legacy modified type -> types');
});

test('applySession rejects foreign & malformed shapes without throwing on load path', () => {
  // arrays/objects missing sub-keys should degrade to warnings, not exceptions
  const ok = Core.buildSession(freshState(), B, baseMap, baseKwMap);
  delete ok.cards; delete ok.keywords;
  const { warnings } = Core.applySession(B, ok);
  eq(warnings.length, 0, 'missing card/keyword sections tolerated');
});

console.log(failures ? `\n${failures} FAILURE(S)` : '\nall smoke tests passed');
process.exit(failures ? 1 : 0);
