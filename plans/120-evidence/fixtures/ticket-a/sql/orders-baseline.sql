select order_id, total from orders where customer_key = @customerId order by order_id;
