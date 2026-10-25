select event_id, event_date from events where event_date >= @boundaryDate order by event_id;
