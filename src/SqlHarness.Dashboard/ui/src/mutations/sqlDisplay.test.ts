import { expect, test } from "vitest"
import { inlineMutationSql } from "./sqlDisplay"
const p = (name: string, value: string | null, type: string | null = null) => ({ name, value, type })
test("SQL Server lexical boundaries and precision", () => {
  const result = inlineMutationSql("SELECT @@ROWCOUNT, @A, @ab, [@a], '@a' /* outer /* @a */ @ab */ -- @a", "sqlserver", [p("a", "O'Brien"), p("ab", "9007199254740993", "bigint")])
  for (const text of ["N'O''Brien'", "9007199254740993", "@@ROWCOUNT", "[@a]", "'@a'", "/* outer /* @a */ @ab */"]) expect(result.sql).toContain(text)
  expect(result.reason).toBeUndefined()
})
test("PostgreSQL dollar quotes, comments, operators and empty text", () => {
  const result = inlineMutationSql("SELECT @a, $$@a$$, $body$@a$body$, '@a', x @> y -- @a", "postgres", [p("a", "")])
  for (const text of ["''", "$$@a$$", "$body$@a$body$", "x @> y"]) expect(result.sql).toContain(text)
  expect(result.reason).toBeUndefined()
})
test("unsupported, missing, duplicate, ambiguous and huge inputs retain exact original", () => {
  for (const params of [[p("a", "1", "geography")], [], [p("a", "1"), p("A", "2")], [p("a", "a\\b")]]) {
    const result = inlineMutationSql("SELECT @a", "postgres", params)
    expect(result.reason).toBeTruthy()
    expect(result.sql).toBe("SELECT @a")
  }
  expect(inlineMutationSql("x".repeat(100001), "postgres", []).reason).toBeTruthy()
})
test("null is NULL and default numeric-looking text stays text", () => {
  const result = inlineMutationSql("SELECT @a,@b,@c", "sqlserver", [p("a", null), p("b", "123"), p("c", "false", "bit")])
  expect(result.sql).toContain("NULL")
  expect(result.sql).toContain("N'123'")
  expect(result.sql).toMatch(/\b0\b/)
})

test("normalizes contract engine and parameter prefix without changing tokens", () => {
  const result = inlineMutationSql("SELECT @a, @ab", "SqlServer", [p("@a", "1", "int"), p("ab", "2", "int")])
  expect(result.reason).toBeUndefined()
  expect(result.sql).not.toContain("@a")
})
