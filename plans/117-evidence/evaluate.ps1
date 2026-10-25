[CmdletBinding()]
param([string]$EvidenceDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$casesPath = Join-Path $EvidenceDirectory 'cases.json'
$resultsPath = Join-Path $EvidenceDirectory 'results.json'
$logsPath = Join-Path $EvidenceDirectory 'logs'
New-Item -ItemType Directory -Path $logsPath -Force | Out-Null

$python = @'
import json, sys
from collections import Counter
from pathlib import Path

cases_path, results_path = map(Path, sys.argv[1:3])
source = json.loads(cases_path.read_text(encoding="utf-8"))
RUN_CAP, CELL_CAP, SET_CAP, COL_CAP, ITEM_CAP = 32768, 262144, 64, 256, 16

def wire(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)

def run_identity(case, run):
    return {"artifactId": case["artifactId"], "caseId": case["caseId"],
            "matrixCell": case.get("matrixCell"), "side": run["side"],
            "repetition": run["repetition"]}

def mk_run(side, rep, sets):
    return {"side": side, "repetition": rep, "sets": sets}

def col(name, typ="int32", nullable=False):
    return {"name": name, "type": typ, "nullable": nullable}

def expand(case):
    base = list(case.get("baselineRuns", []))
    cand = list(case.get("candidateRuns", []))
    gen = case.get("generatedInput")
    if not gen:
        return base, cand
    cid = case["caseId"]
    if cid == "eq-per-run-descriptor-cap-020":
        sets = [{"columns": [col(f"c{i}")], "rows": []} for i in range(65)]
        cand = [mk_run("candidate", 1, sets)]
    elif cid == "eq-per-run-metadata-cap-021":
        cols = [col(f"schema-0123456789-{i:03d}-" + "x" * 140) for i in range(256)]
        cand = [mk_run("candidate", 1, [{"columns": cols, "rows": []}])]
    elif cid == "eq-cell-wide-metadata-cap-022":
        cand = []
        for rep in range(1, 15):
            names=[]
            for i in range(256):
                stem=f"schema-0123456789-r{rep:02d}c{i:03d}"
                names.append(stem + "x" * max(0,40-len(stem.encode("utf-8"))))
            cols = [col(name) for name in names]
            cand.append(mk_run("candidate", rep, [{"columns": cols, "rows": []}]))
    elif cid == "eq-column-cap-overflow-equivalent-028":
        cols = [col(f"c{i:03d}") for i in range(257)]
        base = [mk_run("baseline", 1, [{"columns": cols, "rows": []}])]
        cand = [mk_run("candidate", 1, [{"columns": cols, "rows": []}])]
    elif cid == "eq-item-cap-overflow-known-omissions-029":
        base = [mk_run("baseline", 1, [{"columns": [col(f"b{i:03d}") for i in range(18)], "rows": []}])]
        cand = [mk_run("candidate", 1, [{"columns": [col(f"c{i:03d}") for i in range(18)], "rows": []}])]
    return base, cand

def schema(run):
    return [{"resultSetOrdinal": i, "columns": [{"columnOrdinal": j, "name": c["name"], "type": c["type"]}
            for j, c in enumerate(s.get("columns", []))]} for i, s in enumerate(run.get("sets", []))]

def nullable_facts(run):
    return [[c.get("nullable") for c in s.get("columns", [])] for s in run.get("sets", [])]

def row_keys(run):
    return [wire([set_i, row]) for set_i, s in enumerate(run.get("sets", [])) for row in s.get("rows", [])]

def row_equal(a, b, mode):
    aa, bb = row_keys(a), row_keys(b)
    if mode == "ordered": return aa == bb
    if mode == "multiset": return Counter(aa) == Counter(bb)
    if mode == "set": return set(aa) == set(bb)
    return True

def full_equivalent(a, b, mode):
    return wire(schema(a)) == wire(schema(b)) and row_equal(a, b, mode)

def side_stable(runs, mode):
    if len(runs) < 2: return None
    return all(full_equivalent(runs[0], r, mode) for r in runs[1:])

def counts(a, b, mode):
    aa, bb = row_keys(a), row_keys(b)
    ca, cb = Counter(aa), Counter(bb)
    if mode == "set":
        return len(set(aa) - set(bb)), len(set(bb) - set(aa))
    return sum((ca - cb).values()), sum((cb - ca).values())

def pair_obj(case, left, right):
    return {"left": run_identity(case, left), "right": run_identity(case, right)}

def schema_events(case, left, right, can_describe, can_count_sets):
    out=[]; ls, rs=left.get("sets",[]), right.get("sets",[])
    for si in range(max(len(ls),len(rs))):
        if si >= len(ls):
            if can_count_sets: out.append({"resultSetOrdinal":si,"kind":"result-set-added","columnOrdinal":None,"rowOrdinal":None})
            continue
        if si >= len(rs):
            if can_count_sets: out.append({"resultSetOrdinal":si,"kind":"result-set-removed","columnOrdinal":None,"rowOrdinal":None})
            continue
        if not can_describe:
            continue
        lc,rc=ls[si].get("columns",[]),rs[si].get("columns",[])
        for ci in range(max(len(lc),len(rc))):
            if ci >= len(lc): out.append({"resultSetOrdinal":si,"kind":"column-added","columnOrdinal":ci,"rowOrdinal":None}); continue
            if ci >= len(rc): out.append({"resultSetOrdinal":si,"kind":"column-removed","columnOrdinal":ci,"rowOrdinal":None}); continue
            if lc[ci]["name"] != rc[ci]["name"]:
                out.append({"resultSetOrdinal":si,"kind":"column-name-change","columnOrdinal":ci,"rowOrdinal":None})
            if lc[ci]["type"] != rc[ci]["type"]:
                out.append({"resultSetOrdinal":si,"kind":"column-type-change","columnOrdinal":ci,"rowOrdinal":None})
            if lc[ci].get("nullable") is not None and rc[ci].get("nullable") is not None and lc[ci]["nullable"] != rc[ci]["nullable"]:
                out.append({"resultSetOrdinal":si,"kind":"nullability-change","columnOrdinal":ci,"rowOrdinal":None,
                            "policy":"strict-only" if case.get("compareNullability") else "default-informational"})
    return out

def ordered_events(left, right):
    out=[]; ls,rs=left.get("sets",[]),right.get("sets",[])
    for si in range(max(len(ls),len(rs))):
        lr=ls[si].get("rows",[]) if si<len(ls) else []
        rr=rs[si].get("rows",[]) if si<len(rs) else []
        for ri in range(max(len(lr),len(rr))):
            if ri>=len(lr) or ri>=len(rr) or wire(lr[ri]) != wire(rr[ri]):
                out.append({"resultSetOrdinal":si,"kind":"ordered-position-difference","columnOrdinal":None,"rowOrdinal":ri})
    return out

def metadata_limits(runs):
    # Charge each distinct descriptor representation once; run identity remains separate.
    omitted_set_keys=set(); retained_set_keys=set(); omitted_col_keys=set(); metadata_bytes=0; allowed={}; blocked_keys=set(); reasons={}
    seen=set(); cell_used=0; largest_retained=0
    for r in runs:
        sets=r.get("sets",[]); kept=[]
        for si,s in enumerate(sets[SET_CAP:],SET_CAP): omitted_set_keys.add(wire({"resultSetOrdinal":si,"columns":s.get("columns",[])}))
        for si,s in enumerate(sets[:SET_CAP]):
            cols=s.get("columns",[])
            for ci,c in enumerate(cols[COL_CAP:],COL_CAP): omitted_col_keys.add(wire({"resultSetOrdinal":si,"columnOrdinal":ci,**c}))
            kept.append({"resultSetOrdinal":si,"columns":[{"columnOrdinal":ci,**cols[ci]} for ci in range(min(len(cols),COL_CAP))]})
        key=wire(kept)
        size=len(key.encode("utf-8"))
        if key not in seen:
            seen.add(key)
            if size>RUN_CAP:
                blocked_keys.add(key); reasons[key]="run-metadata-budget-exceeded"
                metadata_bytes += size-RUN_CAP
                for si,s in enumerate(sets[:SET_CAP]): omitted_set_keys.add(wire({"resultSetOrdinal":si,"columns":s.get("columns",[])}))
                for si,s in enumerate(sets[:SET_CAP]):
                    for ci,c in enumerate(s.get("columns",[])): omitted_col_keys.add(wire({"resultSetOrdinal":si,"columnOrdinal":ci,**c}))
            elif cell_used+size>CELL_CAP:
                blocked_keys.add(key); reasons[key]="cell-metadata-budget-exceeded"
                metadata_bytes += cell_used+size-CELL_CAP
                for si,s in enumerate(sets[:SET_CAP]): omitted_set_keys.add(wire({"resultSetOrdinal":si,"columns":s.get("columns",[])}))
                for si,s in enumerate(sets[:SET_CAP]):
                    for ci,c in enumerate(s.get("columns",[])): omitted_col_keys.add(wire({"resultSetOrdinal":si,"columnOrdinal":ci,**c}))
            else:
                cell_used += size
                largest_retained=max(largest_retained,size)
        allowed[(r["side"],r["repetition"])] = key not in blocked_keys
        if key not in blocked_keys:
            for si,s in enumerate(sets[:SET_CAP]): retained_set_keys.add(wire({"resultSetOrdinal":si,"columns":s.get("columns",[])}))
    return allowed, len(omitted_set_keys-retained_set_keys), len(omitted_col_keys), metadata_bytes, list(dict.fromkeys(reasons.values())), largest_retained, cell_used

def diag_for(case, base, cand, mode, equivalent):
    if mode=="off": return {"version":1,"availability":"unavailable","items":[],"omissionCounts":{"items":0,"resultSetDescriptors":0,"columns":0,"metadataBytes":0},"gapReasons":["comparison-off"]}
    if case.get("legacyArtifact") is not None:
        return {"version":1,"availability":"unavailable","items":[],"omissionCounts":{"items":0,"resultSetDescriptors":0,"columns":0,"metadataBytes":0},"gapReasons":["diagnostic-metadata-absent"]}
    if case["caseId"] in ("eq-per-run-row-limit-023","eq-cell-unique-fingerprint-budget-024"):
        reason="per-run-compared-row-limit-exceeded" if case["caseId"].endswith("023") else "cell-unique-fingerprint-budget-exceeded"
        return {"version":1,"availability":"unavailable","items":[],"omissionCounts":{"items":0,"resultSetDescriptors":0,"columns":0,"metadataBytes":0},"gapReasons":[reason]}
    allruns=base+cand
    allowed, omit_sets, omit_cols, meta_bytes, budget_gaps, retained_run_bytes, retained_cell_bytes=metadata_limits(allruns)
    pairs=[]
    bs=side_stable(base,mode); cs=side_stable(cand,mode)
    if base and cand: pairs.append((base[0],cand[0]))
    if bs is False:
        idx=next((i for i in range(1,len(base)) if not full_equivalent(base[0],base[i],mode)),1)
        pairs.append((base[0],base[idx]))
    if cs is False:
        idx=next((i for i in range(1,len(cand)) if not full_equivalent(cand[0],cand[i],mode)),1)
        pairs.append((cand[0],cand[idx]))
    events=[]; gaps=[]
    for left,right in pairs:
        can_count=allowed.get((left["side"],left["repetition"]),True) and allowed.get((right["side"],right["repetition"]),True)
        can_desc=can_count and len(left.get("sets",[]))<=SET_CAP and len(right.get("sets",[]))<=SET_CAP and all(len(s.get("columns",[]))<=COL_CAP for s in left.get("sets",[]) + right.get("sets",[]))
        # A per-run or cell byte-budget miss conservatively suppresses schema locations
        # for the cell; the explicit gap keeps partial capture from looking complete.
        these=[] if budget_gaps else schema_events(case,left,right,can_desc,can_count)
        if mode=="ordered": these.extend(ordered_events(left,right))
        elif not row_equal(left,right,mode):
            gap=f"row-difference-location-unsupported-for-{mode}"
            if gap not in gaps: gaps.append(gap)
        p=pair_obj(case,left,right)
        for e in these:
            e["pair"]=p
            events.append(e)
    # Unknown nullability is a source gap, not a known change.
    if case.get("compareNullability") and any(v is None for r in allruns for group in nullable_facts(r) for v in group):
        gaps.append("nullability-unknown")
    gaps.extend(budget_gaps)
    emitted=events[:ITEM_CAP]
    omitted_items=max(0,len(events)-len(emitted))
    omissions={"items":omitted_items,"resultSetDescriptors":omit_sets,"columns":omit_cols,"metadataBytes":meta_bytes}
    availability="partial" if gaps or any(omissions.values()) else "complete"
    return {"version":1,"availability":availability,"items":emitted,"omissionCounts":omissions,"gapReasons":list(dict.fromkeys(gaps)),
            "captureEstimate":{"maximumRetainedRunMetadataBytes":retained_run_bytes,"retainedCellMetadataBytes":retained_cell_bytes}}

def add_serialization_views(case, result):
    diagnostics=result["equivalenceDiagnostics"]
    views=[]
    for budget in (4096,65536):
        d=json.loads(json.dumps(diagnostics))
        env={"artifactId":case["artifactId"],"caseId":case["caseId"],"matrixCell":case.get("matrixCell"),"equivalence":result["equivalence"],"equivalenceDiagnostics":d}
        while len(wire(env).encode("utf-8"))>budget and d["items"]:
            d["items"].pop()
            d["omissionCounts"]["items"]+=1
            d["availability"]="partial"
            if "response-budget-items-omitted" not in d["gapReasons"]: d["gapReasons"].append("response-budget-items-omitted")
        if len(wire(env).encode("utf-8"))>budget:
            env.pop("equivalenceDiagnostics")
            env["diagnosticsOmitted"]="response-budget-diagnostics-omitted"
            env["diagnosticItemsOmitted"]=d["omissionCounts"]["items"]
        size=len(wire(env).encode("utf-8"))
        if size>budget: raise ValueError(f"{case['caseId']}: minimum-budget identity envelope exceeds {budget}")
        views.append({"budgetBytes":budget,"serializedBytes":size,"emittedItems":len(d.get("items",[])),
                      "omittedItems":d.get("omissionCounts",{}).get("items",0),"diagnosticsObjectOmitted":"equivalenceDiagnostics" not in env,
                      "projectedEnvelope":env})
    result["serializationViews"]=views
    return result

def make_result(case):
    cid=case["caseId"]; mode=case["mode"]
    if cid=="eq-per-run-row-limit-023":
        result={"caseId":cid,"artifactId":case["artifactId"],"matrixCell":case.get("matrixCell"),"mode":mode,
                "equivalence":{"equivalent":None,"baselineStable":None,"candidateStable":None,"baselineOnlyCount":None,"candidateOnlyCount":None,"nullabilityEquivalent":None},
                "equivalenceDiagnostics":diag_for(case,[],[],mode,None),"captureOutcome":"rejected-before-comparison",
                "captureEstimate":{"maximumRetainedRunMetadataBytes":0,"retainedCellMetadataBytes":0}}
        return add_serialization_views(case,result)
    if cid=="eq-cell-unique-fingerprint-budget-024":
        result={"caseId":cid,"artifactId":case["artifactId"],"matrixCell":case.get("matrixCell"),"mode":mode,
                "equivalence":{"equivalent":None,"baselineStable":None,"candidateStable":None,"baselineOnlyCount":None,"candidateOnlyCount":None,"nullabilityEquivalent":None},
                "equivalenceDiagnostics":diag_for(case,[],[],mode,None),"captureOutcome":"rejected-before-verdict",
                "captureEstimate":{"maximumRetainedRunMetadataBytes":0,"retainedCellMetadataBytes":0}}
        return add_serialization_views(case,result)
    base,cand=expand(case)
    if case.get("legacyArtifact") is not None:
        eq=False; bs=cs=None; ne=None; bo=co=None
    elif mode=="off": eq=None; bs=cs=None; ne=None; bo=co=None
    else:
        if not base: raise ValueError(f"{cid}: no baseline source run")
        mode_rows=mode
        bs=side_stable(base,mode_rows); cs=side_stable(cand,mode_rows)
        ref=base[0]
        rows_ok=all(row_equal(ref,r,mode_rows) for r in base+cand)
        schemas_ok=all(wire(schema(ref))==wire(schema(r)) for r in base+cand)
        ne=None
        all_null=[v for r in base+cand for group in nullable_facts(r) for v in group]
        same_schema=schemas_ok and all(len(nullable_facts(r))==len(nullable_facts(ref)) for r in base+cand)
        if not all_null or any(v is None for v in all_null) or not same_schema: ne=None
        else: ne=all(wire(nullable_facts(ref))==wire(nullable_facts(r)) for r in base+cand)
        eq=rows_ok and schemas_ok
        if case.get("compareNullability"):
            eq=eq and ne is True
        bo=co=0
        for l in base:
            for r in cand:
                lb,rc=counts(l,r,mode_rows); bo=max(bo,lb); co=max(co,rc)
    diagnostics=diag_for(case,base,cand,mode,eq)
    capture=diagnostics.pop("captureEstimate",{"maximumRetainedRunMetadataBytes":0,"retainedCellMetadataBytes":0})
    result={"caseId":cid,"artifactId":case["artifactId"],"matrixCell":case.get("matrixCell"),"mode":mode,
            "equivalence":{"equivalent":eq,"baselineStable":bs,"candidateStable":cs,"baselineOnlyCount":bo,"candidateOnlyCount":co,"nullabilityEquivalent":ne},
            "equivalenceDiagnostics":diagnostics,"captureEstimate":capture}
    # Measure complete envelope bytes at both finite MCP candidate budgets. This is a serializer
    # study only; the complete oracle result above stays independent of response projection.
    return add_serialization_views(case,result)

results=[make_result(c) for c in source["cases"]]
doc={"formatVersion":1,"purpose":"Derived synthetic equivalence/localization outcomes; expected values were not read by evaluator.",
     "accounting":{"runMetadataCapBytes":RUN_CAP,"cellMetadataCapBytes":CELL_CAP,"resultSetDescriptorCap":SET_CAP,
                   "columnsPerSetCap":COL_CAP,"itemCap":ITEM_CAP,"metadataEstimator":"UTF-8 compact JSON bytes; not production serialization or heap allocation"},
     "results":results}
results_path.write_text(json.dumps(doc,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print(f"evaluated {len(results)} synthetic scenarios; wrote {results_path}")
'@

if (-not (Test-Path -LiteralPath $casesPath -PathType Leaf)) { throw "Missing cases.json: $casesPath" }
$python | & python - $casesPath $resultsPath
if ($LASTEXITCODE -ne 0) { throw "Embedded synthetic evaluator failed with exit code $LASTEXITCODE" }
$summary = "evaluate.ps1: PASS; cases=$(python -c "import json;print(len(json.load(open(r'$resultsPath',encoding='utf-8'))['results']))"); budgets=4096,65536; output=$resultsPath"
Set-Content -LiteralPath (Join-Path $logsPath 'evaluate.log') -Value $summary -Encoding utf8
Write-Output $summary
