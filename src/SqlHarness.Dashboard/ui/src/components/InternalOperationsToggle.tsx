export function InternalOperationsToggle({ showInternal, onChange }: { showInternal: boolean; onChange: (show: boolean) => void }) {
  return <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={showInternal} onChange={event => onChange(event.target.checked)} />Show internal operations</label>
}
