select order_id, total from orders where customer_key = @customerId and is_archived = 0 order by order_id;
