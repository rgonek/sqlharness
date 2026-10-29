import type { AgentSessionKey } from "@/api/types"
import type { OperationDetailSearch, SessionSearch } from "@/router"

export function operationReturnSearch(sessionId: number, search: SessionSearch, agentSessionKey?: AgentSessionKey): OperationDetailSearch {
  return {
    returnSession: sessionId,
    returnAgentKind: agentSessionKey?.agentKind as OperationDetailSearch["returnAgentKind"],
    returnAgentId: agentSessionKey?.conversationId,
    returnSubject: search.subject,
    returnDb: search.db,
    returnGroup: search.group,
    returnGroupB: search.groupB,
    returnScope: search.scope,
    returnA: search.a,
    returnB: search.b,
    returnCase: search.case,
    returnAgg: search.agg,
    returnView: search.view,
    returnUnassigned: search.unassigned,
  }
}

export function sessionSearchFromOperation(search: OperationDetailSearch): SessionSearch {
  return {
    subject: search.returnSubject,
    db: search.returnDb,
    group: search.returnGroup,
    groupB: search.returnGroupB,
    scope: search.returnScope,
    a: search.returnA,
    b: search.returnB,
    case: search.returnCase,
    agg: search.returnAgg,
    view: search.returnView,
    unassigned: search.returnUnassigned,
  }
}
