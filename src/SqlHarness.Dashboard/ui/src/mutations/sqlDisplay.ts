import { format } from "sql-formatter"
import type { MutationParameter } from "./mutationStore"

const PREVIEW_CHARACTER_BUDGET = 100_000

/** Presentation only; never used to submit a decision or execute SQL. */
export function inlineMutationSql(sql: string, engine: string, parameters: MutationParameter[]): { sql: string; reason?: string } {
  const fallback = () => ({ sql, reason: "Values inline is unavailable for this SQL or parameter type. As sent shows the original request." })
  if (sql.length > 100_000 || parameters.length > 500 || parameters.reduce((n, p) => n + (p.value?.length ?? 0), 0) > 100_000) return fallback()
  engine = engine.toLowerCase()
  const postgres = engine === "postgres"
  if (!postgres && engine !== "sqlserver") return fallback()
  try {
    const values = new Map<string, string>()
    for (const parameter of parameters) {
      const name = parameter.name.replace(/^@/, "").toLowerCase()
      if (!/^[a-z_][a-z0-9_]*$/i.test(name) || values.has(name)) return fallback()
      const type = (parameter.type || "nvarchar").toLowerCase()
      const value = parameter.value
      let literal: string
      if (value === null) literal = "NULL"
      else if (/^(nvarchar|varchar|nchar|char)(\((max|\d+)\))?$/.test(type)) {
        if (postgres && value.includes("\\")) return fallback()
        literal = `${!postgres && type.startsWith("n") ? "N" : ""}'${value.replaceAll("'", "''")}'`
      } else if (/^(tinyint|smallint|int|bigint)$/.test(type) && /^[+-]?\d+$/.test(value)) {
        const ranges: Record<string, [bigint, bigint]> = { tinyint: [0n, 255n], smallint: [-32768n, 32767n], int: [-2147483648n, 2147483647n], bigint: [-9223372036854775808n, 9223372036854775807n] }
        const [min, max] = ranges[type]
        if (BigInt(value) < min || BigInt(value) > max) return fallback()
        literal = value
      }
      else if (/^(decimal|numeric)(\(\d+,\d+\))?$/.test(type) && /^[+-]?\d+(\.\d+)?$/.test(value)) {
        // .NET decimal binding can round beyond its precision.
        if (value.replace(/[^0-9]/g, "").length > 28) return fallback()
        literal = value
      }
      else if (type === "bit" && /^(true|false|0|1)$/i.test(value)) literal = postgres ? (/^(true|1)$/i.test(value) ? "TRUE" : "FALSE") : (/^(true|1)$/i.test(value) ? "1" : "0")
      else return fallback()
      values.set(name, literal)
    }
    // Charge original SQL plus every occurrence's fully escaped literal before
    // returning it to the formatter. Counting placeholders twice is conservative.
    let expandedCharacters = sql.length
    const params = new Proxy(Object.create(null) as Record<string, string>, { get: (_target, key) => {
      const value = typeof key === "string" ? values.get(key.toLowerCase()) : undefined
      if (value === undefined) throw new Error("Unresolved SQL parameter")
      expandedCharacters += value.length
      if (expandedCharacters > PREVIEW_CHARACTER_BUDGET) throw new Error("SQL preview character budget exceeded")
      return value
    } })
    const rendered = format(sql, { language: postgres ? "postgresql" : "transactsql", params, paramTypes: { named: ["@"] } })
    // Secondary guard for formatting whitespace; repeated literals are bounded above.
    return rendered.length <= PREVIEW_CHARACTER_BUDGET ? { sql: rendered } : fallback()
  } catch { return fallback() }
}
