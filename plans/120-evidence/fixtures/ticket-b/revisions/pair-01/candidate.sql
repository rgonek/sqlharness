select event_id, event_date from events where event_date >= @boundaryDate and is_visible = 1 order by event_id;
