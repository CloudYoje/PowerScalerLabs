# HealthScale 1.1.1 companion boundary

HealthScale is bundled with PowerScaler Labs as a **sealed companion**, not as merged runtime code.

PowerScaler Labs may:

- build the sealed HealthScale source snapshot during Windows publishing;
- package `xinput_other.dll`, the default `HealthScale.ini`, and companion documentation;
- let the user select a DBXV2 installation;
- install only when no unknown `xinput_other.dll` would be overwritten;
- verify the installed DLL by SHA-256;
- uninstall only files proven to belong to the managed installation.

PowerScaler Labs may not:

- compile HealthScale source into the PowerScaler runtime or app assemblies;
- call HealthScale internals or share live memory state with it;
- modify the sealed source without regenerating and reviewing its integrity manifests;
- overwrite an unrecognized `xinput_other.dll`;
- modify the game folder while `DBXV2.exe` is running.

The reviewed HealthScale source snapshot is sealed by `UPSTREAM_SOURCE_SHA256SUMS.txt`. The Windows publisher builds it as a separate Visual C++ solution and stages the payload under `artifacts\PowerScalerLabs\Companions\HealthScale\Payload`.


HealthScale has no hard DBXV2 executable-version or XV2 Patcher image-size dependency. Version strings are logged only for diagnostics; live hook signatures and structural validation remain the activation boundary.
