/** A route `$id` as a journal row id: plain decimal digits only (no hex, exponent, sign or spaces), else null. */
export function parseRouteId(raw: string): number | null {
  if (!/^\d+$/.test(raw)) return null
  const id = Number(raw)
  return Number.isSafeInteger(id) ? id : null
}
