select account_id, count(*) as event_count from events where is_visible = @includeHidden group by account_id order by account_id;
