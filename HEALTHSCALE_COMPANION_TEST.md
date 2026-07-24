# HealthScale Companion 1 Windows test

1. Extract to a fresh folder and run `START_HERE.cmd` from a Visual Studio Developer Command Prompt with .NET 8 and the C++ desktop workload installed.
2. Confirm the published HealthScale payload exists under `artifacts\PowerScalerLabs\Companions\HealthScale\Payload`.
3. Launch PowerScaler Labs and open **Companion Apps**.
4. Select the DB Xenoverse 2 folder or its `bin` folder.
5. With DBXV2 closed, choose **Install / Adopt** and confirm:
   - `xinput_other.dll` appears in `bin`;
   - a missing `HealthScale.ini` is installed;
   - an existing INI is not overwritten;
   - status becomes **Installed · Verified**.
6. Launch DBXV2 and confirm install/uninstall controls are locked.
7. Close DBXV2 and choose **Verify**.
8. Replace or modify `xinput_other.dll` temporarily and confirm PowerScaler reports a conflict and refuses removal.
9. Restore/reinstall the correct DLL, edit `HealthScale.ini`, then uninstall. Confirm the DLL is removed and the modified INI is preserved.
10. Place an unrelated `xinput_other.dll` in a clean test folder containing a copied `DBXV2.exe`; confirm install refuses to overwrite it.
11. Run DBXV2 with the verified companion and execute the HealthScale quest/transformation test plan bundled in the companion documentation.
