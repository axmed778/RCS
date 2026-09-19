# Review MVP presentation redesign

Validated on 18 September 2026. This is a product-owner Review build, not a production release.

The starting checkout is `feature/review-mvp`, commit `e0e01e8305af026ce71ab8645dd76a5a1bc75b29`.
GitHub's current `main` (`8d6f0f9a8333733676c67e6fad7e85e744df4073`) merges that feature; its file tree is identical.

The new desktop shell uses local CSS, the existing Azerbaijani resource catalogue, a permanent sidebar,
breadcrumbs, a link to the case-list filter, and the existing Review actor and synthetic-data banner.
Home, case list, organizations and existing forms share the dark theme.

The case canvas is a read-only projection of the existing recursive Razor tree. Vanilla JavaScript creates
selectable HTML nodes and moves the original detail elements into the inspector. It does not recreate forms,
tokens, permissions or handlers. An SVG layer connects actual parent/child elements and recalculates on resize,
zoom and collapse. Deep dependency chains use a horizontal continuation to avoid an unreadably tall column.

Selection is reflected in the URL fragment, including the existing action redirects. The inspector contains
the original details/actions, links to related entities, and history filtered by entity ID. Case history shows
all events in the case. With JavaScript disabled, the original detailed server-rendered tree and forms remain
available. Branch collapse and zoom are temporary view state.

`Ui/WorkflowAppearance.cs` is presentation code only: green approved, amber conditional, red rejected,
grey waiting/neutral. It does not redefine progress or closure. Superseded/void answers do not determine a
request's colour; conflicting conclusive answers are explicitly labelled; informational replies are not
mislabelled as waiting for a response. Overdue status remains separate from response outcome.

No domain, application service, infrastructure, SQL, migration, seed, authorization, audit, configuration,
route or page-handler behavior changed. No dependencies, framework, CDN, fonts or telemetry were added.

## Validation

- Original baseline: 102 unit tests and 69 integration-project tests passed.
- Final full solution: 102 unit tests and 85 integration-project tests passed, zero failures or skips.
- The 16 additional tests cover outcome presentation, inactive/conflicting replies, inspector-source entity IDs,
  antiforgery protection, Start/Close form submission and fragment redirects, validation and case-create idempotency.
- Build succeeded without warnings or errors.
- Browser checks: home, case list/filter/empty result, organizations, new organization, new case,
  parallel and nested cases, selected details/history, linked entities, collapse, fit/zoom, and response form.
- Desktop canvas checked at 1920×1080 and 1440×900; connectors remain aligned. No browser errors observed.
- Existing synthetic Review records were not changed by browser checks. Mutation tests use disposable test databases.

## Run in the existing WSL checkout

```sh
cd "$RCS_REPO/src/Rcs.Web"
DOTNET_ENVIRONMENT=Development DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  dotnet run --no-build -- --urls http://127.0.0.1:5080
```

Open <http://localhost:5080/cases>. Use **2026/0001** for the parallel scenario and **2026/0002** for the nested scenario.
Click the communication-map requirement to inspect its evidence and related child request.

Set `RCS_REPO` to the local checkout and put the .NET SDK on `PATH`. To rebuild and test,
provide `RCS_TEST_ADMIN_CONNECTION` through the local environment/secrets configuration
(the integration suite needs PostgreSQL 18 and the preview OS prerequisites in `PREVIEW.md`):

```sh
cd "$RCS_REPO"
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet test Rcs.sln --no-restore
```

## Review limitations

Large/deep graphs can require scrolling or **Ekrana sığdır**. Initial automatic scaling stops at 80% to preserve
legibility; explicit fit can shrink further. There is no minimap, drag-to-edit or persistent custom node positioning.
The existing data model has no document-upload UI, so no document counts or empty document tabs are invented.
Inspector width is fixed at desktop sizes; on narrow screens it stacks below the graph.
The seed has no rejected-response example; red outcome handling is covered by tests without changing demo scenarios.
Authentication and production readiness remain outside this Review MVP.

## Changed files

- `src/Rcs.Web/Pages/Shared/_Layout.cshtml`
- `src/Rcs.Web/Pages/Cases/Workspace.cshtml`
- `src/Rcs.Web/Pages/Shared/_RequestNode.cshtml`
- `src/Rcs.Web/Pages/Shared/_RequirementNode.cshtml`
- `src/Rcs.Web/Pages/Cases/Index.cshtml`
- `src/Rcs.Web/Pages/Organizations/Index.cshtml`
- `src/Rcs.Web/Resources/SharedResource.resx`
- `src/Rcs.Web/Ui/WorkflowAppearance.cs` (new, presentation only)
- `src/Rcs.Web/wwwroot/css/site.css`
- `src/Rcs.Web/wwwroot/js/workspace.js` (new)
- `tests/Rcs.IntegrationTests/Web/ReviewUiTests.cs`
- `tests/Rcs.IntegrationTests/Web/WorkflowAppearanceTests.cs` (new)
- `docs/REVIEW_UI.md` (this report)

## Document Preview

Sənədlər rows expose Ön baxış in the existing workspace. The in-RCS dialog preserves the dark workspace and offers close/Escape, fullscreen, page navigation, zoom and fit. It polls pending work and shows Azerbaijani ready, pending, failed, unsupported and retry states. PDF/Office use image pages, raster formats use normalized images, and KMZ/DXF use a local canvas with bounds and point/line/polygon geometry. No map tiles or CDN are requested. Limited page/geometry output is labeled. Original download remains available separately.
