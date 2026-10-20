import { useId } from "react"
import { Checkbox } from "@/components/ui/checkbox"
import { Label } from "@/components/ui/label"

export function InternalOperationsToggle({ showInternal, onChange }: { showInternal: boolean; onChange: (show: boolean) => void }) {
  const id = useId()
  return <div className="flex items-center gap-2 text-sm">
    <Checkbox id={id} checked={showInternal} onCheckedChange={onChange} />
    <Label htmlFor={id}>Show internal operations</Label>
  </div>
}
