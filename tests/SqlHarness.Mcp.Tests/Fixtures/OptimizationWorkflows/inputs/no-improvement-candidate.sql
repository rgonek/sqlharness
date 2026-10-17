SELECT Id, Name FROM dbo.Orders WHERE Region = @Region ORDER BY Id OPTION (FAST 1);
