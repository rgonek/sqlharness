SELECT Id, Amount FROM dbo.Orders WHERE BatchSize = @BatchSize ORDER BY Id;
