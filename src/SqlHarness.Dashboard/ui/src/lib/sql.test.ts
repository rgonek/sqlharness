import { expect, test } from "vitest"
import { formatSql, highlightSql } from "@/lib/sql"

test("formats T-SQL and Postgres with their dialects", () => {
  expect(formatSql("select a,b from t where x=1", "sqlserver")).toBe("select\n  a,\n  b\nfrom\n  t\nwhere\n  x = 1")
  expect(formatSql("select [a] from [t]", null)).toContain("[a]")
  expect(formatSql("select a::int from t", "postgres")).toContain("a::int")
})

test("unparsable SQL is returned unchanged", () => {
  const odd = "select ' unterminated"
  expect(formatSql(odd, "sqlserver")).toBe(odd)
})

test("escapes html in sql", () => {
  const html = highlightSql("select '<script>alert(1)</script>'")
  expect(html).not.toContain("<script>")
  expect(html).toContain("&lt;script&gt;")
  expect(html).toContain("hljs-keyword")
})
