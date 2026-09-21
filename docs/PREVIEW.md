# Document Preview operations

ADR-044 introduces optional isolated previews; originals remain immutable and indefinitely retained.

## Formats and offline dependencies

| Format | Output / limits |
| --- | --- |
| PDF | Poppler PNG pages and thumbnail; at most 150 pages by default |
| DOC / DOCX | LibreOffice print conversion to PDF, then Poppler |
| XLS / XLSX | LibreOffice PDF sheets, then Poppler; print settings affect pagination |
| PNG / JPEG / WebP | libvips normalized PNG/JPEG, orientation applied, metadata stripped |
| KMZ | bounded KML points, lines, polygons; first selected KML, no external resources/maps |
| ASCII DXF | bounded 2D geometry; unsupported entities may be skipped; not a CAD editor |
| DWG | UNSUPPORTED: no approved local converter |
| ArchiCAD | UNSUPPORTED: no confirmed mapping or approved renderer |
| Macro-enabled Office / other formats | UNSUPPORTED by policy or missing route; download remains available |

Install OS packages separately: LibreOffice Writer/Calc, poppler-utils, libvips-tools,
bubblewrap and util-linux (prlimit), plus fonts required by local documents. Tested on
Ubuntu 24.04 with LibreOffice 24.2.7.2, Poppler 24.02.0, libvips 8.15.1 and bubblewrap 0.9.0.
These binaries are not vendored. Assemble approved packages, fonts and licenses in the offline
release bundle. Conversion makes no network calls; missing fonts can change Office layout.
Password-protected, malformed, oversized and unsupported files do not damage originals.

## Configuration

`Rcs:Preview:Enabled` defaults false outside Development. Development uses
`../../.local/previews` and `../../.local/preview-tmp` from the web project directory.
Production must explicitly configure `StorageRoot` (for example `/srv/rcs/previews`)
and `TempRoot` (`/srv/rcs/preview-tmp`), distinct from each other and original storage/upload roots.
Paths resolve against the working directory. Keep all these directories private, on managed
storage; do not use symbolic links or put document storage under converter/runtime directories.

The default `Sandbox:Mode` is `Bubblewrap`. `Unisolated` is Development-only and provides
no namespace security. `Worker:Path` defaults to `preview-worker/Rcs.PreviewWorker.dll` beside
the host. `Worker:DotnetHost` normally resolves automatically. Build and publish include the
worker. Tools are absolute paths under `Rcs:Preview:Tools`; defaults are `/usr/bin` tools.
Capability probing executes inside the actual sandbox. Restart after upgrading converters.

Default execution limits: one worker, 240-second job wall time, 512 MiB managed heap,
512 MiB private tmpfs, 4 GiB address space per converter, 150-second converter timeout,
200 MiB preview input, 256 MiB total ingested output, 32 MiB per artifact, 1800-pixel long side.
Three automatic attempts use exponential backoff; users have three manual retries per generation.
Configure `Limits`, `MaxAttempts`, `RetryBackoffSeconds`, `ManualRetryLimit`,
`WorkerConcurrency` (1–4) and `PollIntervalSeconds` for the deployment's tested capacity.

## Isolation and remaining deployment hardening

Enforced now: separate process, scrubbed environment, no network namespace access,
read-only staged input, no original-store or database-socket mounts, private PID/IPC/mount
namespaces, read-only tools/runtime, converter CPU/address-space/file-size limits and a parent
wall timeout. The staged original is rehashed. Only validated derived artifact types are ingested.

The app's development identity launches bubblewrap. Before deployment configure a dedicated
preview identity and a narrowly scoped launcher, cgroup aggregate MemoryMax/TasksMax,
filesystem quotas for work/output, noexec/nosuid/nodev mounts where compatible, controlled
font/converter updates and service permissions. `LauncherPrefix` supports an administrator-owned
wrapper; do not grant broad sudo. Resource limits per process do not cap aggregate descendants or
all scratch files, so cgroups/quotas are required deployment hardening, not claimed here as enforced.
The preview user must receive access to that job's staging only; validate this with the actual
service account. The system's existing Review actor remains Development-only, not production auth.

## Schema, operations and recovery

Apply `dotnet Rcs.Web.dll migrate`, then `dotnet Rcs.Web.dll check-schema`; schema 15 is required.
Migration uses the migration connection; the running host uses runtime privileges only.
Start from `src/Rcs.Web` with `dotnet run --no-build -- --urls http://127.0.0.1:5082` in Development.

- `dotnet Rcs.Web.dll preview list`: latest current generations and failure codes.
- `dotnet Rcs.Web.dll preview verify`: size/hash verification of READY artifacts; exit 4 on findings.
- `dotnet Rcs.Web.dll preview retry <preview-id>`: retry FAILED with a new automatic attempt budget.
- `dotnet Rcs.Web.dll preview regenerate <version-id>`: supersede a settled generation and queue a new one.
- `dotnet Rcs.Web.dll preview run`: reconcile and drain currently due jobs; future backoff work stays pending.
- `dotnet Rcs.Web.dll verify-documents --rehash`: independent original-store integrity check.

Jobs are rows in `document_preview`, claimed using SKIP LOCKED and a lease longer than the worker timeout.
Expired leases are reclaimed up to the attempt limit, then FAILED. Completion locks and fences the token
before adding artifacts. Stale workers never delete shared generation directories. Crashes may leave
unreferenced derived files. Cleanup of superseded/orphaned preview generations is deferred; do not
run deletion against original storage. Monitor disk usage and inspect derived directories separately.

## HTTP and authorization

All routes start `/cases/{caseId}/documents/{linkId}/versions/{versionId}/preview`:
GET for metadata, GET `/status`, GET `/artifacts/{artifactId}`, POST `/retry` with antiforgery token.
The placement must expose exactly that version and its case must be visible now, matching download.
Wrong case, guessed version/artifact and inaccessible or removed placement yield 404.
Metadata/artifacts have private/no-store; artifacts additionally have nosniff, same-origin resource
policy and sandbox CSP. No storage keys or raw paths are returned. Original download remains an
attachment. Preview opening is audited; polling status does not create content-view audit events.
