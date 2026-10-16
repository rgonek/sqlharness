SELECT id, region FROM public.orders WHERE region = $1 ORDER BY id LIMIT 100;
