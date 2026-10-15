import { useEffect, useRef, useState } from "react"
import { Check, Copy } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog"

export function CopyButton({ label, getText }: { label: string; getText: () => string | Promise<string> }) {
  const [copied, setCopied] = useState(false)
  const [manualText, setManualText] = useState<string | null>(null)
  const textarea = useRef<HTMLTextAreaElement>(null)

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
    let text: string | null = null
    try {
      text = await getText()
      await navigator.clipboard.writeText(text)
      setCopied(true)
    } catch {
      try {
        setManualText(text ?? await getText())
      } catch {
        setManualText("")
      }
    }
  }

  return <>
    <Button type="button" variant="ghost" size="icon" aria-label={label} title={label} onClick={() => void copy()}>
      {copied ? <Check aria-hidden="true" /> : <Copy aria-hidden="true" />}
    </Button>
    <span role="status" className="sr-only">{copied ? "Copied" : ""}</span>
    <Dialog open={manualText !== null} onOpenChange={open => { if (!open) setManualText(null) }}>
      <DialogContent>
        <DialogHeader><DialogTitle>Copy manually</DialogTitle></DialogHeader>
        <textarea ref={textarea} aria-label="Text to copy" readOnly value={manualText ?? ""} onFocus={event => event.currentTarget.select()} className="min-h-40 w-full rounded border bg-background p-2 font-mono text-xs" />
      </DialogContent>
    </Dialog>
  </>
}
