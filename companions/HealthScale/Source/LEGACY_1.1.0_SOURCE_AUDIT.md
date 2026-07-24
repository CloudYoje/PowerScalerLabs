# HealthScale 1.1.0 Source Audit

## Result

**PASS**

- Original uploaded 1.0.1 archive SHA-256: `154449a30442a3a549d657a8983b0803fac03756c29099c670fe8eb3cbba8420`
- Candidate source files: `30`
- Candidate source size: `306,212` bytes
- Compiled DLL included: `NO`
- Original frozen archive modified: `NO`

## Source checks

- mixed domain ratio bound present: `PASS`
- target rebase rule present: `PASS`
- quest lane setting present: `PASS`
- dynamic lane registry present: `PASS`
- dynamic lane transition state present: `PASS`
- version 1 1 0: `PASS`
- no compiled outputs: `PASS`

## Validation performed

- C++20 model tests: `PASS`
- AddressSanitizer + UndefinedBehaviorSanitizer model tests: `PASS`
- Six native translation units, Clang C++20 syntax check with warnings: `PASS`
- Visual Studio project file-reference audit: `PASS`
- Source package binary purge: `PASS`

## Limits

- No MSVC/Windows SDK link was possible in this Linux environment.
- No live Xenoverse 2 runtime test was possible here.
