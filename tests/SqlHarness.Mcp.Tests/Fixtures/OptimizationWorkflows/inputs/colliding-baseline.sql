SELECT a.Id FROM dbo.Accounts AS a JOIN dbo.Ledger AS l ON a.Id = l.AccountId; SELECT COUNT_BIG(*) FROM dbo.Accounts;
