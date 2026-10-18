import { Button } from "@/components/ui/button"

export type SafetyNotice = { operationId: number; notices: string[] }

export function SafetyNoticeToast({ enabled, notices }: { enabled: boolean; notices: SafetyNotice[] }) {
  if (!enabled || notices.length === 0) return null
  return (
    <div aria-live="polite" className="fixed right-4 bottom-4 z-40 flex w-[min(24rem,calc(100vw-2rem))] flex-col gap-2">
      {notices.slice(0, 3).map(item => (
        <section key={item.operationId} className="rounded-xl border bg-card p-4 text-card-foreground shadow-lg" aria-label="Session safety notice">
          <div className="font-medium">SQL session safety notice</div>
          <ul className="mt-2 list-disc pl-5 text-sm">{item.notices.map(notice => <li key={notice}>{notice}</li>)}</ul>
          <Button asChild className="mt-3" size="sm" variant="outline">
            <a href={`/operations/${item.operationId}`}>View operation</a>
          </Button>
        </section>
      ))}
    </div>
  )
}
