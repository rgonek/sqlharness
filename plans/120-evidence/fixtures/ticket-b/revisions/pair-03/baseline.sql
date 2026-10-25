select event_id, label from events where event_date = @boundaryDate order by event_id;
