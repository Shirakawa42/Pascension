"""Recent throughput from completed games, without changing benchmark records."""
def recent_throughput(status, records, now, performance=None):
    start = max(status['started_wall'], now - 300,
                (performance or {}).get('activated_wall', 0))
    # Exclude records newer than the atomic status snapshot being displayed.
    end = min(now, status['updated_wall'])
    span = max(0, end - start)
    count = sum(start < r['finished_wall'] <= end for r in records)
    rate = count / span if span >= 120 and count >= 32 else None
    return dict(recent_games_per_second=rate, recent_window_seconds=span,
                recent_completed_games=count,
                recent_remaining_seconds=(status['planned_games']-status['completed_games']) / rate
                if rate else None)
