select account_id, event_count from events where is_visible = @includeHidden order by event_id;
