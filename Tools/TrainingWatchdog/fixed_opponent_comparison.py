"""Compare policy versions while holding each hero's opponent policy fixed."""
import math

from paired_followup import ArenaGame, HEROES, summarize_arena


def compatible_training_changes(older_inventory, runtime_inventory):
    """Keep older manifests intact when only training code has changed.

    The caller must separately bind each inventory to its source fingerprint,
    verify policy files and Host, and load the ordinary model.Policy class.
    Every inference/engine file must remain byte-identical.
    """
    older, runtime = older_inventory["files"], runtime_inventory["files"]
    model = "Tools/ZeroDepthTraining/model.py"
    if model not in older or model not in runtime or older[model] != runtime[model]:
        raise ValueError("Inference model source differs")
    changed = sorted(path for path in set(older) | set(runtime) if older.get(path) != runtime.get(path))
    allowed = {"Tools/ZeroDepthTraining/train.py", "Tools/ZeroDepthTraining/performance_upgrade.py",
               "Tools/ZeroDepthTraining/opponent_archive.py"}
    if any(path not in allowed and not path.startswith("Tools/ZeroDepthTraining/tests/") for path in changed):
        raise ValueError("Inference or engine source differs")
    return changed


def compare_fixed_opponent(older, newer, *, alpha=.05):
    """Pair identical engine seeds and seats across two complete frozen arenas.

    A direct model-versus-model hero difference also changes the opposing
    model. Here both versions face exactly the same frozen opponent. Rez is
    the declared primary hero; the five-hero intervals cover their family.
    """
    if isinstance(alpha, bool) or not isinstance(alpha, (int, float)) or not 0 < alpha < 1:
        raise ValueError("Invalid comparison confidence")
    stable = ("baseline", "planned_games", "seed_base", "policy_seed", "batch", "workers",
              "hero_mode", "hero_assignment_version", "action_selection", "search_depth",
              "device", "precision", "pairing", "block_policy_seeds",
              "mirror_initial_publications")
    if any(older[key] != newer[key] for key in stable):
        raise ValueError("Fixed opponent, sampling plan or initial states differ")
    count = older["planned_games"]
    if type(count) is not int or count < 40 or count % 40 or older["search_depth"] != 0:
        raise ValueError("Complete balanced zero-depth games required")
    pairs = count // 2
    expected = {(seed, seat) for seed in range(older["seed_base"], older["seed_base"] + pairs)
                for seat in (0, 1)}
    mapped = []
    for arena in (older, newer):
        if (arena.get("integrity_verified") is not True
                or arena.get("initial_publication_mirrors_verified") is not True
                or arena.get("promotion_allowed") is not False):
            raise ValueError("Unverified or non-exploratory arena")
        records = [ArenaGame(**row) for row in arena["results"]]
        summary, _, coverage = summarize_arena(records, planned_pairs=pairs)
        if (not summary["evaluation_finished"] or summary["censored"]
                or any(row["candidate_seat0"] != count // 40
                       or row["candidate_seat1"] != count // 40 for row in coverage.values())):
            raise ValueError("Incomplete or unbalanced comparison")
        for key in ("recorded_games", "wins", "losses", "draws", "censored", "resolved_game_score"):
            if summary[key] != arena["summary"][key]:
                raise ValueError("Published summary differs from raw outcomes")
        rows = {(row.seed, row.learner_seat): row for row in records}
        if len(rows) != count or set(rows) != expected:
            raise ValueError("Missing, duplicate or foreign seed/seat")
        mapped.append(rows)
    scores = {hero: [[], []] for hero in HEROES}
    pair_differences = []
    for seed in range(older["seed_base"], older["seed_base"] + pairs):
        differences = []
        for seat in (0, 1):
            left, right = (rows[seed, seat] for rows in mapped)
            if (left.hero0, left.hero1) != (right.hero0, right.hero1):
                raise ValueError("Compared hero assignments differ")
            hero = (left.hero0, left.hero1)[seat]
            values = [.5 if row.winner == -1 else float(row.winner == seat) for row in (left, right)]
            for target, value in zip(scores[hero], values):
                target.append(value)
            differences.append(values[1] - values[0])
        pair_differences.append(sum(differences) / 2)

    def interval(delta, observations, error):
        radius = math.sqrt(2 * math.log(2 / error) / observations)
        return [max(-1., delta - radius), min(1., delta + radius)]

    heroes = {}
    for hero, (left, right) in scores.items():
        if len(left) != count // 5 or len(right) != len(left):
            raise ValueError("Wrong per-hero coverage")
        delta = (sum(right) - sum(left)) / len(left)
        heroes[hero] = dict(games_per_version=len(left), older_score=sum(left) / len(left),
                            newer_score=sum(right) / len(right), paired_difference=delta,
                            simultaneous_difference_interval=interval(delta, len(left), alpha / len(HEROES)))
    rez = heroes["rez"]
    rez_primary = {**rez, "primary_difference_interval": interval(rez["paired_difference"],
                                                                 rez["games_per_version"], alpha)}
    overall = sum(pair_differences) / pairs
    return dict(schema="shards-fixed-opponent-progress-v1", games_per_version=count,
                fixed_opponent=older["baseline"], older_policy=older["candidate"], newer_policy=newer["candidate"],
                overall_paired_difference=overall,
                overall_difference_interval=interval(overall, pairs, alpha),
                heroes=heroes, rez_primary=rez_primary, confidence=1-alpha, promotion_allowed=False,
                interpretation="Same frozen opponent, engine seeds, seats and sampling plan for both versions. "
                               "Rez is the declared primary hero; five-hero intervals are jointly adjusted. "
                               "Exploratory comparison, not a repeated-testing promotion guarantee.")
