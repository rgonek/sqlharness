select return_id, reason from returns where customer_key = @customerId and is_void = 0 order by return_id;
