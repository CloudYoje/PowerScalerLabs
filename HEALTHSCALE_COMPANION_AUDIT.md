# HealthScale Companion 1 audit

## Decision

HealthScale 1.1.1 is added to PowerScaler Labs as a **sealed companion product**. Its native source remains an independent Visual C++ solution under `companions/HealthScale/Source`; it is not referenced by `PowerScalerLabs.sln`, compiled into either .NET executable, or used as a memory-access bridge.

## Desktop-app capabilities

The new **Companion Apps** page can:

- locate a DBXV2 installation by a running process, the default Steam path, or a user-selected folder;
- install or adopt the exact published `xinput_other.dll` payload;
- install the default `HealthScale.ini` only when one does not already exist;
- verify the installed DLL against the published SHA-256;
- create a local installation receipt under `%LOCALAPPDATA%\PowerScaler Labs\Companion Apps\HealthScale`;
- remove only a DLL whose path and hash still match that receipt;
- remove the default INI only when PowerScaler created it and it has not changed;
- preserve modified or pre-existing configuration.

## Fail-closed rules

- No install or uninstall while `DBXV2.exe` is running.
- No overwrite of an unrecognized `xinput_other.dll`.
- No uninstall without a matching managed receipt.
- No uninstall after the managed DLL changes.
- File copies are staged through a temporary file before replacement.
- The PowerScaler external runtime has no file-management path and remains query/VM-read only.

## Frozen source proof

`UPSTREAM_SOURCE_SHA256SUMS.txt` records every file supplied in the uploaded HealthScale 1.1.1 source package. `Deep-Audit-HealthScaleCompanion.ps1` checks each file before publishing. The publisher builds that independent solution and stages only its output DLL, default INI, and review documentation.

## Windows publish output

```text
artifacts\PowerScalerLabs\PowerScalerLabs.exe
artifacts\PowerScalerLabs\Runtime\PowerScalerLabs.Runtime.exe
artifacts\PowerScalerLabs\Companions\HealthScale\Payload\xinput_other.dll
artifacts\PowerScalerLabs\Companions\HealthScale\Payload\HealthScale.ini
artifacts\PowerScalerLabs\Companions\HealthScale\Payload\payload.sha256
```

## Validation performed in this packaging environment

- copied HealthScale source matched the uploaded source byte-for-byte;
- XAML parsed as XML;
- all new XAML event handlers were found in code-behind;
- source-manifest verification was reproduced independently;
- PowerScaler runtime source contains no HealthScale integration;
- package/audit invariants were statically checked.

This Linux environment does not contain .NET SDK, MSBuild, Visual Studio C++, WPF, or DBXV2. Windows compilation and live install/verify/uninstall testing remain required.
