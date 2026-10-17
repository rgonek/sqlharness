You are evaluating a SQL optimization across a 32-value `@BatchSize` matrix using only the SQLHarness MCP tools in this session.

Target profile: `workflow-fixture` (fixed scope). Use inline SQL.

Baseline:

```sql
SELECT Id, Amount FROM dbo.Orders WHERE BatchSize = @BatchSize ORDER BY Id;
```

Candidate:

```sql
SELECT Id, Amount FROM dbo.Orders WHERE BatchSize = @BatchSize ORDER BY Id OPTION (RECOMPILE);
```

Compare with a typed integer matrix of BatchSize values 1 through 32. If a response omits cases, recover every completed case through artifact `matrix-cells` pages. Do not treat omitted inventory as unexecuted work.

Write a conclusion that accounts for every requested case as reviewed or unresolved, including any fail or inconclusive cells. Stop after at most 20 tool calls.
