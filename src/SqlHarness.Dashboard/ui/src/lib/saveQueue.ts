/** Runs saves one after another so a slower earlier save never lands after a later one. */
export function createSaveQueue<T>(save: (value: T) => Promise<unknown>) {
  let tail: Promise<unknown> = Promise.resolve()
  return {
    enqueue(value: T): Promise<void> {
      const run = tail.then(() => save(value))
      tail = run.catch(() => undefined)
      return run.then(() => undefined)
    },
  }
}
