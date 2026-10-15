/** Geometric H mark and two-tone wordmark shared by dashboard navigation. */
export function BrandLogo() {
  return (
    <span className="inline-flex items-center gap-2 whitespace-nowrap">
      <svg aria-hidden="true" viewBox="0 0 64 64" className="size-8 shrink-0 text-foreground">
        <path fill="currentColor" d="M11 5 25 13v10l-5 2v14l5 2v10l-14 8c-1.5.9-3-.2-3-2V8c0-1.2 1.4-2.6 3-3Z" />
        <path fill="currentColor" d="M53 5 39 13v10l5 2v14l-5 2v10l14 8c1.5.9 3-.2 3-2V8c0-1.2-1.4-2.6-3-3Z" />
        <path fill="#1765FF" d="m20 27 4-1c5 1.6 11 1.6 16 0l4 1v10l-4 1c-5-1.6-11-1.6-16 0l-4-1Z" />
      </svg>
      <span className="hidden text-lg font-semibold tracking-tight md:inline">
        <span className="text-foreground">SQL</span><span className="text-[#1765FF]">Harness</span>
      </span>
    </span>
  )
}
