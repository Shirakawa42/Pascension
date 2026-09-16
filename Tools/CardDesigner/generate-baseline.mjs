#!/usr/bin/env node
// Regenerates baseline.js from Tools/ShardsData/cards-table.md (the registry export).
// Run after any real card change:  node Tools/CardDesigner/generate-baseline.mjs
// (regenerate cards-table.md first: cd Tools/EngineVerify && dotnet test --filter ExportShardsCardTable)
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const tablePath = join(here, '..', 'ShardsData', 'cards-table.md');
const outPath = join(here, 'baseline.js');

const metadata = JSON.parse(readFileSync(join(here, 'registry-metadata.json'), 'utf8'));
const lines = readFileSync(tablePath, 'utf8').split(/\r?\n/);
const cards = [];
for (let i = 2; i < lines.length; i++) {
  let l = lines[i];
  if (!l.startsWith('| ')) continue;
  // Ingeminex rules texts span two physical lines — merge until the row closes.
  // Bounded so a truncated / mid-row-EOF table errors loudly instead of looping forever.
  while (!l.trimEnd().endsWith('|')) {
    if (++i >= lines.length) throw new Error('cards-table.md ends mid-row (unterminated table row): ' + l.slice(0, 70));
    l += '\n' + lines[i];
  }
  const c = l.split('|').map(s => s.trim());
  const num = v => (v === '–' || v === '' ? null : Number(v));
  cards.push({
    id: c[1], name: c[2], set: c[3], faction: c[4], types: [c[5]], // types is an array — cards may carry several
    cost: Number(c[6]), qty: Number(c[7]), defense: num(c[8]), shield: num(c[9]),
    text: l.slice(l.indexOf('|', 1)).split('|').slice(9, -1).join('|').trim() // keep embedded newlines
      .split('\n').map(s => s.trim()).join('\n'),
    art: c[1] + '.png', artPrompt: '', notes: ''
  });
}
// Tripwire against a silently-shifted table. If the real pool count legitimately changed,
// bump this number AND the '198 cards' + keyword-census expectations in smoke-test.mjs.
// 188 = all registered defs after Whisper Extractor was removed.
const EXPECTED_DEFS = 188;
if (cards.length !== EXPECTED_DEFS)
  throw new Error(`expected ${EXPECTED_DEFS} card defs, parsed ${cards.length}. If the pool genuinely changed, ` +
    `update EXPECTED_DEFS here and the '198 cards' + keyword-census asserts in Tools/CardDesigner/smoke-test.mjs.`);
// Header sanity: guard against a reordered/renamed column silently corrupting positional parsing.
const header = lines[0].split('|').map(s => s.trim());
const EXPECTED_COLS = ['', 'Id', 'Name', 'Set', 'Faction', 'Type', 'Cost', 'Qty', 'Def', 'Shield', 'Rules (functional paraphrase)', ''];
if (header.join('|') !== EXPECTED_COLS.join('|'))
  throw new Error('cards-table.md column layout changed — update the parser in generate-baseline.mjs. Got header: ' + lines[0]);

// Keep old printings available for non-Duel balance work, clearly mark their
// replacements, and use the inherited art exactly as SoiCardFaces does.
for (const card of cards) {
  card.art = metadata.artIds[card.id] + '.png';
  const replacements = Object.entries(metadata.replacements).filter(([, id]) => id === card.id).map(([id]) => id);
  if (replacements.length) card.notes = 'Superseded when the corresponding DLC is enabled: ' + replacements.join(', ') + '.';
}

// The five playable characters (not in the table; portraits ship as soichar_*.png).
const FOCUS = 'Focus — Exhaust: pay 1 gem, gain 1 mastery. Once per turn.';
const characters = [
  ['soichar_decima', 'Decima', 'base', 'Homodeus'],
  ['soichar_tetra', 'Tetra', 'base', 'Order'],
  ['soichar_volos', 'Volos', 'base', 'Undergrowth'],
  ['soichar_kosynwu', 'Ko Syn Wu', 'base', 'Wraethe'],
  ['soichar_rez', 'Rez', 'shadow_of_salvation', 'Aion'],
].map(([id, name, set, faction]) => ({
  id, name, set, faction, types: ['Character'], cost: null, qty: 1, defense: null, shield: null,
  text: FOCUS, art: id + '.png', artPrompt: '', notes: ''
}));
cards.push(...characters);
for (const ability of metadata.abilities) {
  const hero = characters.find(c => c.id === ability.id.replace('soiability_', 'soichar_'));
  cards.push({ ...ability, set: 'duel', faction: hero.faction, types: ['Hero Ability'],
    cost: null, qty: 1, defense: null, shield: null, art: ability.id + '.png', artPrompt: '', notes: '' });
}

// Keyword glossary — meanings mirror SoiKeywordGlossary.cs (the in-game tooltips).
// kind: 'text' keywords are detected by regex over rules text; 'type'/'stat' by card shape.
const keywords = [
  { id: 'exhaust', name: 'Exhaust', faction: '', kind: 'text', pattern: '\\bexhaust', flags: 'i',
    meaning: 'Tap this ready card to use its ability. It readies at your end phase.' },
  { id: 'unify', name: 'Unify', faction: 'Undergrowth', kind: 'text', pattern: '\\bUnify\\b', flags: '',
    meaning: "Active if you played another card of this faction this turn; otherwise automatically reveal the first matching card in your hand." },
  { id: 'dominion', name: 'Dominion', faction: 'Order', kind: 'text', pattern: '\\bDominion\\b', flags: '',
    meaning: 'Base game: another Homodeus, Undergrowth and Wraethe card played or revealed. Duel: at least 3 other cards of 3 different factions played or revealed.' },
  { id: 'allegiance', name: 'Allegiance', faction: '', kind: 'text', pattern: '\\bAllegiance\\b', flags: '',
    meaning: 'Active if you own at least the shown number of cards of that faction in your deck, hand, discard and in play.' },
  { id: 'inspire', name: 'Inspire', faction: 'Homodeus', kind: 'text', pattern: '\\bInspire\\b', flags: '',
    meaning: 'Active while you control a champion.' },
  { id: 'echo', name: 'Echo', faction: 'Wraethe', kind: 'text', pattern: '\\bEcho\\b', flags: '',
    meaning: "Active once if at least one card of this faction is in your discard pile. It only scales per card when explicitly stated." },
  { id: 'warp', name: 'Warp', faction: 'Aion', kind: 'text', pattern: '\\bWarp\\b', flags: '',
    meaning: 'Fast-play a row ally costing up to the shown number for free (no number: any ally). It goes under the center deck at end of turn.' },
  { id: 'mastery-threshold', name: 'Mastery threshold', faction: '', kind: 'text', pattern: '\\bM\\d+\\b', flags: '',
    meaning: 'Needs that much Mastery when you play or exhaust this card.' },
  { id: 'shield', name: 'Shield', faction: '', kind: 'stat', pattern: '', flags: '',
    meaning: 'Reveal it from your hand when attacked to prevent that much damage. It stays in your hand. (Champion printed shields are inert in play.)' },
  { id: 'mercenary', name: 'Mercenary', faction: '', kind: 'type', pattern: '', flags: '',
    meaning: 'Recruit it, or fast-play it for its cost: effect now, then under the center deck.' },
  { id: 'ingeminex-attack', name: 'Attack (Ingeminex)', faction: 'Monster', kind: 'type', pattern: '', flags: '',
    meaning: "Strikes every player at the end of the turn it appeared, unless it is defeated first. Not damage — shields don't prevent it." },
  { id: 'ingeminex-reward', name: 'Reward (Ingeminex)', faction: 'Monster', kind: 'type', pattern: '', flags: '',
    meaning: 'Deal it 10 total power to defeat it; only the player who defeats it claims the reward.' },
];

const baseline = {
  generatedAt: new Date().toISOString().slice(0, 10),
  source: 'Tools/ShardsData/cards-table.md',
  artDir: '../../Assets/Art/Shards/Cards/',
  sets: [
    { id: 'base', name: 'Base game' },
    { id: 'relics_of_the_future', name: 'Relics of the Future' },
    { id: 'shadow_of_salvation', name: 'Shadow of Salvation' },
    { id: 'into_the_horizon', name: 'Into the Horizon' },
    { id: 'duel', name: 'Duel of Doom' },
  ],
  factions: [
    { id: 'Homodeus', color: '#857542' },
    { id: 'Order', color: '#3D6194' },
    { id: 'Undergrowth', color: '#3D7A47' },
    { id: 'Wraethe', color: '#6B4785' },
    { id: 'Aion', color: '#9E5238' },
    { id: 'Monster', color: '#8F2E2E' },
    { id: 'None', color: '#52525C' },
  ],
  types: ['Ally', 'Champion', 'Mercenary', 'Relic', 'Destiny', 'Starter', 'Monster', 'Character', 'Hero Ability'],
  keywords, cards,
};

writeFileSync(outPath,
  '// GENERATED by generate-baseline.mjs — do not hand-edit. Regenerate after real card changes.\n' +
  'window.SOI_BASELINE = ' + JSON.stringify(baseline, null, 1) + ';\n');
console.log(`baseline.js written: ${cards.length} cards (${characters.length} characters + ${metadata.abilities.length} hero abilities), ${keywords.length} keywords`);
