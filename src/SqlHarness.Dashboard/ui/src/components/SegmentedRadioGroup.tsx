import { useId } from "react"
import { cn } from "cn"
import { Label } from "@/components/ui/label"
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group"

/** A single-choice radio group presented as joined, labelled segments. */
export function SegmentedRadioGroup({ label, value, onValueChange, options, className }: {
  label: string
  value: string
  onValueChange: (value: string) => void
  options: { value: string; label: string }[]
  className?: string
}) {
  const id = useId()
  return <RadioGroup aria-label={label} value={value} onValueChange={onValueChange} className={cn("flex w-fit max-w-full flex-wrap gap-0", className)}>
    {options.map((option, index) => <div key={option.value} className="relative min-w-0 max-w-full border border-input first:rounded-l-lg last:rounded-r-lg [&+div]:border-l-0 has-[[aria-checked=true]]:bg-muted has-[:focus-visible]:ring-3 has-[:focus-visible]:ring-ring/50">
      <RadioGroupItem id={`${id}-${index}`} value={option.value} className="absolute inset-0 size-full aspect-auto opacity-0 after:inset-0" />
      <Label htmlFor={`${id}-${index}`} className="cursor-pointer break-words px-2.5 py-1.5">{option.label}</Label>
    </div>)}
  </RadioGroup>
}
