# Batched balance monitoring

The local dashboard at http://localhost:8768/ now includes heroes, relics,
cards, destinies, hero-by-seat results, and a directional hero matchup matrix.
Item views support exact per-hero acquisition filtering. Filters distinguish
unobserved definitions, small samples, expansion replacements, and acquisition
modes. Raw snapshots remain downloadable.

## Collection and interpretation

The production host records public acquisition events and completed game
outcomes in reused lane-local counters. It publishes cumulative snapshots every
10,000 completed games, plus initial and final snapshots. Serialization and
atomic publication use a coalesced background writer. Immutable publication
history supports a recent window of approximately 100,000 completed games.
The monitor recalculates the derived tables only when a publication changes.

Both players contribute correlated observations from each real game. Counts
distinguish real-game clusters, player outcomes, and repeated acquisition
events. Unfinished/capped games are separate; they are never fabricated draws.
Normal purchases, fast-play entries, effect-driven acquisitions, relics, and
destinies remain separate. Legal-menu opportunity counts are unavailable in
this inexpensive path, so it does not invent pick rates. A fast-play entry may
later become a permanent acquisition through an effect.

Additional observations include seat advantage, final-round distribution,
winner and loser mastery, winner health, and permanent collection size.
These are associations under the policies currently training, not causal card
strength or proof of optimal hero balance. The descriptive intervals assume
independent game clusters, pool changing policies, and do not adjust for
ranking many items or repeatedly checking the dashboard.

Frozen-policy evaluations form a separate source. They have fixed policy and
opponent identities, seat-swapped seeds, and selected-action/opportunity
telemetry. They do not infer effect acquisitions that their telemetry did not
observe. CPU shadow matches can contend with training, so this path is bounded
and infrequent; the large live tables do not require additional matches.

The mixed hero curriculum has separate `forced-random` and `natural-draft`
publications and source controls. Their counts must never be pooled silently.
Legacy natural training remains a separate source as well.

## Verified cost and correctness

The V4 host also replaces repeated faction scans with one exact pass.
Three paired full-batch CPU request measurements with statistics enabled had
a median 1.085x speedup over V3. A separate 10,438-concession-game CPU workload
exercised periodic background publication and final flushing; this is not a
learned-policy workload. See the [host report](../experiments/HostV4/README.md).

The stronger frozen GPU check used three complete 256-game cohorts per variant,
each preceded by its own warmup. All actions, retained tensors, outcomes,
ownership and behavior probabilities matched the V3 reference exactly.
Statistics counts, host exit, schema, and binary identity also passed.
Collection plus verification throughput was 35,858 retained rows/s with
statistics versus 35,870 rows/s for the combined V3 brackets: effectively
unchanged. This is neither a measured training speedup nor a strength gain.
The timed cohorts do not include the 10,000-game publication boundary; that
cost was tested separately above. Startup and optimizer work are excluded.
[GPU comparison](frozen-host-v4-2026-09-26.json).

The live continuation resumed from generation 3096 with all policy, Adam,
archive, RNG, and budget state preserved. No additional time allocation was
created. V4 source identity is
`3b8cf5b37bc032081d46c944b570ec64c3e231240f2e57ec82d8f3b43aad862c`.

The catalog metadata comes from the real V3 host catalog and engine card
definitions via `experiments/balance_catalog`. Its rules/catalog/observation
identity fields were added from the verified V3 run identity after matching
the host binary hash. V4 preserves those identities; monitoring rejects
incompatible rule, catalog, observation, binary, and cohort sources.

Tests cover cluster accounting, cumulative subtraction, publication windows,
identity guards, effect modes, censors, per-hero totals, cohort separation,
process health, safe downloads, and desktop/mobile browser rendering.


## Dedicated artwork gallery

The read-only [statistics window](http://127.0.0.1:8768/statistics) now uses the
actual 189 card-definition and five hero PNGs through a local allowlisted image
endpoint. Cards open enlarged art and current rules text. Source, expansion,
minimum sample count, search, exact per-hero acquisition filters, gallery/table
views, and hero matchups all use the same validated snapshots as monitoring.
A gallery page contains at most 24 lazy-loaded entries. HTTP caching and
unchanged-snapshot checks avoid reloading artwork or rebuilding views every poll.
The training dashboard opens a named statistics window, with a normal link
fallback. Neither page sends training controls or requests extra game samples.

The browser checks cover pagination, local asset lookup by definition identity,
unknown/censored scores, zero coverage, safe text rendering, exact hero rows,
source persistence, and desktop/mobile layout. A live read-only render loaded
all 24 images in the first card page and displayed real rules and intervals.
The captured snapshot held 69,740 engine games; the count subsequently changes
with each publication. Hero/card rows count completed player-game outcomes,
not independent real games; repeated acquisition event counts may be larger.
Separate acquisition modes are separate rows, so 316 card rows in that snapshot
did not mean 316 distinct card definitions. No new strength or causal balance
claim follows from making these observations visible.
