const MAX_CASE_LABEL_LENGTH = 48

export function displayCaseLabel(label: string): string {
  if (label.length <= MAX_CASE_LABEL_LENGTH) return label
  const edgeLength = Math.floor((MAX_CASE_LABEL_LENGTH - 1) / 2)
  return `${label.slice(0, edgeLength)}…${label.slice(-edgeLength)}`
}
