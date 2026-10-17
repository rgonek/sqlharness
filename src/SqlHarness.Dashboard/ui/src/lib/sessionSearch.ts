import type { OperationDetailSearch, SessionSearch } from "@/router"

export function operationReturnSearch(sessionId: number, search: SessionSearch): OperationDetailSearch {
  return {
    returnSession: sessionId,
    returnSubject: search.subject,
    returnDb: search.db,
    returnGroup: search.group,
    returnScope: search.scope,
    returnA: search.a,
    returnB: search.b,
    returnCase: search.case,
    returnAgg: search.agg,
    returnUnassigned: search.unassigned,
  }
}

export function sessionSearchFromOperation(search: OperationDetailSearch): SessionSearch {
  return {
    subject: search.returnSubject,
    db: search.returnDb,
    group: search.returnGroup,
    scope: search.returnScope,
    a: search.returnA,
    b: search.returnB,
    case: search.returnCase,
    agg: search.returnAgg,
    unassigned: search.returnUnassigned,
  }
}
