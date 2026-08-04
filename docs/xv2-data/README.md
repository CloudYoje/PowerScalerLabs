# Xenoverse 2 Local Data Indexes

Generated on 2026-08-04 from the loose data under:

`C:\Games\SteamLIBRARY\steamapps\common\DB Xenoverse 2\data`

The installation is modded. These files are local research snapshots, not canonical vanilla ID registries.

| File | Source | Rows |
|---|---|---:|
| `local-character-index.csv` | `char_model_spec.cms.xml` | 258 |
| `local-character-presets.csv` | `custom_skill.cus.xml` skill sets | 619 |
| `local-skill-index.csv` | `custom_skill.cus.xml` skill catalogs | 1,130 |
| `local-item-index.csv` | ten `system/item/*.idb` tables converted with `genser` 4.2 | 4,150 |

Display names come from comments emitted/preserved by community tooling. Treat them as annotations. Use the domain/table plus numeric ID as identity, and preserve the game build and source-file hash when promoting data into application logic.
