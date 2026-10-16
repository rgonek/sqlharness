You are evaluating a SQL optimization using only the SQLHarness MCP tools in this session.

Target profile: `workflow-fixture` (fixed scope). Use inline SQL; do not invent a live database.

Baseline:

```sql
SELECT Id, Name FROM dbo.Orders WHERE Region = @Region ORDER BY Id;
```

Candidate:

```sql
SELECT Id, Name FROM dbo.Orders WHERE Region = @Region ORDER BY Id OPTION (FAST 1);
```

Compare the two statements on representative `@Region` values. Decide whether the candidate is a demonstrated improvement. Use saved artifact sections if you need more than the compare summary. Stop after at most 20 tool calls.

Write a conclusion that separates execution status, result equivalence, and the latency verdict. Do not treat a regression `pass` as proof of improvement.
