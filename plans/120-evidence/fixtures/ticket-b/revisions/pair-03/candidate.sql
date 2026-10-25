select event_id, label from events where event_date = @boundaryDate and is_visible = 1 and is_public = 1 order by event_id;
