You are diagnosing why a candidate batch differs using only the SQLHarness MCP tools in this session.

Target profile: `workflow-fixture` (fixed scope). Use inline SQL.

Baseline:

```sql
SELECT a.Id FROM dbo.Accounts AS a JOIN dbo.Ledger AS l ON a.Id = l.AccountId; SELECT COUNT_BIG(*) FROM dbo.Accounts;
```

Candidate:

```sql
SELECT a.Id FROM dbo.Accounts AS a JOIN dbo.Ledger AS l ON a.Id = l.AccountId; SELECT COUNT_BIG(*) FROM dbo.Accounts WHERE a.Active = 1;
```

Compare the batches, then read saved plan pages for the operator that actually carries a warning. Node ids may repeat across statements. Do not treat a matching node id as proof that two operators are the same source.

If you name a causal operator, identify its saved variant, statement, and node path from the artifact `plan` section. Stop after at most 20 tool calls.
