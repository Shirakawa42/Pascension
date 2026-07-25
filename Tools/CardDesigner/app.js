/* SoI Card Designer — pure design tool, touches nothing in the game.
 * Core = pure logic (node-testable: `node smoke-test.mjs`); UI below only runs in a browser.
 * Data flow: baseline.js (generated from the registry export) -> working state -> session
 * JSON (reopenable + AI-readable) -> optional Markdown implementation brief.
 */
'use strict';

const CARD_FIELDS = ['name', 'set', 'faction', 'types', 'cost', 'qty', 'defense', 'shield', 'text', 'art', 'artPrompt', 'notes'];
const KW_FIELDS = ['name', 'faction', 'kind', 'pattern', 'flags', 'meaning'];
// Canonical type order — cards keep their `types` sorted by this so saved deltas are stable
// regardless of the order the designer checked the boxes. Unknown/custom types append after.
const TYPE_ORDER = ['Ally', 'Champion', 'Mercenary', 'Relic', 'Destiny', 'Starter', 'Monster', 'Character'];

const Core = {
  esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, c =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  },

  slugify(name) {
    return String(name || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '')
      .replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '') || 'card';
  },

  norm(v) { return v === undefined || v === null || v === '' ? null : v; },

  clone(o) { return JSON.parse(JSON.stringify(o)); },

  /** A card's types as a clean array (tolerates legacy single-`type`, empty, or garbage). */
  typesOf(c) {
    if (Array.isArray(c.types)) return c.types;
    if (c.type) return [c.type]; // legacy single-type record
    return [];
  },
  hasType(c, t) { return Core.typesOf(c).includes(t); },
  primaryType(c) { return Core.typesOf(c)[0] || 'Ally'; },
  canonTypes(arr) {
    const src = (Array.isArray(arr) ? arr : arr == null ? [] : [arr]).map(x => String(x)).filter(Boolean);
    const seen = new Set(), out = [];
    for (const t of TYPE_ORDER) if (src.includes(t) && !seen.has(t)) { seen.add(t); out.push(t); }
    for (const t of src) if (!seen.has(t)) { seen.add(t); out.push(t); } // keep custom types
    return out.length ? out : ['Ally'];
  },

  /** Stable field order so saved files diff cleanly and read top-down. */
  canonCard(c) {
    const out = { id: c.id };
    for (const f of CARD_FIELDS) out[f] = f === 'types' ? Core.canonTypes(Core.typesOf(c)) : (c[f] ?? (f === 'qty' ? 0 : null));
    return out;
  },

  canonKw(k) {
    const out = { id: k.id };
    for (const f of KW_FIELDS) out[f] = k[f] ?? '';
    return out;
  },

  /** Changed fields between a baseline record and a working record (array-aware for `types`). */
  diffFields(base, cur, fields) {
    const out = {};
    for (const f of fields) {
      const a = Core.norm(base[f]), b = Core.norm(cur[f]);
      const same = (Array.isArray(a) || Array.isArray(b)) ? JSON.stringify(a) === JSON.stringify(b) : a === b;
      if (!same) out[f] = { was: base[f] ?? null, now: cur[f] ?? null };
    }
    return out;
  },

  cardStatus(card, baseMap) {
    if (card.removed) return 'removed';
    const base = baseMap.get(card.id);
    if (!base) return 'new';
    return Object.keys(Core.diffFields(base, card, CARD_FIELDS)).length ? 'modified' : '';
  },

  kwStatus(kw, baseMap) {
    if (kw.removed) return 'removed';
    const base = baseMap.get(kw.id);
    if (!base) return 'new';
    return Object.keys(Core.diffFields(base, kw, KW_FIELDS)).length ? 'modified' : '';
  },

  /** The literal whole-word pattern a text keyword falls back to when no custom pattern is set. */
  autoPattern(name) {
    return '\\b' + String(name || '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\b';
  },

  keywordRegex(kw) {
    if (kw.kind !== 'text') return null;
    try {
      const src = kw.pattern && kw.pattern.trim() ? kw.pattern : Core.autoPattern(kw.name);
      return new RegExp(src, (kw.flags || '').replace(/[^gimsuy]/g, ''));
    } catch { return null; }
  },

  cardHasKeyword(card, kw) {
    if (kw.kind === 'type') {
      if (kw.id === 'mercenary') return Core.hasType(card, 'Mercenary');
      if (kw.id.startsWith('ingeminex')) return Core.hasType(card, 'Monster');
      return Core.hasType(card, kw.name);
    }
    if (kw.kind === 'stat') return (card.shield || 0) > 0 || /\bShield\b/.test(card.text || '');
    const re = Core.keywordRegex(kw);
    return re ? re.test(card.text || '') : false;
  },

  keywordUsage(kw, cards) {
    return cards.filter(c => !c.removed && Core.cardHasKeyword(c, kw)).length;
  },

  keywordsOn(card, keywords) {
    return keywords.filter(k => !k.removed && Core.cardHasKeyword(card, k));
  },

  applyFilters(cards, f, keywords, baseMap) {
    const q = (f.q || '').trim().toLowerCase();
    const kw = f.keyword ? keywords.find(k => k.id === f.keyword) : null;
    return cards.filter(c => {
      const status = Core.cardStatus(c, baseMap) || 'untouched';
      if (f.status && f.status !== 'all' && status !== f.status) return false;
      if (f.sets && f.sets.size && !f.sets.has(c.set)) return false;
      if (f.factions && f.factions.size && !f.factions.has(c.faction)) return false;
      if (f.types && f.types.size && !Core.typesOf(c).some(t => f.types.has(t))) return false;
      if (f.costMin != null || f.costMax != null) {
        // a cost filter is an explicit ask for buyable cards — the 5 cost-less characters drop out
        if (c.cost == null) return false;
        if (f.costMin != null && c.cost < f.costMin) return false;
        if (f.costMax != null && c.cost > f.costMax) return false;
      }
      if (kw && !Core.cardHasKeyword(c, kw)) return false;
      if (q && !(`${c.name} ${c.id} ${c.text} ${c.notes}`.toLowerCase().includes(q))) return false;
      return true;
    });
  },

  sortCards(cards, sortBy) {
    const by = {
      name: (a, b) => a.name.localeCompare(b.name),
      cost: (a, b) => (a.cost ?? 99) - (b.cost ?? 99) || a.name.localeCompare(b.name),
      qty: (a, b) => b.qty - a.qty || a.name.localeCompare(b.name),
      faction: (a, b) => a.faction.localeCompare(b.faction) || (a.cost ?? 99) - (b.cost ?? 99),
      type: (a, b) => Core.primaryType(a).localeCompare(Core.primaryType(b)) || (a.cost ?? 99) - (b.cost ?? 99),
    }[sortBy] || ((a, b) => a.name.localeCompare(b.name));
    return [...cards].sort(by);
  },

  groupCards(cards, groupBy, keywords, baseMap) {
    if (groupBy === 'none') return [{ title: 'All cards', cards }];
    if (groupBy === 'keyword') {
      const groups = keywords.filter(k => !k.removed)
        .map(k => ({ title: k.name, cards: cards.filter(c => Core.cardHasKeyword(c, k)) }))
        .filter(g => g.cards.length);
      const none = cards.filter(c => !keywords.some(k => !k.removed && Core.cardHasKeyword(c, k)));
      if (none.length) groups.push({ title: 'No keyword', cards: none });
      return groups;
    }
    if (groupBy === 'type') {
      // a multi-type card lists under EACH of its types (like keyword grouping)
      const groups = TYPE_ORDER
        .map(t => ({ title: t, cards: cards.filter(c => Core.hasType(c, t)) }))
        .filter(g => g.cards.length);
      const known = new Set(TYPE_ORDER);
      const customTypes = [...new Set(cards.flatMap(c => Core.typesOf(c)).filter(t => !known.has(t)))].sort();
      for (const t of customTypes) groups.push({ title: t, cards: cards.filter(c => Core.hasType(c, t)) });
      return groups;
    }
    const keyOf = {
      faction: c => c.faction, set: c => c.set,
      cost: c => c.cost == null ? 'No cost' : 'Cost ' + c.cost,
      status: c => Core.cardStatus(c, baseMap) || 'untouched',
    }[groupBy] || (c => c.faction);
    const m = new Map();
    for (const c of cards) {
      const k = keyOf(c);
      if (!m.has(k)) m.set(k, []);
      m.get(k).push(c);
    }
    let entries = [...m.entries()];
    if (groupBy === 'cost') entries.sort((a, b) => (a[0] === 'No cost') - (b[0] === 'No cost') || a[0].localeCompare(b[0], undefined, { numeric: true }));
    else if (groupBy === 'status') {
      const order = ['new', 'modified', 'removed', 'untouched'];
      entries.sort((a, b) => order.indexOf(a[0]) - order.indexOf(b[0]));
    } else entries.sort((a, b) => String(a[0]).localeCompare(String(b[0])));
    return entries.map(([title, cs]) => ({ title, cards: cs }));
  },

  /* ---------- session file (save / open) ---------- */

  buildSession(state, baseline, baseMap, baseKwMap) {
    const added = [], modified = [], removed = [];
    for (const c of state.cards) {
      const st = Core.cardStatus(c, baseMap);
      if (st === 'new') added.push(Core.canonCard(c));
      else if (st === 'modified') modified.push({ id: c.id, name: c.name, fields: Core.diffFields(baseMap.get(c.id), c, CARD_FIELDS) });
      else if (st === 'removed') {
        // keep any edits made before removal so Restore is lossless across save/reopen
        const rec = { id: c.id, name: c.name };
        if (baseMap.has(c.id)) {
          const fields = Core.diffFields(baseMap.get(c.id), c, CARD_FIELDS);
          if (Object.keys(fields).length) rec.fields = fields;
        } else {
          rec.card = Core.canonCard(c); // a new card the user then removed — keep the full spec
        }
        removed.push(rec);
      }
    }
    const kwAdded = [], kwModified = [], kwRemoved = [];
    for (const k of state.keywords) {
      const st = Core.kwStatus(k, baseKwMap);
      if (st === 'new') kwAdded.push(Core.canonKw(k));
      else if (st === 'modified') kwModified.push({ id: k.id, name: k.name, fields: Core.diffFields(baseKwMap.get(k.id), k, KW_FIELDS) });
      else if (st === 'removed') kwRemoved.push({ id: k.id, name: k.name });
    }
    return {
      _format: 'soi-card-designer/1',
      _readme: [
        'Design session for the Shards of Infinity fan port — game content is NOT touched by this file.',
        'Reopen it with Tools/CardDesigner/index.html (Open…) to keep designing.',
        'cards.added = complete new card specs. cards.modified = per-field was -> now deltas vs the live pool.',
        'cards.removed = cards this design cuts. keywords.* = same idea for mechanic keywords.',
        'qty = copies in the center deck (starters: copies per player). cost null = the card is not bought.',
        'To implement: follow the checklist in the exported Markdown brief (Export AI brief button).',
      ],
      session: { name: state.sessionName || '', savedAt: new Date().toISOString(), notes: state.notes || '' },
      baselineStamp: { generatedAt: baseline.generatedAt, cardCount: baseline.cards.length },
      cards: { added, modified, removed },
      keywords: { added: kwAdded, modified: kwModified, removed: kwRemoved },
    };
  },

  /** Coerce an untrusted field value from a (possibly AI-authored) session file to its
   *  expected shape, so foreign JSON can never inject HTML or wrong types into the tool. */
  coerceCardField(f, v) {
    if (f === 'types') return Core.canonTypes(v);
    if (['cost', 'defense', 'shield'].includes(f)) { // null is meaningful (no cost / not a champion / no shield)
      if (v === null || v === undefined || v === '') return null;
      const n = Number(v); return Number.isFinite(n) ? Math.max(0, Math.trunc(n)) : null;
    }
    if (f === 'qty') { const n = Number(v); return Number.isFinite(n) ? Math.max(0, Math.trunc(n)) : 0; }
    return v == null ? '' : String(v);
  },
  sanitizeCard(raw, fallbackId) {
    const c = { id: String(raw.id || fallbackId || 'card') };
    // accept a legacy single-`type` record by folding it into `types`
    const src = raw.types != null ? raw.types : (raw.type != null ? [raw.type] : undefined);
    for (const f of CARD_FIELDS) c[f] = Core.coerceCardField(f, f === 'types' ? src : raw[f]);
    if (!c.name) c.name = c.id;
    return c;
  },

  /** Rebuild working state = baseline + session deltas. Returns {state, warnings}. */
  applySession(baseline, sess) {
    const warnings = [];
    if (!sess || sess._format !== 'soi-card-designer/1')
      throw new Error('Not a SoI card-designer session file (missing _format).');
    if (sess.baselineStamp && sess.baselineStamp.cardCount !== baseline.cards.length)
      warnings.push(`Session was saved against a ${sess.baselineStamp.cardCount}-card pool; the live pool now has ${baseline.cards.length}. Review MODIFIED cards.`);
    const cards = baseline.cards.map(Core.clone);
    const byId = new Map(cards.map(c => [c.id, c]));
    const applyFields = (target, fields, allowed, coerce) => {
      fields = fields || {};
      // migrate a legacy single-`type` delta into the `types` array shape
      if (fields.type && !fields.types) fields = { ...fields, types: { now: [fields.type.now] } };
      for (const [f, d] of Object.entries(fields)) {
        if (!allowed.includes(f) || !d || typeof d !== 'object') continue; // ignore unknown/garbage field names
        target[f] = coerce ? coerce(f, d.now) : d.now;
      }
    };
    for (const rec of sess.cards?.removed || []) {
      let c = byId.get(rec.id);
      if (!c && rec.card) { // a new-then-removed card: recreate it, then mark removed
        c = Core.sanitizeCard(rec.card, rec.id); cards.push(c); byId.set(c.id, c);
      }
      if (c) { applyFields(c, rec.fields, CARD_FIELDS, Core.coerceCardField); c.removed = true; }
      else warnings.push(`Removed card '${rec.id}' no longer exists in the pool.`);
    }
    for (const rec of sess.cards?.modified || []) {
      const c = byId.get(rec.id);
      if (!c) { warnings.push(`Modified card '${rec.id}' no longer exists in the pool — change dropped.`); continue; }
      applyFields(c, rec.fields, CARD_FIELDS, Core.coerceCardField);
    }
    for (const rec of sess.cards?.added || []) {
      if (byId.has(rec.id)) { warnings.push(`Added card '${rec.id}' collides with an existing id — session version kept.`); cards.splice(cards.indexOf(byId.get(rec.id)), 1); }
      const c = Core.sanitizeCard(rec, rec.id);
      cards.push(c); byId.set(c.id, c);
    }
    const keywords = baseline.keywords.map(Core.clone);
    const kwById = new Map(keywords.map(k => [k.id, k]));
    for (const rec of sess.keywords?.removed || []) {
      const k = kwById.get(rec.id);
      if (k) k.removed = true; else warnings.push(`Removed keyword '${rec.id}' not found.`);
    }
    for (const rec of sess.keywords?.modified || []) {
      const k = kwById.get(rec.id);
      if (!k) { warnings.push(`Modified keyword '${rec.id}' not found — change dropped.`); continue; }
      applyFields(k, rec.fields, KW_FIELDS, (f, v) => v == null ? '' : String(v));
    }
    for (const rec of sess.keywords?.added || []) {
      if (kwById.has(rec.id)) { warnings.push(`Added keyword '${rec.id}' collides — session version kept.`); keywords.splice(keywords.indexOf(kwById.get(rec.id)), 1); }
      const k = Core.canonKw({ kind: 'text', ...Core.clone(rec) });
      k.id = String(k.id || 'keyword');
      if (!['text', 'type', 'stat'].includes(k.kind)) k.kind = 'text';
      keywords.push(k); kwById.set(k.id, k);
    }
    return {
      state: { cards, keywords, sessionName: String(sess.session?.name || ''), notes: String(sess.session?.notes || '') },
      warnings,
    };
  },

  /* ---------- AI implementation brief ---------- */

  fmtVal(v) {
    if (v === null || v === undefined || v === '') return '—';
    if (Array.isArray(v)) return v.length ? v.join(', ') : '—';
    return String(v).replace(/\n/g, ' / ').replace(/\|/g, '\\|');
  },

  buildBrief(state, baseline, baseMap, baseKwMap) {
    const s = Core.buildSession(state, baseline, baseMap, baseKwMap);
    const L = [];
    const date = s.session.savedAt.slice(0, 10);
    L.push(`# Shards of Infinity — card design brief${s.session.name ? ` — ${s.session.name}` : ''}`);
    L.push('');
    L.push(`Exported ${date} from the SoI Card Designer (Tools/CardDesigner). Baseline pool: ${s.baselineStamp.cardCount} cards (${baseline.generatedAt}).`);
    L.push('This brief is a complete, self-contained spec of every design change. Nothing in the game has been modified yet.');
    if (s.session.notes) { L.push(''); L.push('## Designer notes'); L.push(''); L.push(s.session.notes); }

    const specBlock = c => {
      const types = Core.typesOf(c);
      // champion-ness is derived exactly as ShardsCardDef.IsChampion does it
      const champ = types.includes('Champion') || types.includes('Monster') || (types.includes('Relic') && (c.defense || 0) > 0);
      const typeNote = types.includes('Relic')
        ? ((c.defense || 0) > 0 ? ' (Relic is a **Champion** — Type includes Relic + Defense>0, deploys as a champion)' : ' (Relic Ally)')
        : (champ ? ' (deploys as a champion)' : '');
      const rows = [`- **Set**: \`${c.set}\` · **Faction**: ${c.faction} · **Type(s)**: ${types.join(', ')}${typeNote}`];
      if (types.length > 1) rows.push(`- ⚠ **Multi-type card** — carries ${types.length} types at once (${types.join(' + ')}); this is a designer-invented combination, confirm the engine supports it.`);
      const stats = [];
      if (c.cost != null) stats.push(`cost ${c.cost}`);
      stats.push(`qty ${c.qty}`);
      if (c.defense != null) stats.push(`defense ${c.defense}`);
      if (c.shield != null) stats.push(`shield ${c.shield}`);
      rows.push(`- **Stats**: ${stats.join(' · ')}`);
      rows.push(`- **Rules text**: ${c.text ? c.text.replace(/\n/g, '  \n  ') : '(none)'}`);
      if (c.artPrompt) rows.push(`- **Art prompt**: ${c.artPrompt}`);
      if (c.notes) rows.push(`- **Design notes**: ${c.notes}`);
      return rows.join('\n');
    };

    if (s.cards.added.length) {
      L.push(''); L.push(`## New cards (${s.cards.added.length})`);
      const bySet = new Map();
      for (const c of s.cards.added) { if (!bySet.has(c.set)) bySet.set(c.set, []); bySet.get(c.set).push(c); }
      for (const [set, cs] of bySet) {
        L.push(''); L.push(`### Set \`${set}\` (${cs.length} card${cs.length > 1 ? 's' : ''})`);
        for (const c of cs) { L.push(''); L.push(`#### ${c.name} (\`${c.id}\`)`); L.push(''); L.push(specBlock(c)); }
      }
    }
    if (s.cards.modified.length) {
      L.push(''); L.push(`## Modified cards (${s.cards.modified.length})`);
      for (const m of s.cards.modified) {
        L.push(''); L.push(`### ${m.name} (\`${m.id}\`)`); L.push('');
        L.push('| field | was | now |'); L.push('|---|---|---|');
        for (const [f, d] of Object.entries(m.fields))
          L.push(`| ${f} | ${Core.fmtVal(d.was)} | ${Core.fmtVal(d.now)} |`);
      }
    }
    if (s.cards.removed.length) {
      L.push(''); L.push(`## Removed cards (${s.cards.removed.length})`); L.push('');
      for (const r of s.cards.removed) L.push(`- ${r.name} (\`${r.id}\`)${r.fields ? ' — (had unsaved edits; ignore, this card is cut)' : ''}`);
    }
    const kwAny = s.keywords.added.length + s.keywords.modified.length + s.keywords.removed.length;
    if (kwAny) {
      L.push(''); L.push('## Keyword changes');
      for (const k of s.keywords.added) {
        L.push(''); L.push(`### NEW keyword: ${k.name}${k.faction ? ` (${k.faction})` : ''}`); L.push('');
        L.push(`- **Meaning**: ${k.meaning}`);
        L.push(`- **Detection**: ${k.kind === 'text' ? `regex \`${k.pattern || '\\b' + k.name + '\\b'}\` over rules text` : k.kind}`);
      }
      for (const m of s.keywords.modified) {
        L.push(''); L.push(`### Modified keyword: ${m.name} (\`${m.id}\`)`); L.push('');
        L.push('| field | was | now |'); L.push('|---|---|---|');
        for (const [f, d] of Object.entries(m.fields)) L.push(`| ${f} | ${Core.fmtVal(d.was)} | ${Core.fmtVal(d.now)} |`);
      }
      if (s.keywords.removed.length) {
        L.push(''); L.push('### Removed keywords'); L.push('');
        for (const r of s.keywords.removed) L.push(`- ${r.name} (\`${r.id}\`)`);
      }
    }
    if (!s.cards.added.length && !s.cards.modified.length && !s.cards.removed.length && !kwAny) {
      L.push(''); L.push('_No changes vs the live pool yet._');
    }
    L.push('');
    L.push('## How to implement (repo map for the implementing AI)');
    L.push('');
    L.push('1. Read the `shards-cards` and `shards-engine` skills first; both registries MUST be updated in the same change.');
    L.push('2. Card definitions: `Assets/Scripts/Shards/Content/` — one builder per set (`ShardsBaseSet.cs`, `ShardsRelicsSet.cs`, `ShardsShadowSet.cs`, `ShardsHorizonSet.cs`). A new DLC set gets its own builder + registration in `ShardsContentRegistry`, plus a DLC enable flag like the existing sets.');
    L.push('3. Compose effects from the vocabulary in `Assets/Scripts/Shards/Engine/ShardsEffects.cs` (`Gain`, `E.Seq`, `E.At` additive tiers, `BestByMastery`, `Unify`, `Dominion`, `PerCount`, `WarpUpTo`, …). Prefer a new generic effect class or a `ShardsCardDef` hook over `Custom`.');
    L.push('4. Quantities or set membership changed → update `Counts_MatchPublishedComponentLists` in `ShardsContentTests.cs`; add ruling tests in `ShardsRulingsTests.cs` for tricky interactions.');
    L.push('5. New keywords → detection + tooltip in `Assets/Scripts/Game/Soi/SoiKeywordGlossary.cs` (+ French entries in LocFrench).');
    L.push('6. EVERY new or renamed card needs a French entry in `Assets/Scripts/Game/Soi/SoiFrenchCards.cs` (official IELLO terminology).');
    L.push('7. Regenerate the table + this tool\'s baseline: `cd Tools/EngineVerify && dotnet test --filter ExportShardsCardTable`, then `node Tools/CardDesigner/generate-baseline.mjs`. Card-count changed → bump the expected count in `generate-baseline.mjs` (the `!== 125` assert) and in `smoke-test.mjs` (the `130 cards` assert + keyword-census expectations).');
    L.push('8. Art: generate via the art-pipeline skill (Anima/ComfyUI); per-card art prompts are included above where written.');
    L.push('9. Add a dated entry to the SoI list in `Assets/Scripts/Game/UI/Changelog.cs` (EN + FR).');
    L.push('10. Verify: `cd Tools/EngineVerify && dotnet test` — keep it green.');
    L.push('');
    return L.join('\n');
  },
};

if (typeof module !== 'undefined' && module.exports) module.exports = { Core, CARD_FIELDS, KW_FIELDS };

/* ======================= UI layer (browser only) ======================= */
if (typeof document !== 'undefined' && document.getElementById('topbar')) (() => {

  const B = window.SOI_BASELINE;
  const baseMap = new Map(B.cards.map(c => [c.id, Object.freeze(Core.clone(c))]));
  const baseKwMap = new Map(B.keywords.map(k => [k.id, Object.freeze(Core.clone(k))]));
  const LS_KEY = 'soi-card-designer-autosave';

  let state = { cards: B.cards.map(Core.clone), keywords: B.keywords.map(Core.clone), sessionName: '', notes: '' };
  let selectedId = null;
  let dirty = false;

  const $ = id => document.getElementById(id);
  const el = (tag, cls, html) => { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; };

  const filters = { q: '', status: 'all', sets: new Set(), factions: new Set(), types: new Set(), costMin: null, costMax: null, keyword: '' };

  /* ---------- lookups ---------- */
  const factionColor = f => (B.factions.find(x => x.id === f) || {}).color || customFactionColor(f);
  const CUSTOM_COLORS = ['#7a5f9e', '#3f7f8f', '#8f6b3f', '#5f8f4f', '#8f3f6b', '#4f5f8f'];
  function customFactionColor(f) {
    let h = 0; for (const ch of String(f)) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
    return CUSTOM_COLORS[h % CUSTOM_COLORS.length];
  }
  const allSets = () => [...new Set([...B.sets.map(s => s.id), ...state.cards.map(c => c.set)])];
  const allFactions = () => [...new Set([...B.factions.map(f => f.id), ...state.cards.map(c => c.faction)])];
  const allTypes = () => [...new Set([...B.types, ...state.cards.flatMap(c => Core.typesOf(c))])];
  const setName = id => (B.sets.find(s => s.id === id) || {}).name || id;

  /* ---------- rules-text rendering (token-substitution so regexes never chew emitted HTML) ---------- */
  function renderText(text, card) {
    if (!text) return '<span style="opacity:.4">(no rules text)</span>';
    const tokens = [];
    const put = html => String.fromCodePoint(0xE000 + (tokens.push(html) - 1));
    let t = String(text).replace(/[\uE000-\uF8FF]/g, ""); // drop any stray PUA from the source
    // Ingeminex line labels
    t = t.replace(/(^|\n)(Attack):\s*/g, (_, p) => p + put('<span class="tk-atk" title="Strikes every player at end of the reveal turn unless defeated">⚔ </span>'));
    t = t.replace(/(^|\n)(Reward):\s*/g, (_, p) => p + put('<span class="tk-rew" title="Only the defeater claims this">✦ </span>'));
    // keyword highlights (working set, incl. new/modified keywords)
    for (const kw of state.keywords) {
      if (kw.removed || kw.kind !== 'text') continue;
      const re = Core.keywordRegex(kw);
      if (!re) continue;
      const g = new RegExp(re.source, re.flags.includes('g') ? re.flags : re.flags + 'g');
      t = t.replace(g, m => put(`<span class="kw" title="${Core.esc(kw.meaning)}">${Core.esc(m)}</span>`));
    }
    // mastery pills that survived (keyword pass usually owns M\d+)
    t = t.replace(/\bM(\d+)\b/g, (_, n) => put(`<span class="mpill">M${n}</span>`));
    // resource tinting
    t = t.replace(/\b(power|puissance)\b/gi, m => put(`<span class="tk-power">${Core.esc(m)}</span>`));
    t = t.replace(/\b(gems?|cristal|cristaux)\b/gi, m => put(`<span class="tk-gem">${Core.esc(m)}</span>`));
    t = t.replace(/\b(health|santé)\b/gi, m => put(`<span class="tk-health">${Core.esc(m)}</span>`));
    t = t.replace(/\b(mastery|maîtrise)\b/gi, m => put(`<span class="tk-mastery">${Core.esc(m)}</span>`));
    t = Core.esc(t);
    t = t.replace(/[\uE000-\uF8FF]/g, m => tokens[m.codePointAt(0) - 0xE000]);
    return t;
  }

  // Mirrors ShardsCardDef.IsChampion (ShardsTypes.cs): champion-ness is DERIVED, not stored —
  // a Champion, an Ingeminex, or a Relic with defense > 0 (Praetorian-02) deploys as a champion.
  const isChampionCard = c => Core.hasType(c, 'Champion') || Core.hasType(c, 'Monster') || (Core.hasType(c, 'Relic') && (c.defense || 0) > 0);
  const cardTakesDefense = c => Core.hasType(c, 'Champion') || Core.hasType(c, 'Monster') || Core.hasType(c, 'Relic');

  function typeWord(t, c) {
    switch (t) {
      case 'Monster': return 'Ingeminex';
      case 'Starter': return 'Item — Ally';
      case 'Mercenary': return 'Mercenary Ally';
      case 'Relic': return 'Relic ' + ((c.defense || 0) > 0 ? 'Champion' : 'Ally');
      default: return t; // Ally / Champion / Destiny / Character / custom
    }
  }

  function typeLine(c) {
    const types = Core.typesOf(c);
    const faction = c.faction === 'None' || c.faction === 'Monster' ? '' : c.faction + ' ';
    if (types.length <= 1) return (faction + typeWord(types[0] || 'Ally', c)).trim();
    return (faction + types.map(t => typeWord(t, c)).join(' · ')).trim(); // multi-type: join the pieces
  }

  /* ---------- card element ---------- */
  function cardEl(c) {
    const status = Core.cardStatus(c, baseMap);
    const root = el('div', 'card' + (c.removed ? ' removed' : '') + (c.id === selectedId ? ' selected' : ''));
    root.style.setProperty('--fc', factionColor(c.faction));
    root.dataset.id = c.id;
    const showCost = ['Ally', 'Mercenary', 'Champion'].some(t => Core.hasType(c, t)) && c.cost != null;
    const art = c.art
      ? `<img loading="lazy" alt="" src="${Core.esc(B.artDir + c.art)}" onerror="this.remove()"><div class="noart" style="z-index:-1">no art</div>`
      : `<div class="noart">new card — no art</div>`;
    root.innerHTML =
      `${status ? `<span class="status ${status}">${status}</span>` : ''}` +
      `<span class="qty" title="copies">×${Core.esc(c.qty)}</span>` +
      `<div class="inner">` +
      `<div class="head">${showCost ? `<span class="cost">${Core.esc(c.cost)}</span>` : ''}<span class="nm">${Core.esc(c.name)}</span></div>` +
      `<div class="artbox">${art}</div>` +
      `<div class="typeline">${Core.hasType(c, 'Mercenary') ? '<span class="merc">▲</span>' : ''}${Core.esc(typeLine(c))}</div>` +
      `<div class="text">${renderText(c.text, c)}</div>` +
      `<div class="foot">` +
      `${c.shield != null && c.shield > 0 ? `<span class="badge shield" title="Shield">🛡 ${Core.esc(c.shield)}</span>` : ''}` +
      `${c.defense != null && c.defense > 0 ? `<span class="badge def" title="Defense">${Core.esc(c.defense)}</span>` : ''}` +
      `</div></div>`;
    root.addEventListener('click', () => { selectedId = c.id; openEditor(); renderGrid(); });
    return root;
  }

  /* ---------- grid ---------- */
  function visibleCards() {
    const f = { ...filters };
    return Core.sortCards(Core.applyFilters(state.cards, f, state.keywords, baseMap), $('gSort').value);
  }

  function renderGrid() {
    const groupsEl = $('groups');
    groupsEl.textContent = '';
    const vis = visibleCards();
    const groups = Core.groupCards(vis, $('gGroup').value, state.keywords, baseMap);
    for (const g of groups) {
      const copies = g.cards.reduce((s, c) => s + (c.removed ? 0 : c.qty), 0);
      const head = el('div', 'groupHead');
      head.append(el('h2', '', Core.esc(String(g.title === 'base' || B.sets.some(s => s.id === g.title) ? setName(g.title) : g.title))));
      head.append(el('span', 'gcount', `${g.cards.length} cards · ${copies} copies`));
      groupsEl.append(head);
      const grid = el('div', 'grid');
      for (const c of g.cards) grid.append(cardEl(c));
      groupsEl.append(grid);
    }
    $('empty').style.display = vis.length ? 'none' : 'block';
    renderStats(vis);
  }

  function renderStats(vis) {
    const copies = a => a.reduce((s, c) => s + (c.removed ? 0 : c.qty), 0);
    let nNew = 0, nMod = 0, nRem = 0;
    for (const c of state.cards) {
      const st = Core.cardStatus(c, baseMap);
      if (st === 'new') nNew++; else if (st === 'modified') nMod++; else if (st === 'removed') nRem++;
    }
    const kwChanged = state.keywords.filter(k => Core.kwStatus(k, baseKwMap)).length;
    $('stats').innerHTML =
      `showing <b>${vis.length}</b> / ${state.cards.filter(c => !c.removed).length} cards · <b>${copies(vis)}</b> copies<br>` +
      `<span class="sN">+${nNew} new</span> · <span class="sM">~${nMod} modified</span> · <span class="sR">−${nRem} removed</span><br>` +
      `keywords changed: <b>${kwChanged}</b>`;
  }

  /* ---------- filters UI ---------- */
  function chipRow(container, values, sel, labelOf) {
    container.textContent = '';
    for (const v of values) {
      const c = el('span', 'chip' + (sel.has(v) ? ' on' : ''), Core.esc(labelOf ? labelOf(v) : v));
      c.addEventListener('click', () => { sel.has(v) ? sel.delete(v) : sel.add(v); c.classList.toggle('on'); renderGrid(); });
      container.append(c);
    }
  }

  function pruneFilter(sel, known) {
    const set = new Set(known);
    for (const v of [...sel]) if (!set.has(v)) sel.delete(v);
  }

  function renderFilters() {
    // A selected set/faction/type whose last card was renamed, removed or reset would otherwise
    // leave a checked-but-invisible filter that empties the grid with no chip to clear it.
    pruneFilter(filters.sets, allSets());
    pruneFilter(filters.factions, allFactions());
    pruneFilter(filters.types, allTypes());
    if (filters.keyword && !state.keywords.some(k => !k.removed && k.id === filters.keyword)) filters.keyword = '';
    const stEl = $('fStatus'); stEl.textContent = '';
    for (const [v, lbl] of [['all', 'All'], ['new', 'New'], ['modified', 'Modified'], ['removed', 'Removed'], ['untouched', 'Untouched']]) {
      const c = el('span', 'chip' + (filters.status === v ? ' on' : ''), lbl);
      c.addEventListener('click', () => { filters.status = v; renderFilters(); renderGrid(); });
      stEl.append(c);
    }
    chipRow($('fSets'), allSets(), filters.sets, id => setName(id).replace('Shards of Infinity — ', ''));
    chipRow($('fFactions'), allFactions(), filters.factions);
    chipRow($('fTypes'), allTypes(), filters.types);
    for (const [id, key] of [['fCostMin', 'costMin'], ['fCostMax', 'costMax']]) {
      const s = $(id); s.textContent = '';
      s.append(el('option', '', 'any'));
      for (let i = 0; i <= 9; i++) { const o = el('option', '', String(i)); o.value = String(i); s.append(o); }
      s.value = filters[key] == null ? 'any' : String(filters[key]);
      s.onchange = () => { filters[key] = s.value === 'any' ? null : +s.value; renderGrid(); };
    }
    const kwSel = $('fKeyword'); kwSel.textContent = '';
    kwSel.append(el('option', '', 'any'));
    for (const k of state.keywords.filter(k => !k.removed)) {
      const o = el('option', '', Core.esc(k.name)); o.value = k.id; kwSel.append(o);
    }
    kwSel.value = filters.keyword || 'any';
    if (kwSel.value !== filters.keyword) filters.keyword = '';
    kwSel.onchange = () => { filters.keyword = kwSel.value === 'any' ? '' : kwSel.value; renderGrid(); };
  }

  /* ---------- editor drawer ---------- */
  function fieldRow(lbl, inputHtml) { return `<div class="frow"><label>${lbl}</label>${inputHtml}</div>`; }

  // A card may carry ANY combination of types (checklist). Champion-ness is still derived the
  // way the engine does it (Champion / Ingeminex / Relic+Defense) — the hint reflects that.
  function typeChecklist(c) {
    const chosen = new Set(Core.typesOf(c));
    const boxes = allTypes().map(t =>
      `<label class="typebox${chosen.has(t) ? ' on' : ''}"><input type="checkbox" data-type="${Core.esc(t)}"${chosen.has(t) ? ' checked' : ''}>${Core.esc(t)}</label>`
    ).join('');
    const hint = isChampionCard(c)
      ? 'This card is a <b>champion</b> — set its <b>Defense</b> below (deploys to your champion zone).'
      : (Core.hasType(c, 'Relic') ? 'A Relic with <b>Defense &gt; 0</b> becomes a <b>Relic Champion</b> (Praetorian-02).' : '');
    return `<div class="frow"><label>Types <span style="text-transform:none;letter-spacing:0;opacity:.7">(check any combination)</span></label>` +
      `<div class="typegrid" id="edTypes">${boxes}` +
      `<input id="edTypeCustom" class="typecustom" placeholder="+ custom type" spellcheck="false"></div>` +
      (hint ? `<div class="hintline" style="margin-top:6px">${hint}</div>` : '') + `</div>`;
  }

  function openEditor() {
    const ed = $('editor');
    const c = state.cards.find(x => x.id === selectedId);
    if (!c) { ed.classList.remove('open'); return; }
    const isNew = !baseMap.has(c.id);
    const status = Core.cardStatus(c, baseMap);
    ed.classList.add('open');
    const opt = (v, cur) => `<option${v === cur ? ' selected' : ''}>${Core.esc(v)}</option>`;
    const numVal = v => v == null ? '' : String(v);
    ed.innerHTML =
      `<h2>${Core.esc(c.name)} ${status ? `<span class="status ${status}" style="position:static">${status}</span>` : ''}</h2>` +
      `<div class="idline">id: ${Core.esc(c.id)}${isNew ? ' (editable below)' : ''} · baseline: ${isNew ? 'none — new design' : 'live card'}</div>` +
      fieldRow('Name', `<input id="edName" value="${Core.esc(c.name)}">`) +
      (isNew ? fieldRow('Id (snake_case)', `<input id="edId" value="${Core.esc(c.id)}" spellcheck="false">`) : '') +
      `<div class="frow2">` +
      `<div class="frow"><label>Set</label><input id="edSet" list="dlSets" value="${Core.esc(c.set)}"></div>` +
      `<div class="frow"><label>Faction</label><input id="edFaction" list="dlFactions" value="${Core.esc(c.faction)}"></div>` +
      `</div>` +
      typeChecklist(c) +
      `<div class="frow2">` +
      `<div class="frow"><label>Card reads as</label><div class="typePreview">${Core.esc(typeLine(c))}${isChampionCard(c) ? ' <span class="champTag">champion</span>' : ''}</div></div>` +
      `<div class="frow"><label>Copies (qty)</label><input id="edQty" type="number" min="0" value="${c.qty}"></div>` +
      `</div>` +
      `<div class="frow3">` +
      `<div class="frow"><label>Cost <span class="unit">gems</span></label><input id="edCost" type="number" min="0" value="${numVal(c.cost)}" placeholder="—"></div>` +
      `<div class="frow"><label${cardTakesDefense(c) ? '' : ' style="opacity:.5"'}>Defense</label><input id="edDefense" type="number" min="0" value="${numVal(c.defense)}" placeholder="${cardTakesDefense(c) ? (Core.hasType(c, 'Relic') && !isChampionCard(c) ? 'ally' : '—') : 'n/a'}"${cardTakesDefense(c) ? '' : ' disabled title="Only champions (Champion / Ingeminex / Relic Champion) have defense"'}></div>` +
      `<div class="frow"><label>Shield <span class="unit">block</span></label><input id="edShield" type="number" min="0" value="${numVal(c.shield)}" placeholder="—" title="Shield reveals from hand to block that much damage"></div>` +
      `</div>` +
      `<div class="frow"><label>Art file <span class="unit">(blank = black)</span></label><input id="edArt" value="${Core.esc(c.art || '')}" placeholder="black" spellcheck="false"></div>` +
      fieldRow('Rules text', `<textarea id="edText" rows="6">${Core.esc(c.text)}</textarea>`) +
      fieldRow('Art prompt (for the art pipeline later)', `<textarea id="edArtPrompt" rows="2">${Core.esc(c.artPrompt || '')}</textarea>`) +
      fieldRow('Design notes', `<textarea id="edNotes" rows="2">${Core.esc(c.notes || '')}</textarea>`) +
      `<div id="edDiff"></div>` +
      `<div class="btns">` +
      `<button id="edDuplicate">Duplicate</button>` +
      (!isNew ? `<button id="edRevert" ${status === 'modified' ? '' : 'disabled'}>Revert to live</button>` : '') +
      (!isNew ? `<button id="edRemove" class="danger">${c.removed ? 'Restore' : 'Remove from pool'}</button>` : '') +
      (isNew ? `<button id="edDelete" class="danger">Delete new card</button>` : '') +
      `<button id="edClose">Close</button>` +
      `</div>` +
      `<datalist id="dlSets">${allSets().map(s => `<option value="${Core.esc(s)}">`).join('')}</datalist>` +
      `<datalist id="dlFactions">${allFactions().map(f => `<option value="${Core.esc(f)}">`).join('')}</datalist>`;

    renderEditorDiff(c);

    const num = v => v.trim() === '' ? null : Math.max(0, Math.floor(+v) || 0);
    // The editor is a separate panel from the grid, so re-rendering the grid on each keystroke
    // never steals focus from the field being typed in. A full renderGrid keeps every view
    // consistent — duplicate nodes under keyword grouping, group membership, headers, status
    // badges — which per-node patching could not (a card can appear in several keyword groups).
    const bind = (id, fn, structural) => {
      const n = $(id);
      if (n) n.addEventListener('input', () => {
        fn(n.value);
        commit(structural); // structural also refreshes the filter chips (new set/faction)
        if (!structural) renderGrid();
        renderEditorDiff(state.cards.find(x => x.id === selectedId));
      });
    };
    bind('edName', v => { c.name = v; if (isNew && !c._idTouched) { c.id = uniqueId(Core.slugify(v), c.id); selectedId = c.id; const idIn = $('edId'); if (idIn) idIn.value = c.id; } });
    bind('edId', v => { c._idTouched = true; const want = Core.slugify(v); if (want !== c.id) { c.id = uniqueId(want, c.id); selectedId = c.id; } });
    bind('edSet', v => c.set = v.trim() || 'new_dlc', true);
    bind('edFaction', v => c.faction = v.trim() || 'None', true);
    bind('edQty', v => c.qty = num(v) ?? 0);
    bind('edCost', v => c.cost = num(v));
    bind('edShield', v => c.shield = num(v));
    // defense: keep focus while live-refreshing the type-line preview (champion tag) in place
    const defNode = $('edDefense');
    if (defNode) defNode.addEventListener('input', () => {
      c.defense = num(defNode.value);
      commit(false); renderGrid();
      const prev = ed.querySelector('.typePreview');
      if (prev) prev.innerHTML = Core.esc(typeLine(c)) + (isChampionCard(c) ? ' <span class="champTag">champion</span>' : '');
      renderEditorDiff(state.cards.find(x => x.id === selectedId));
    });
    bind('edArt', v => c.art = v.trim());
    bind('edText', v => c.text = v);
    bind('edArtPrompt', v => c.artPrompt = v);
    bind('edNotes', v => c.notes = v);
    const retypeCard = () => {
      // Champion / Ingeminex always need a defense to survive; seed one. Defense is meaningless
      // for a card that can't hold it (no Champion/Monster/Relic) — clear it so the card reads clean.
      if ((Core.hasType(c, 'Champion') || Core.hasType(c, 'Monster')) && (c.defense || 0) === 0) c.defense = 1;
      if (!cardTakesDefense(c)) c.defense = null;
      commit(); openEditor(); // rebuild the drawer: hint + defense gating depend on the type set
    };
    $('edTypes').querySelectorAll('input[type=checkbox]').forEach(cb => cb.addEventListener('change', () => {
      const chosen = new Set(Core.typesOf(c));
      cb.checked ? chosen.add(cb.dataset.type) : chosen.delete(cb.dataset.type);
      c.types = Core.canonTypes([...chosen]);
      retypeCard();
    }));
    const customIn = $('edTypeCustom');
    if (customIn) customIn.addEventListener('keydown', e => {
      if (e.key !== 'Enter') return;
      e.preventDefault();
      const t = customIn.value.trim();
      if (!t) return;
      c.types = Core.canonTypes([...Core.typesOf(c), t]);
      retypeCard();
    });
    $('edDuplicate').addEventListener('click', () => {
      const copy = Core.clone(c); delete copy.removed; delete copy._idTouched;
      copy.id = uniqueId(c.id); copy.name = c.name + ' (variant)';
      state.cards.push(copy); selectedId = copy.id; commit(); openEditor();
    });
    const rev = $('edRevert');
    if (rev) rev.addEventListener('click', () => {
      const base = baseMap.get(c.id);
      for (const f of CARD_FIELDS) c[f] = Core.clone(base[f]);
      commit(); openEditor();
    });
    const rem = $('edRemove');
    if (rem) rem.addEventListener('click', () => { c.removed = !c.removed; commit(); openEditor(); });
    const del = $('edDelete');
    if (del) del.addEventListener('click', () => {
      state.cards = state.cards.filter(x => x.id !== c.id);
      selectedId = null; commit(); openEditor();
    });
    $('edClose').addEventListener('click', () => { selectedId = null; $('editor').classList.remove('open'); renderGrid(); });
  }

  function renderEditorDiff(c) {
    const box = $('edDiff');
    if (!box || !c) return;
    const base = baseMap.get(c.id);
    if (!base) { box.style.display = 'none'; return; }
    const d = Core.diffFields(base, c, CARD_FIELDS);
    const keys = Object.keys(d);
    if (!keys.length) { box.style.display = 'none'; return; }
    box.style.display = 'block';
    box.innerHTML = '<table>' + keys.map(f =>
      `<tr><td class="f">${f}</td><td class="was">${Core.esc(Core.fmtVal(d[f].was))}</td><td class="now">${Core.esc(Core.fmtVal(d[f].now))}</td></tr>`
    ).join('') + '</table>';
  }

  function uniqueId(base, selfId) {
    let id = base, n = 2;
    const ids = new Set(state.cards.filter(c => c.id !== selfId).map(c => c.id));
    while (ids.has(id)) id = `${base}_${n++}`;
    return id;
  }

  /* ---------- keyword manager ---------- */
  function renderKeywords() {
    const t = $('kwTable');
    t.innerHTML = '<tr><th>Keyword</th><th>Faction</th><th>Meaning (tooltip)</th>' +
      '<th title="Regex matched against rules text (blank = the whole word). Only for text keywords.">Detection</th>' +
      '<th style="text-align:right">Cards</th><th></th><th></th></tr>';
    for (const k of state.keywords) {
      const st = Core.kwStatus(k, baseKwMap);
      const isNew = !baseKwMap.has(k.id);
      const isText = k.kind === 'text';
      const tr = el('tr');
      if (k.removed) tr.style.opacity = '.45';
      const badRe = isText && !Core.keywordRegex(k); // invalid custom pattern
      tr.innerHTML =
        `<td class="kn"><input value="${Core.esc(k.name)}" data-f="name" ${!isText && !isNew ? 'disabled' : ''}>` +
        `${!isText ? `<div class="kfac">${k.kind === 'stat' ? 'stat-based' : 'type-based'}</div>` : ''}</td>` +
        `<td><input value="${Core.esc(k.faction || '')}" data-f="faction" placeholder="—" style="width:88px"></td>` +
        `<td><textarea rows="2" data-f="meaning">${Core.esc(k.meaning)}</textarea></td>` +
        `<td>${isText
          ? `<input value="${Core.esc(k.pattern || '')}" data-f="pattern" placeholder="\\b${Core.esc(k.name)}\\b" spellcheck="false" style="width:150px;font-family:var(--mono);font-size:11px${badRe ? ';border-color:var(--rem)' : ''}" title="regex; blank = whole word">` +
            `<input value="${Core.esc(k.flags || '')}" data-f="flags" placeholder="flags e.g. i" spellcheck="false" style="width:70px;font-family:var(--mono);font-size:11px;margin-top:3px">` +
            (badRe ? '<div class="kfac" style="color:var(--rem)">invalid regex</div>' : '')
          : '<span class="kfac">auto</span>'}</td>` +
        `<td class="ku">${Core.keywordUsage(k, state.cards)}</td>` +
        `<td>${st ? `<span class="st ${st}">${st}</span>` : ''}</td>` +
        `<td>${isNew
          ? '<button data-act="delete" class="danger">✕</button>'
          : `<button data-act="toggle" class="danger">${k.removed ? 'restore' : 'remove'}</button>`}</td>`;
      tr.querySelectorAll('[data-f]').forEach(inp => inp.addEventListener('input', () => {
        const f = inp.dataset.f;
        if (f === 'name' && isText) {
          // Follow a rename only when the pattern is the implicit whole-word form (blank, or the
          // literal auto-pattern of EITHER the old or the current baseline name). Store the new
          // auto-pattern explicitly so renaming back to the baseline name restores the baseline
          // pattern exactly (no permanent phantom "modified"). Hand-written patterns are left alone.
          const base = baseKwMap.get(k.id);
          const autos = [Core.autoPattern(k.name)];
          if (base) autos.push(Core.autoPattern(base.name), base.pattern || Core.autoPattern(base.name));
          if (!k.pattern || autos.includes(k.pattern)) k.pattern = Core.autoPattern(inp.value) || '';
        }
        k[f] = inp.value;
        commit(false); renderGrid();
        // reflect a newly (in)valid pattern without stealing focus from another field
        if (f === 'pattern') { const bad = !Core.keywordRegex(k); inp.style.borderColor = bad ? 'var(--rem)' : ''; }
      }));
      const actBtn = tr.querySelector('[data-act]');
      actBtn.addEventListener('click', () => {
        if (actBtn.dataset.act === 'delete') state.keywords = state.keywords.filter(x => x.id !== k.id);
        else k.removed = !k.removed;
        commit(); renderKeywords();
      });
      t.append(tr);
    }
  }

  /* ---------- persistence ---------- */
  const safeStorage = {
    set(v) { try { localStorage.setItem(LS_KEY, v); } catch { /* quota / storage blocked */ } },
    get() { try { return localStorage.getItem(LS_KEY); } catch { return null; } },
    clear() { try { localStorage.removeItem(LS_KEY); } catch { /* storage blocked */ } },
  };

  function commit(fullRender = true) {
    dirty = true;
    $('dirtyDot').classList.add('on');
    safeStorage.set(JSON.stringify(Core.buildSession(state, B, baseMap, baseKwMap)));
    if (fullRender) { renderFilters(); renderGrid(); }
    else renderStats(visibleCards());
  }

  function download(name, text, mime) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([text], { type: mime }));
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 5000);
  }

  let bannerTimer = null;
  function banner(msg, sticky) {
    const b = $('banner');
    if (bannerTimer) { clearTimeout(bannerTimer); bannerTimer = null; } // never let an old timer hide a new message
    if (!msg) { b.style.display = 'none'; return; }
    b.style.display = 'block';
    b.innerHTML = Core.esc(msg) + ' <button id="bannerX">dismiss</button>';
    $('bannerX').addEventListener('click', () => banner(null));
    if (!sticky) bannerTimer = setTimeout(() => { b.style.display = 'none'; bannerTimer = null; }, 12000);
  }

  const clearFilters = () => {
    Object.assign(filters, { q: '', status: 'all', sets: new Set(), factions: new Set(), types: new Set(), costMin: null, costMax: null, keyword: '' });
    $('fQ').value = '';
  };

  // Apply an opened/restored session and mirror it into the autosave so a later reload restores
  // THIS session (not a stale one). Returns warnings for the caller to surface. Never sets dirty:
  // the content is either on disk (Open) or already the autosave (boot restore).
  function loadSessionObject(obj) {
    const { state: st, warnings } = Core.applySession(B, obj);
    state = st;
    selectedId = null;
    clearFilters();
    $('sessionName').value = state.sessionName;
    $('sessionNotes').value = state.notes;
    $('editor').classList.remove('open');
    safeStorage.set(JSON.stringify(Core.buildSession(state, B, baseMap, baseKwMap)));
    dirty = false; $('dirtyDot').classList.remove('on');
    renderFilters(); renderGrid(); renderKeywords();
    return warnings;
  }

  /* ---------- topbar wiring ---------- */
  $('sessionName').addEventListener('input', e => { state.sessionName = e.target.value; commit(false); });
  $('sessionNotes').addEventListener('input', e => { state.notes = e.target.value; commit(false); });
  $('fQ').addEventListener('input', e => { filters.q = e.target.value; renderGrid(); });
  $('gGroup').addEventListener('change', renderGrid);
  $('gSort').addEventListener('change', renderGrid);

  $('btnNewCard').addEventListener('click', () => {
    const lastNewSet = [...state.cards].reverse().find(c => !baseMap.has(c.id))?.set;
    const card = {
      id: uniqueId('new_card'), name: 'New Card', set: lastNewSet || 'new_dlc', faction: 'None',
      types: ['Ally'], cost: 0, qty: 1, defense: null, shield: null, text: '', art: '', artPrompt: '', notes: '',
    };
    state.cards.push(card);
    selectedId = card.id;
    commit();
    if (!visibleCards().some(x => x.id === card.id)) {
      clearFilters(); // active filters would hide the fresh card — clear them so it's never "lost"
      renderFilters(); renderGrid();
    }
    openEditor();
    document.querySelector(`.card[data-id="${CSS.escape(card.id)}"]`)?.scrollIntoView({ block: 'center' });
  });

  $('btnSave').addEventListener('click', () => {
    const sess = Core.buildSession(state, B, baseMap, baseKwMap);
    const slug = Core.slugify(state.sessionName || 'session');
    download(`soi-design-${slug}-${sess.session.savedAt.slice(0, 10)}.json`, JSON.stringify(sess, null, 2), 'application/json');
    // work is now safe on disk; the autosave still mirrors it so a reload continues seamlessly
    dirty = false; $('dirtyDot').classList.remove('on');
    banner(`Saved to your downloads. Keep designing — your work also stays in this browser.`);
  });

  $('btnBrief').addEventListener('click', () => {
    const slug = Core.slugify(state.sessionName || 'session');
    download(`soi-design-brief-${slug}.md`, Core.buildBrief(state, B, baseMap, baseKwMap), 'text/markdown');
  });

  $('btnOpen').addEventListener('click', () => $('fileInput').click());
  $('fileInput').addEventListener('change', async e => {
    const file = e.target.files[0];
    e.target.value = '';
    if (!file) return;
    if (dirty && !confirm('Opening a file replaces your current unsaved changes. Continue?')) return;
    try {
      const warnings = loadSessionObject(JSON.parse(await file.text()));
      // Show warnings if any (they'd otherwise be lost); only announce a clean open when there are none.
      banner(warnings.length ? `Opened ${file.name} with notes — ${warnings.join(' — ')}` : `Opened ${file.name}.`, warnings.length > 0);
    } catch (err) { banner('Could not open file: ' + err.message, true); }
  });

  $('btnReset').addEventListener('click', () => {
    if (!confirm('Discard EVERY change and return to the live pool?')) return;
    safeStorage.clear();
    state = { cards: B.cards.map(Core.clone), keywords: B.keywords.map(Core.clone), sessionName: '', notes: '' };
    selectedId = null; dirty = false;
    clearFilters();
    $('dirtyDot').classList.remove('on');
    $('sessionName').value = '';
    $('sessionNotes').value = '';
    $('gGroup').value = 'faction';
    $('editor').classList.remove('open');
    banner(null);
    renderFilters(); renderGrid(); renderKeywords();
  });

  $('btnKeywords').addEventListener('click', () => { renderKeywords(); $('kwOverlay').classList.add('open'); });
  const closeKw = () => { $('kwOverlay').classList.remove('open'); renderFilters(); renderGrid(); };
  $('btnKwClose').addEventListener('click', closeKw);
  // Close on a genuine backdrop click only — track where the press STARTED so a text selection
  // inside a textarea that happens to release on the backdrop doesn't dismiss the modal.
  let kwDownOnBackdrop = false;
  $('kwOverlay').addEventListener('pointerdown', e => { kwDownOnBackdrop = e.target === $('kwOverlay'); });
  $('kwOverlay').addEventListener('click', e => { if (e.target === $('kwOverlay') && kwDownOnBackdrop) closeKw(); });
  $('btnAddKw').addEventListener('click', () => {
    let id = 'new_keyword', n = 2;
    while (state.keywords.some(k => k.id === id)) id = `new_keyword_${n++}`;
    state.keywords.push({ id, name: 'NewKeyword', faction: '', kind: 'text', pattern: '', flags: '', meaning: 'Explain how it activates.' });
    commit(false); renderKeywords();
  });

  window.addEventListener('beforeunload', e => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });

  /* ---------- boot ---------- */
  try {
    const saved = safeStorage.get();
    if (saved) {
      const obj = JSON.parse(saved);
      const n = (obj.cards?.added?.length || 0) + (obj.cards?.modified?.length || 0) + (obj.cards?.removed?.length || 0) +
        (obj.keywords?.added?.length || 0) + (obj.keywords?.modified?.length || 0) + (obj.keywords?.removed?.length || 0);
      const hasText = !!(obj.session?.name || obj.session?.notes);
      if (n > 0 || hasText) {
        const warnings = loadSessionObject(obj);
        const when = obj.session?.savedAt?.slice(0, 16).replace('T', ' ') || '?';
        const head = n > 0
          ? `Restored your last session from this browser (${n} change${n > 1 ? 's' : ''}, ${when}). Use Save to keep a file, or Reset to discard.`
          : `Restored your last session from this browser (${when}).`;
        banner(warnings.length ? `${head} — ${warnings.join(' — ')}` : head, true);
      }
    }
  } catch { /* corrupted autosave — start clean */ }

  renderFilters(); renderGrid();
})();
