# StrictMode Publish Hotfix 1 Audit

## Failure reproduced from target-machine log

The Windows publish stopped before restore/build in `Deep-Audit-DeclutterChronology.ps1` with:

```text
The property 'Count' cannot be found on this object.
FullyQualifiedErrorId: PropertyNotFoundStrict
```

The failure occurred in the generated-binary exclusion check. Under Windows PowerShell 5.1, a pipeline that returns exactly one `FileInfo` is scalar-unwrapped. With `Set-StrictMode -Version Latest`, that scalar does not expose the collection-only `.Count` property expected by the audit.

## Bounded change

Only the audit collection materialization was changed:

```powershell
$sourceBinaries = @(
    Get-ChildItem ... | Where-Object { ... }
)
```

The array subexpression operator guarantees zero, one, or many results all expose `.Count`. Scanner, candidate, chronology, persistence, application, protocol, and runtime code were not changed.

## Deep audit between changes

- Confirmed the uploaded log failed before any dotnet restore, build, or publish command.
- Searched all PowerShell scripts for `.Count` access. The failed `Get-ChildItem` pipeline was the only scalar-unwrapping risk.
- The other `.Count` use is on `System.Text.RegularExpressions.MatchCollection`, which always exposes `.Count`.
- Confirmed the audit still rejects generated `.dll`, `.exe`, `.pdb`, `.obj`, and `.lib` files under `src`.
- Confirmed the read-only scanner and HealthScaler ownership boundaries are unchanged.
- Recomputed the package SHA-256 manifest after the hotfix.
- Verified every manifest entry against package bytes after repackaging.

## Runtime impact

None. This change affects only the pre-build PowerShell audit. It adds no scanner work, allocations, IPC traffic, disk recording, UI behavior, game hooks, or game-process access.

## Remaining target-machine gate

Run `START_HERE.cmd`. The publish should now pass the strict-mode collection check and continue into restore, build, and publish. Return the new `logs\publish.log` for the next audit.
