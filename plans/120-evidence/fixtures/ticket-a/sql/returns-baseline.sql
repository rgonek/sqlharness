select return_id, reason from returns where customer_key = @customerId order by return_id;
