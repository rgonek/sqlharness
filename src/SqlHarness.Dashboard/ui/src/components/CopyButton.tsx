import { useEffect, useRef, useState } from "react"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Textarea } from "@/components/ui/textarea"
import { HugeiconsIcon } from "@hugeicons/react";
import { Tick02Icon, Copy01Icon } from "@hugeicons/core-free-icons";

export function CopyButton({ label, getText, showLabel = false, disabled = false, title, variant = "ghost", scopeKey }: {
  label: string
  getText: () => string | Promise<string>
  showLabel?: boolean
  disabled?: boolean
  title?: string
  variant?: "ghost" | "outline"
  scopeKey?: string
}) {
  const [copied, setCopied] = useState(false)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [manualText, setManualText] = useState<string | null>(null)
  const [scope, setScope] = useState({ label, scopeKey })
  const textarea = useRef<HTMLTextAreaElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const busy = useRef(false)
  const scopeVersion = useRef(0)
  const payload = label.replace(/^copy\s*/i, "")

  if (scope.label !== label || scope.scopeKey !== scopeKey) {
    setScope({ label, scopeKey })
    setCopied(false)
    setError(null)
    setManualText(null)
  }

  useEffect(() => {
    scopeVersion.current++
  }, [label, scopeKey])

  useEffect(() => {
    if (manualText === null) return
    const timeout = window.setTimeout(() => {
      textarea.current?.focus()
      textarea.current?.select()
    }, 0)
    return () => window.clearTimeout(timeout)
  }, [manualText])

  useEffect(() => {
    if (!copied) return
    const timeout = window.setTimeout(() => setCopied(false), 2_000)
    return () => window.clearTimeout(timeout)
  }, [copied])

  async function copy() {
    if (busy.current) return
    busy.current = true
    setPending(true)
    setCopied(false)
    setError(null)
    const version = scopeVersion.current
    try {
      const text = await getText()
      try {
        await navigator.clipboard.writeText(text)
        if (version === scopeVersion.current) setCopied(true)
      } catch {
        if (version === scopeVersion.current) setManualText(text)
      }
    } catch {
      if (version === scopeVersion.current) setError(`Could not prepare ${payload || "text"}. Try again.`)
    } finally {
      busy.current = false
      setPending(false)
    }
  }

  return <>
    <Button ref={trigger} type="button" variant={variant} size={showLabel ? "sm" : "icon"} className={showLabel ? "h-auto min-h-6 max-w-full whitespace-normal text-left" : undefined} disabled={disabled || pending} aria-busy={pending || undefined} aria-label={label} title={title ?? label} onClick={() => void copy()}>
      {copied ? <HugeiconsIcon icon={Tick02Icon} strokeWidth={2} aria-hidden="true" /> : <HugeiconsIcon icon={Copy01Icon} strokeWidth={2} aria-hidden="true" />}
      {showLabel && <span className="min-w-0 [overflow-wrap:anywhere]">{label}</span>}
    </Button>
    <span role="status" className="sr-only">{pending ? `Preparing ${payload || "text"}…` : copied ? `Copied${payload ? ` ${payload}` : ""}` : ""}</span>
    {error && <span role="alert" className="text-sm text-destructive">{error}</span>}
    <Dialog open={manualText !== null} onOpenChange={open => { if (!open) setManualText(null) }}>
      <DialogContent finalFocus={trigger}>
        <DialogHeader><DialogTitle>Copy manually</DialogTitle>
          <DialogDescription>Clipboard access was unavailable. Copy the selected {payload || "text"} using your device's copy command.</DialogDescription>
        </DialogHeader>
        <Textarea ref={textarea} aria-label="Text to copy" readOnly value={manualText ?? ""} onFocus={event => event.currentTarget.select()} className="min-h-40 w-full rounded border bg-background p-2 font-mono text-sm" />
      </DialogContent>
    </Dialog>
  </>
}
