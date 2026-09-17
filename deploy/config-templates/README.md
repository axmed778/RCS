# Configuration templates

Templates only. **Real values never enter source control** (SECURITY.md invariant 16).

| Template | Copy to | Readable by |
|---|---|---|
| `appsettings.Production.example.json` | the application directory as `appsettings.Production.json` | the service account (not secret) |
| `rcs-runtime.secrets.example.json` | outside the source and application trees, mode `0600` (Windows: ACL for the service account only). Point `RCS_SECRETS_FILE` at it in the **service** environment | the service account only |
| `rcs-migration.secrets.example.json` | same rules, but only for the **deployment** account running `Rcs.Web migrate`. Never in the service's environment | the deployment account only |

`RCS_SECRETS_FILE` is loaded after every other source, so its values win. The runtime and migration credentials
are separate on purpose: the running application must never be able to change the schema (ADR-004, ADR-018).
